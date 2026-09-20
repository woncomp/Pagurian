using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

// The SDK monitor is deliberately separate from CopilotSessionTracker. It owns
// only its read-only runtime and never resumes or attaches to a target session.
internal sealed class CopilotSdkSessionService
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BlockedConfirmationDelay = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly Dictionary<string, SdkState> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private ICopilotSdkSessionSource? _source;
    private Task? _discoveryTask;
    private Task? _statusTask;
    private TaskCompletionSource<bool>? _ready;
    private Logger? _log;
    private DispatcherQueue? _dispatcher;
    private int _references;
    private long _generation;

    public event Action<CopilotSession>? SessionStarted;
    public event Action<CopilotSession>? SessionChanged;
    public event Action<CopilotSession>? SessionEnded;
    public event Action? UiChanged;

    public IReadOnlyList<CopilotSession> Sessions
    {
        get
        {
            lock (_sync)
                return _states.Values.Select(state => state.Session).ToArray();
        }
    }

    public void Acquire(Logger log)
    {
        lock (_sync)
        {
            _references++;
            if (_lifetime is not null)
                return;
            _dispatcher = ReactorApp.UIDispatcher
                ?? throw new InvalidOperationException(
                    "The Reactor UI dispatcher is unavailable.");
            _log = log;
            _lifetime = new CancellationTokenSource();
            _source = new CopilotSdkSessionSource();
            _ready = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var generation = ++_generation;
            var token = _lifetime.Token;
            _discoveryTask = Task.Run(
                () => RunDiscoveryAsync(generation, token),
                CancellationToken.None);
            _statusTask = Task.Run(
                () => RunStatusAsync(generation, token),
                CancellationToken.None);
        }
    }

    public void Release()
    {
        CancellationTokenSource? lifetime = null;
        Task[] tasks = [];
        ICopilotSdkSessionSource? source = null;
        lock (_sync)
        {
            if (_references > 0)
                _references--;
            if (_references != 0 || _lifetime is null)
                return;
            lifetime = _lifetime;
            _lifetime = null;
            ++_generation;
            source = _source;
            _source = null;
            tasks = new[] { _discoveryTask, _statusTask }
                .Where(task => task is not null).Cast<Task>().ToArray();
            _discoveryTask = null;
            _statusTask = null;
            _ready = null;
            _states.Clear();
        }

        lifetime.Cancel();
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); }
        catch (AggregateException exception)
        {
            _log?.Warn($"Copilot SDK monitor shutdown failed: {exception.GetType().Name}");
        }
        try { source?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            _log?.Warn($"Copilot SDK runtime shutdown failed: {exception.GetType().Name}");
        }
        lifetime.Dispose();
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            if (_lifetime is null)
            {
                _references = 0;
                return;
            }
            _references = 1;
        }
        Release();
    }

    private async Task RunDiscoveryAsync(long generation, CancellationToken token)
    {
        try
        {
            var source = GetSource();
            await source.StartAsync(token).ConfigureAwait(false);
            _ready?.TrySetResult(true);
            var first = true;
            using var timer = new PeriodicTimer(DiscoveryInterval);
            while (first || await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                first = false;
                if (!IsCurrent(generation))
                    return;
                await DiscoverOnceAsync(generation, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ready?.TrySetCanceled(token);
        }
        catch (Exception exception)
        {
            _ready?.TrySetException(exception);
            _log?.Warn($"Copilot SDK discovery stopped: {exception.GetType().Name}");
        }
    }

    private async Task RunStatusAsync(long generation, CancellationToken token)
    {
        try
        {
            var ready = _ready?.Task;
            if (ready is not null)
                await ready.WaitAsync(token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(StatusInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsCurrent(generation))
                    return;
                var states = SnapshotStates();
                await Task.WhenAll(states.Select(state =>
                    ReadStatusAsync(state, generation, token))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log?.Warn($"Copilot SDK status polling stopped: {exception.GetType().Name}");
        }
    }

    private async Task DiscoverOnceAsync(long generation, CancellationToken token)
    {
        var source = GetSource();
        var previous = SnapshotStates().Select(state => state.Session.SessionId).ToArray();
        var snapshot = await source.DiscoverAsync(previous, token).ConfigureAwait(false);
        if (!IsCurrent(generation))
            return;
        if (snapshot.Health == CopilotSdkReadHealth.Stale)
        {
            foreach (var state in SnapshotStates())
                Publish(state, CopilotSdkReadHealth.Stale, snapshot.Failure);
            return;
        }

        var metadata = snapshot.Sessions.ToDictionary(
            item => item.SessionId, StringComparer.Ordinal);
        var inUse = snapshot.InUse;
        var ended = new List<CopilotSession>();
        var started = new List<CopilotSession>();
        lock (_sync)
        {
            foreach (var id in inUse)
            {
                if (!metadata.TryGetValue(id, out var item) &&
                    !_states.TryGetValue(id, out var existing))
                    continue;
                if (!_states.TryGetValue(id, out var state))
                {
                    var session = CreateSession(item!);
                    state = new SdkState(session);
                    _states.Add(id, state);
                    started.Add(session);
                }
                else if (metadata.TryGetValue(id, out item))
                {
                    UpdateMetadata(state.Session, item);
                }
            }

            foreach (var pair in _states.ToArray())
            {
                if (inUse.Contains(pair.Key))
                    continue;
                _states.Remove(pair.Key);
                pair.Value.CancelConfirmation();
                ended.Add(pair.Value.Session);
            }
        }

        foreach (var session in started)
        {
            PublishOnUi(() => SessionStarted?.Invoke(session));
            _ = ReadStatusAsync(FindState(session.SessionId), generation, token);
        }
        foreach (var session in ended)
            PublishOnUi(() => SessionEnded?.Invoke(session));
        if (started.Count != 0 || ended.Count != 0)
            PublishOnUi(() => UiChanged?.Invoke());
    }

    private async Task ReadStatusAsync(
        SdkState? state,
        long generation,
        CancellationToken token)
    {
        if (state is null || !IsCurrent(generation) ||
            !await state.ReadGate.WaitAsync(0, token).ConfigureAwait(false))
            return;
        try
        {
            var result = await GetSource().ReadRecentAsync(
                state.Session.SessionId, token).ConfigureAwait(false);
            if (!IsCurrent(generation))
                return;
            ApplyEvents(state, result, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            state.ReadGate.Release();
        }
    }

    private void ApplyEvents(
        SdkState state,
        CopilotSdkEventSnapshot snapshot,
        long generation,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot.Health == CopilotSdkReadHealth.Stale)
        {
            state.SetStatus(CopilotSessionStatus.Unknown, snapshot.Failure);
            Publish(state, CopilotSdkReadHealth.Stale, snapshot.Failure);
            state.CancelConfirmation();
            return;
        }

        var events = snapshot.Events;
        var newest = events.LastOrDefault()?.EventId;
        bool initial = state.Frontier is null;
        int firstNew = 0;
        if (!initial)
        {
            var frontier = events.ToList().FindIndex(item =>
                item.EventId == state.Frontier);
            if (frontier < 0)
            {
                state.MarkGap();
                Publish(state, CopilotSdkReadHealth.Healthy, "event-gap");
                state.CancelConfirmation();
                return;
            }
            firstNew = frontier + 1;
        }

        foreach (var item in events.Skip(firstNew))
            state.Apply(item);
        if (newest is not null)
            state.Frontier = newest;
        state.LastReadAt = now;
        state.Recent = events.TakeLast(5).ToArray();

        var candidate = state.FindCandidate();
        if (candidate is null)
        {
            state.CancelConfirmation();
            state.SetStatus(state.InferredStatus, null);
            Publish(state, snapshot.Health, null);
            return;
        }

        if (state.CandidateKey != candidate.Key)
        {
            state.CandidateKey = candidate.Key;
            state.CandidateSince = now;
            state.ConfirmedCandidate = null;
        }
        if (state.ConfirmedCandidate == candidate.Key)
            state.SetStatus(CopilotSessionStatus.Blocked, candidate.Reason);
        else
            state.SetStatus(state.Session.Status == CopilotSessionStatus.Blocked
                ? CopilotSessionStatus.Unknown
                : state.Session.Status, candidate.Reason);
        Publish(state, CopilotSdkReadHealth.Healthy, candidate.Reason);
        if (now - state.CandidateSince >= BlockedConfirmationDelay)
            ConfirmCandidate(state, candidate, generation, token);
        else
            ScheduleConfirmation(state, candidate.Key, generation, token);
    }

    private void ScheduleConfirmation(
        SdkState state,
        string key,
        long generation,
        CancellationToken token)
    {
        if (state.ConfirmationTask is not null)
            return;
        state.ConfirmationTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BlockedConfirmationDelay, token).ConfigureAwait(false);
                if (IsCurrent(generation) && state.CandidateKey == key)
                    await ReadStatusAsync(state, generation, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                state.ConfirmationTask = null;
            }
        }, CancellationToken.None);
    }

    private void ConfirmCandidate(
        SdkState state,
        SdkCandidate candidate,
        long generation,
        CancellationToken token)
    {
        if (state.ConfirmedCandidate == candidate.Key)
            return;
        state.ConfirmedCandidate = candidate.Key;
        state.SetStatus(CopilotSessionStatus.Blocked, candidate.Reason);
        Publish(state, CopilotSdkReadHealth.Healthy, candidate.Reason);
    }

    private void Publish(SdkState state, CopilotSdkReadHealth health, string? reason)
    {
        state.Session.SdkReadHealth = health;
        state.Session.SdkStatusReason = reason;
        state.Session.SdkLastReadAt = state.LastReadAt;
        state.Session.SdkHistoryPartial = state.Partial;
        state.Session.RecentPersistedEvents = state.Recent;
        PublishOnUi(() =>
        {
            state.Session.NotifyChanged();
            SessionChanged?.Invoke(state.Session);
            UiChanged?.Invoke();
        });
    }

    private static CopilotSession CreateSession(CopilotSdkSessionMetadata metadata) =>
        new()
        {
            SessionId = metadata.SessionId,
            Name = metadata.Name ?? metadata.SessionId,
            NameResolved = metadata.Name is not null,
            Status = CopilotSessionStatus.Unknown,
            Client = CopilotSessionIdentityIndex.ClassifyClient(metadata.ClientName),
            ClientMarker = metadata.ClientName,
            Cwd = metadata.Cwd,
            IsSdk = true,
            SdkReadHealth = CopilotSdkReadHealth.Empty,
            SdkHistoryPartial = true,
        };

    private static void UpdateMetadata(
        CopilotSession session,
        CopilotSdkSessionMetadata metadata)
    {
        if (metadata.Name is not null)
        {
            session.Name = metadata.Name;
            session.NameResolved = true;
        }
        if (metadata.ClientName is not null)
        {
            session.ClientMarker = metadata.ClientName;
            session.Client = CopilotSessionIdentityIndex.ClassifyClient(metadata.ClientName);
        }
        if (metadata.Cwd is not null)
            session.Cwd = metadata.Cwd;
    }

    private SdkState[] SnapshotStates()
    {
        lock (_sync)
            return _states.Values.ToArray();
    }

    private SdkState? FindState(string id)
    {
        lock (_sync)
            return _states.GetValueOrDefault(id);
    }

    private ICopilotSdkSessionSource GetSource() =>
        _source ?? throw new InvalidOperationException(
            "The Copilot SDK monitor is not running.");

    private bool IsCurrent(long generation)
    {
        lock (_sync)
            return _lifetime is not null && _generation == generation;
    }

    private void PublishOnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return;
        if (!dispatcher.TryEnqueue(() => action()))
            _log?.Warn("Copilot SDK UI update was rejected.");
    }

    private sealed class SdkState(CopilotSession session)
    {
        private readonly Dictionary<string, CopilotSdkPersistedEvent> _events =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, CopilotSdkPersistedEvent> _open =
            new(StringComparer.Ordinal);
        public CopilotSession Session { get; } = session;
        public SemaphoreSlim ReadGate { get; } = new(1, 1);
        public string? Frontier { get; set; }
        public DateTimeOffset? LastReadAt { get; set; }
        public IReadOnlyList<CopilotSdkPersistedEvent> Recent { get; set; } = [];
        public bool Partial { get; private set; }
        public string InferredStatusReason { get; private set; } = "no persisted status evidence";
        public CopilotSessionStatus InferredStatus { get; private set; } =
            CopilotSessionStatus.Unknown;
        public string? CandidateKey { get; set; }
        public DateTimeOffset CandidateSince { get; set; }
        public string? ConfirmedCandidate { get; set; }
        public Task? ConfirmationTask { get; set; }

        public void Apply(CopilotSdkPersistedEvent item)
        {
            if (!_events.TryAdd(item.EventId, item))
                return;
            if (item.Type == "permission.requested" && !item.ResolvedByHook &&
                item.RequestId is not null)
                _open[$"permission:{item.AgentId}:{item.RequestId}"] = item;
            else if (item.Type == "permission.completed" && item.RequestId is not null)
                _open.Remove($"permission:{item.AgentId}:{item.RequestId}");
            else if (item.Type is "tool.execution_start" &&
                item.ToolCallId is not null &&
                (item.ToolName is "ask_user" or "exit_plan_mode"))
                _open[$"tool:{item.AgentId}:{item.ToolCallId}"] = item;
            else if (item.Type == "tool.execution_complete" &&
                item.ToolCallId is not null)
                _open.Remove($"tool:{item.AgentId}:{item.ToolCallId}");

            if (item.Lifecycle == "Completed")
            {
                InferredStatus = CopilotSessionStatus.Idle;
                InferredStatusReason = "completed persisted event";
            }
            else if (item.Lifecycle == "Active")
            {
                InferredStatus = CopilotSessionStatus.Working;
                InferredStatusReason = "active persisted event";
            }
        }

        public SdkCandidate? FindCandidate()
        {
            var item = _open.Values.OrderByDescending(value => value.At).FirstOrDefault();
            if (item is null)
                return null;
            var key = item.Type == "permission.requested"
                ? $"permission:{item.AgentId}:{item.RequestId}"
                : $"tool:{item.AgentId}:{item.ToolCallId}";
            var reason = item.Type == "permission.requested"
                ? "permission request inferred from persisted event"
                : $"{item.ToolName} invocation inferred from persisted event";
            return new(key, reason);
        }

        public void SetStatus(CopilotSessionStatus status, string? reason)
        {
            Session.Status = status;
            InferredStatusReason = reason ?? InferredStatusReason;
        }

        public void MarkGap()
        {
            Partial = true;
            InferredStatus = CopilotSessionStatus.Unknown;
            Session.Status = CopilotSessionStatus.Unknown;
            InferredStatusReason = "persisted event continuity gap";
            _open.Clear();
        }

        public void CancelConfirmation()
        {
            CandidateKey = null;
            ConfirmedCandidate = null;
            ConfirmationTask = null;
        }
    }

    private sealed record SdkCandidate(string Key, string Reason);
}
