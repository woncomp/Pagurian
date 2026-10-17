#pragma warning disable GHCP001

using System.Collections.Concurrent;
using GitHub.Copilot.Rpc;
using Pagurian.Modules.Copilot;
using Pagurian.Sdk;

internal static class UsageServiceFixture
{
    private static int _checks;

    internal static async Task RunAsync(Logger log)
    {
        await HourlyRecycling(log);
        await LoginAndCoalescing(log);
        await FailedStartup(log);
        await FailedStop(log);
        await SlowStopAndSuccessfulStartAge(log);
        await CancellationAndQueuedPublication(log);
        await ResponseStates(log);
        await CycleBoundary(log);
        Console.WriteLine($"Copilot Usage service lifecycle: PASS ({_checks} assertions).");
    }

    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task HourlyRecycling(Logger log)
    {
        using var fixture = new Fixture(log);
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Used == 0 && fixture.Created.Count == 1, "initial quota");
        fixture.UpstreamUsed = 2325;
        fixture.Clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Used == 0 && fixture.Created.Count == 1, "reuse before threshold");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Used == 2325 && fixture.Created.Count == 2, "frozen zero recovered at one hour");
        Check(fixture.Service.State.Usage.PremiumInteractions!.PercentageText == "3%", "normalization");
        Check(fixture.Created[0].Disposed && !fixture.Created[0].Active, "old runtime drained");
        Check(fixture.Store.Read(fixture.Account).Cycles.Single().Days.Single().LastUsed == 2325,
            "fresh quota persisted");
        fixture.Clock.ShiftUtc(TimeSpan.FromDays(1));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 2, "wall clock change must not expire runtime");
        fixture.Clock.ShiftUtc(TimeSpan.FromDays(-1));
        fixture.Clock.Advance(TimeSpan.FromHours(4));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 3 && fixture.MaxActive == 1, "resume recycles only once");
    }

    private static async Task LoginAndCoalescing(Logger log)
    {
        using var fixture = new Fixture(log);
        await fixture.Service.RequestRefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        var entered = Signal();
        var release = Signal();
        fixture.Created[0].BeforeQuota = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var refresh = fixture.Service.RequestRefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(refresh, fixture.Service.RequestRefreshAsync()), "refresh coalesced");
        fixture.UpstreamUsed = 300;
        var login = fixture.Service.RequestLoginAsync();
        Check(ReferenceEquals(login, fixture.Service.RequestLoginAsync()), "login coalesced");
        Check(ReferenceEquals(login, fixture.Service.RequestRefreshAsync()), "refresh joins pending login");
        release.TrySetResult();
        await Task.WhenAll(refresh, login);
        Check(fixture.Logins == 1 && fixture.Created.Count == 2 && fixture.Used == 300,
            "login serialized and replaced runtime");
        Check(fixture.MaxActive == 1 && !fixture.Service.State.IsLoggingIn, "login finishes cleanly");
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 2, "login resets age window");
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 3, "hour since login expires runtime");
    }

    private static async Task FailedStartup(Logger log)
    {
        using var fixture = new Fixture(log);
        fixture.Configure = runtime => runtime.BeforeStart =
            _ => Task.FromException(new IOException("fixture startup failure"));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Service.State.Status == CopilotUsageStatus.Error, "startup error surfaced");
        Check(fixture.Created.Single().Disposed && fixture.Active == 0, "failed startup cleaned up");
        Check(!File.Exists(fixture.Store.PathFor(fixture.Account)), "startup failure creates no history");
        fixture.Configure = null;
        fixture.UpstreamUsed = 100;
        await fixture.Service.RequestRefreshAsync();
        var previous = fixture.Service.State;
        Check(previous.Status == CopilotUsageStatus.Ready && fixture.Created.Count == 2, "startup retry");
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Configure = runtime => runtime.BeforeStart =
            _ => Task.FromException(new IOException("fixture replacement failure"));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Service.State.Status == CopilotUsageStatus.Error &&
            fixture.Service.State.Usage == previous.Usage &&
            fixture.Service.State.RefreshedAt == previous.RefreshedAt,
            "replacement failure retains old values without marking fresh");
        Check(fixture.Store.Read(fixture.Account).Cycles.Single().Days.Single().LastUsed == 100,
            "failure does not append a synthetic zero");
        fixture.Configure = null;
        fixture.UpstreamUsed = 200;
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Used == 200 && fixture.Active == 1 && fixture.MaxActive == 1, "replacement retry");
    }

    private static async Task FailedStop(Logger log)
    {
        using var fixture = new Fixture(log);
        await fixture.Service.RequestRefreshAsync();
        var first = fixture.Created[0];
        first.BeforeStop = () => Task.FromException(new IOException("fixture graceful stop failure"));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Service.RequestRefreshAsync();
        Check(first.ForcedStops == 1 && first.Disposed && fixture.Created.Count == 2,
            "graceful failure uses forced cleanup");

        var second = fixture.Created[1];
        second.BeforeStop = () => Task.FromException(new IOException("fixture graceful stop failure"));
        second.BeforeForceStop = () => Task.FromException(new IOException("fixture forced stop failure"));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Service.State.Status == CopilotUsageStatus.Error &&
            fixture.Created.Count == 2 && second.Active && !second.Disposed,
            "failed cleanup prevents overlapping replacement");
        second.BeforeStop = null;
        second.BeforeForceStop = null;
        fixture.UpstreamUsed = 500;
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Used == 500 && second.Disposed && fixture.MaxActive == 1,
            "cleanup retries before creating another runtime");

        var third = fixture.Created[2];
        third.BeforeStop = () => Task.FromException(new IOException("fixture shutdown stop failure"));
        fixture.Service.Shutdown();
        Check(third.ForcedStops == 1 && third.Disposed && fixture.Active == 0, "shutdown forced cleanup");
    }

    private static async Task CancellationAndQueuedPublication(Logger log)
    {
        using (var fixture = new Fixture(log))
        {
            await fixture.Service.RequestRefreshAsync();
            var before = fixture.Service.State;
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            var entered = Signal();
            fixture.Configure = runtime => runtime.BeforeStart = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            var refresh = fixture.Service.RequestRefreshAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Service.Shutdown();
            await refresh;
            Check(fixture.Active == 0 && fixture.Created.All(runtime => runtime.Disposed),
                "cancellation during replacement cleans runtimes");
            Check(fixture.Service.State.RefreshedAt == before.RefreshedAt, "cancelled read never completes");
        }
        using (var fixture = new Fixture(log))
        {
            await fixture.Service.RequestRefreshAsync();
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            var entered = Signal();
            var release = Signal();
            var runtime = fixture.Created.Single();
            runtime.BeforeStop = () =>
            {
                entered.TrySetResult();
                return release.Task;
            };
            runtime.BeforeForceStop = () =>
            {
                release.TrySetResult();
                return Task.CompletedTask;
            };
            var refresh = fixture.Service.RequestRefreshAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Service.Shutdown();
            await refresh;
            Check(runtime.ForcedStops == 1 && fixture.Created.Count == 1 && fixture.Active == 0,
                "cancellation during cleanup does not create replacement");
        }
        var queue = new ConcurrentQueue<Action>();
        using (var fixture = new Fixture(log, queue.Enqueue))
        {
            int changes = 0;
            fixture.Service.Changed += () => changes++;
            await fixture.Service.RequestRefreshAsync();
            fixture.Service.Shutdown();
            while (queue.TryDequeue(out var callback)) callback();
            Check(changes == 0 && fixture.Service.State.RefreshedAt is null,
                "queued publication fenced after shutdown");
            Check(fixture.Active == 0, "queued shutdown drains runtime");
            await fixture.Service.RequestRefreshAsync();
            Check(fixture.Created.Count == 1, "stopped service cannot create runtime");
        }
    }

    private static async Task SlowStopAndSuccessfulStartAge(Logger log)
    {
        using var fixture = new Fixture(log);
        fixture.Configure = runtime => runtime.BeforeStart = _ =>
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(15));
            return Task.CompletedTask;
        };
        await fixture.Service.RequestRefreshAsync();
        fixture.Configure = null;
        fixture.Clock.Advance(TimeSpan.FromMinutes(45));
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 1, "age starts after successful startup, not creation");
        var runtime = fixture.Created[0];
        var release = Signal();
        runtime.BeforeStop = () => release.Task;
        runtime.BeforeForceStop = () =>
        {
            release.TrySetResult();
            return Task.CompletedTask;
        };
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        await fixture.Service.RequestRefreshAsync();
        Check(runtime.ForcedStops == 1 && runtime.Disposed && fixture.Created.Count == 2 &&
            fixture.MaxActive == 1, "hung graceful stop is bounded and forced before replacement");
    }

    private static async Task ResponseStates(Logger log)
    {
        using (var fixture = new Fixture(log))
        {
            fixture.Configure = runtime => runtime.Quota = null;
            await fixture.Service.RequestRefreshAsync();
            Check(fixture.Service.State.Status == CopilotUsageStatus.Ready &&
                fixture.Service.State.Usage.IsEmpty, "missing quota stays unavailable");
        }
        using (var fixture = new Fixture(log))
        {
            fixture.Configure = runtime => runtime.Authenticated = false;
            await fixture.Service.RequestRefreshAsync();
            Check(fixture.Service.State.Status == CopilotUsageStatus.Unauthenticated &&
                fixture.Created[0].QuotaReads == 0, "unauthenticated does not read quota");
        }
        using (var fixture = new Fixture(log))
        {
            fixture.Reset = fixture.Clock.GetUtcNow().AddDays(-1);
            await fixture.Service.RequestRefreshAsync();
            await fixture.Service.RequestRefreshAsync();
            Check(fixture.Used == 0 && fixture.Service.State.Usage.PremiumInteractions!.ResetIsEstimated &&
                fixture.Created.Count == 1, "zero and invalid reset do not trigger recycling");
            var previous = fixture.Service.State;
            fixture.Created[0].BeforeQuota =
                _ => Task.FromException(new IOException("fixture quota failure"));
            await fixture.Service.RequestRefreshAsync();
            Check(fixture.Service.State.Status == CopilotUsageStatus.Error &&
                fixture.Service.State.RefreshedAt == previous.RefreshedAt, "quota error is not fresh success");
        }
    }

    private static async Task CycleBoundary(Logger log)
    {
        using var fixture = new Fixture(log);
        fixture.Clock.ShiftUtc(fixture.Reset.AddSeconds(-1) - fixture.Clock.GetUtcNow());
        fixture.UpstreamUsed = 74000;
        fixture.Configure = runtime =>
        {
            if (fixture.Created.Count == 1)
                runtime.BeforeQuota = _ =>
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                    fixture.UpstreamUsed = 75;
                    return Task.CompletedTask;
                };
        };
        await fixture.Service.RequestRefreshAsync();
        Check(fixture.Created.Count == 2 && fixture.Used == 75 && fixture.MaxActive == 1,
            "boundary retry reads from a new runtime, not frozen old quota");
        Check(fixture.Service.State.Usage.PremiumInteractions!.ReadStartedAt >= fixture.Reset,
            "boundary retry records new read start");
        Check(fixture.Store.Read(fixture.Account).Cycles.Single().Days.Single().LastUsed == 75,
            "only current-cycle value recorded");
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : TimeProvider
    {
        private long _elapsed;
        private long _utc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _elapsed);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utc), TimeSpan.Zero);
        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _elapsed, duration.Ticks);
            ShiftUtc(duration);
        }
        internal void ShiftUtc(TimeSpan duration) => Interlocked.Add(ref _utc, duration.Ticks);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly CopilotUsageAccount Account = new("fixture", "https://github.com", "fake");
        internal readonly List<Runtime> Created = [];
        internal readonly CopilotUsageHistoryStore Store;
        internal readonly CopilotUsageService Service;
        internal long UpstreamUsed;
        internal DateTimeOffset Reset = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        internal Action<Runtime>? Configure;
        internal int Logins, Active, MaxActive;
        internal long? Used => Service.State.Usage.PremiumInteractions?.UsedRequests;
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "pagurian-usage-runtime-" + Guid.NewGuid().ToString("N"));

        internal Fixture(Logger log, Action<Action>? dispatch = null)
        {
            Store = new(_directory);
            Service = new(Create, Clock, Store, dispatch ?? (action => action()), _ =>
            {
                Logins++;
                return Task.CompletedTask;
            });
            Service.Start(log, poll: false);
        }

        private ICopilotUsageRuntime Create()
        {
            var runtime = new Runtime(this)
            {
                Quota = new Dictionary<string, AccountQuotaSnapshot>
                {
                    ["premium_interactions"] = new()
                    {
                        EntitlementRequests = 75000,
                        UsedRequests = UpstreamUsed,
                        RemainingPercentage = 100 - UpstreamUsed * 100d / 75000,
                        ResetDate = Reset,
                    },
                },
            };
            Created.Add(runtime);
            Configure?.Invoke(runtime);
            return runtime;
        }

        public void Dispose()
        {
            Service.Shutdown();
            Check(Active == 0, "fixture leaked runtime");
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Runtime(Fixture owner) : ICopilotUsageRuntime
    {
        internal IDictionary<string, AccountQuotaSnapshot>? Quota;
        internal bool Authenticated = true, Active, Disposed;
        internal int ForcedStops, QuotaReads;
        internal Func<CancellationToken, Task>? BeforeStart, BeforeQuota;
        internal Func<Task>? BeforeStop, BeforeForceStop;

        public async Task StartAsync(CancellationToken token)
        {
            Active = true;
            owner.Active++;
            owner.MaxActive = Math.Max(owner.MaxActive, owner.Active);
            if (BeforeStart is not null) await BeforeStart(token);
        }

        public Task<CopilotUsageAuthentication> GetAuthStatusAsync(CancellationToken token) =>
            Task.FromResult(new CopilotUsageAuthentication(Authenticated, owner.Account, null));

        public async Task<IDictionary<string, AccountQuotaSnapshot>?> GetQuotaAsync(CancellationToken token)
        {
            QuotaReads++;
            if (BeforeQuota is not null) await BeforeQuota(token);
            return Quota;
        }

        public async Task StopAsync()
        {
            if (BeforeStop is not null) await BeforeStop();
            Stop();
        }

        public async Task ForceStopAsync()
        {
            ForcedStops++;
            if (BeforeForceStop is not null) await BeforeForceStop();
            Stop();
        }

        private void Stop()
        {
            if (!Active) return;
            Active = false;
            owner.Active--;
        }

        public void Dispose()
        {
            Check(!Active, "live runtime disposed without stopping");
            Disposed = true;
        }
    }
}

#pragma warning restore GHCP001
