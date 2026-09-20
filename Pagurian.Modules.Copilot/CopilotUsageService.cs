#pragma warning disable GHCP001

using System.Diagnostics;
using System.Globalization;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

// Owns the module's single SDK runtime. All SDK and CLI work runs on pool
// threads; immutable snapshots are published through Reactor's UI dispatcher.
// Refreshes and logins are each coalesced, and a shared gate serializes the two
// operations so account changes cannot race a quota read.
internal sealed class CopilotUsageService : ICopilotUsageSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationShutdownTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClientShutdownTimeout =
        TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private CancellationTokenSource? _lifetime;
    private DispatcherQueue? _dispatcher;
    private Logger? _log;
    private CopilotClient? _client;
    private Process? _loginProcess;
    private Task? _periodicTask;
    private Task? _refreshTask;
    private Task? _loginTask;
    private long _generation;
    private CopilotUsageState _state = CopilotUsageState.Initial;

    public CopilotUsageState State => Volatile.Read(ref _state);

    public event Action? Changed;

    public void Start(Logger log)
    {
        lock (_sync)
        {
            if (_lifetime is not null)
                return;

            _dispatcher = ReactorApp.UIDispatcher
                ?? throw new InvalidOperationException(
                    "The Reactor UI dispatcher is unavailable.");
            _log = log;
            _lifetime = new CancellationTokenSource();
            Volatile.Write(ref _state, CopilotUsageState.Initial);

            var generation = ++_generation;
            var token = _lifetime.Token;
            _periodicTask = Task.Run(
                () => RunPeriodicAsync(generation, token),
                CancellationToken.None);
        }
    }

    public void Shutdown()
    {
        CancellationTokenSource? lifetime;
        Task[] tasks;
        Process? loginProcess;

        lock (_sync)
        {
            lifetime = _lifetime;
            if (lifetime is null)
                return;

            _lifetime = null;
            ++_generation;
            loginProcess = _loginProcess;
            tasks = new[] { _periodicTask, _refreshTask, _loginTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .Distinct()
                .ToArray();
        }

        lifetime.Cancel();
        TryTerminate(loginProcess);

        var operations = Task.WhenAll(tasks);
        var operationsStopped = WaitForShutdown(
            operations,
            OperationShutdownTimeout);
        if (!operationsStopped)
        {
            _log?.Warn(
                "Copilot usage operations did not stop after cancellation; " +
                "forcing the SDK runtime to stop.");
            ForceStopClientForShutdown();
            if (!WaitForShutdown(operations, ClientShutdownTimeout))
            {
                ForceStopClientForShutdown();
                _log?.Warn(
                    "Copilot usage operations remained active during shutdown.");
            }
        }
        else
        {
            StopClientForShutdown();
        }

        lifetime.Dispose();
        lock (_sync)
        {
            _periodicTask = null;
            _refreshTask = null;
            _loginTask = null;
            _loginProcess = null;
            _dispatcher = null;
            _log = null;
        }
    }

    public void Refresh() => _ = RequestRefreshAsync();

    public void Login() => _ = RequestLoginAsync();

    private async Task RunPeriodicAsync(long generation, CancellationToken token)
    {
        try
        {
            await RequestRefreshAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsCurrent(generation))
                    return;

                await RequestRefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private Task RequestRefreshAsync()
    {
        lock (_sync)
        {
            if (_lifetime is null)
                return Task.CompletedTask;
            if (_loginTask is { IsCompleted: false })
                return _loginTask;
            if (_refreshTask is { IsCompleted: false })
                return _refreshTask;

            var generation = _generation;
            var token = _lifetime.Token;
            var task = Task.Run(
                () => RefreshOperationAsync(generation, token),
                CancellationToken.None);
            _refreshTask = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_refreshTask, task))
                            _refreshTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private Task RequestLoginAsync()
    {
        lock (_sync)
        {
            if (_lifetime is null)
                return Task.CompletedTask;
            if (_loginTask is { IsCompleted: false })
                return _loginTask;

            var generation = _generation;
            var token = _lifetime.Token;
            var task = Task.Run(
                () => LoginOperationAsync(generation, token),
                CancellationToken.None);
            _loginTask = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_loginTask, task))
                            _loginTask = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task RefreshOperationAsync(
        long generation,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            Publish(generation, state => state.BeginRefresh());
            await RefreshClientStateAsync(
                    generation,
                    token,
                    finishLogin: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ReportFailure(generation, error, finishLogin: false);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
        }
    }

    private async Task LoginOperationAsync(
        long generation,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            Publish(generation, state => state.BeginLogin());

            // Recreate the SDK runtime after the official CLI writes the shared
            // credential store so the next status read observes the new account.
            await StopClientAsync(token).ConfigureAwait(false);
            await RunOfficialLoginAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await RefreshClientStateAsync(
                    generation,
                    token,
                    finishLogin: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ReportFailure(generation, error, finishLogin: true);
        }
        finally
        {
            if (entered)
                _operationGate.Release();
        }
    }

    private async Task RefreshClientStateAsync(
        long generation,
        CancellationToken token,
        bool finishLogin)
    {
        var client = await EnsureClientAsync(token).ConfigureAwait(false);
        var auth = await client.GetAuthStatusAsync(token).ConfigureAwait(false);
        var account = new CopilotUsageAccount(
            Clean(auth.Login),
            Clean(auth.Host),
            Clean(auth.AuthType));
        var refreshedAt = DateTimeOffset.UtcNow;

        if (!auth.IsAuthenticated)
        {
            Publish(
                generation,
                state => state.CompleteUnauthenticated(
                    account,
                    Clean(auth.StatusMessage),
                    refreshedAt,
                    finishLogin));
            return;
        }

        Publish(generation, state => state.WithAccount(account));
        var readStartedAt = DateTimeOffset.UtcNow;
        var quota = await client.Rpc.Account.GetQuotaAsync(null, null, token).ConfigureAwait(false);
        var premium = NormalizePremiumInteractions(quota.QuotaSnapshots);
        if (premium?.ResetDate is { } reset && readStartedAt < reset.AddMonths(-1))
        {
            // A request spanning reset can normalize into the next fallback
            // cycle. Read once more *after* the boundary, within the existing
            // serialized operation; do not rely on a coalesced Refresh request
            // to start another read while this task is still completing.
            readStartedAt = DateTimeOffset.UtcNow;
            quota = await client.Rpc.Account.GetQuotaAsync(null, null, token).ConfigureAwait(false);
            premium = NormalizePremiumInteractions(quota.QuotaSnapshots);
        }
        var usage = new CopilotUsageSnapshot(
            premium is null ? null : premium with { ReadStartedAt = readStartedAt });

        Publish(
            generation,
            state => state.Complete(
                account,
                usage,
                DateTimeOffset.UtcNow,
                finishLogin));
    }

    private async Task<CopilotClient> EnsureClientAsync(
        CancellationToken token)
    {
        lock (_sync)
        {
            if (_client is not null)
                return _client;
        }

        var runtimePath = CopilotModule.CopilotRuntimePath;
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException(
                "The bundled GitHub Copilot runtime is unavailable.",
                runtimePath);
        }

        var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CopilotCli,
            UseLoggedInUser = true,
            Connection = RuntimeConnection.ForStdio(runtimePath),
        });
        lock (_sync)
            _client = client;

        try
        {
            await client.StartAsync(token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_client, client))
                    _client = null;
            }
            await DisposeClientAsync(client, token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunOfficialLoginAsync(CancellationToken token)
    {
        var cliPath = FindOfficialCopilotCli();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                WorkingDirectory = Path.GetDirectoryName(cliPath),
                UseShellExecute = true,
            },
        };
        process.StartInfo.ArgumentList.Add("login");

        lock (_sync)
            _loginProcess = process;

        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("GitHub Copilot CLI did not start.");

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"GitHub Copilot CLI login exited with code {process.ExitCode}.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryTerminate(process);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_loginProcess, process))
                    _loginProcess = null;
            }
        }
    }

    private static string FindOfficialCopilotCli()
    {
        var bundledRuntimeDirectory = Path.GetDirectoryName(
            Path.GetFullPath(CopilotModule.CopilotRuntimePath));
        var path = Environment.GetEnvironmentVariable("PATH");
        var commandExtensions = CopilotCommandExtensions();

        foreach (var entry in (path ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(
                entry.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            foreach (var extension in commandExtensions)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(
                        Path.Combine(directory, "copilot" + extension));
                }
                catch (Exception error) when (
                    error is ArgumentException or NotSupportedException or
                        PathTooLongException)
                {
                    continue;
                }

                if (!File.Exists(candidate))
                    continue;
                if (string.Equals(
                        Path.GetDirectoryName(candidate),
                        bundledRuntimeDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The official GitHub Copilot CLI is not installed or is not " +
            "available on PATH. Install it, restart Pagurian, and try again.");
    }

    private static IReadOnlyList<string> CopilotCommandExtensions()
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.Trim())
            .Where(extension =>
                extension.Equals(".EXE", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".CMD", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".BAT", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!extensions.Contains(".exe", StringComparer.OrdinalIgnoreCase))
            extensions.Insert(0, ".exe");
        if (!extensions.Contains(".cmd", StringComparer.OrdinalIgnoreCase))
            extensions.Add(".cmd");

        return extensions;
    }

    private async Task StopClientAsync(CancellationToken token)
    {
        var client = DetachClient();
        if (client is null)
            return;

        await DisposeClientAsync(client, token).ConfigureAwait(false);
    }

    private void StopClientForShutdown()
    {
        var client = DetachClient();
        if (client is null)
            return;

        var gracefulStop = Task.Run(
            async () => await client.StopAsync().ConfigureAwait(false));
        var gracefulCompleted = WaitForShutdown(
            gracefulStop,
            ClientShutdownTimeout);
        if (!gracefulCompleted || gracefulStop.IsFaulted)
        {
            _log?.Warn(
                "Copilot SDK graceful shutdown did not complete; forcing stop.");
            ForceStopClientForShutdown(client);
            return;
        }

        client.Dispose();
    }

    private void ForceStopClientForShutdown() =>
        ForceStopClientForShutdown(DetachClient());

    private void ForceStopClientForShutdown(CopilotClient? client)
    {
        if (client is null)
            return;

        var forceStop = Task.Run(
            async () => await client.ForceStopAsync().ConfigureAwait(false));
        if (!WaitForShutdown(forceStop, ClientShutdownTimeout))
        {
            _log?.Warn(
                "Copilot SDK forced shutdown did not complete before exit.");
            return;
        }

        try
        {
            client.Dispose();
        }
        catch (Exception error)
        {
            _log?.Warn(
                "Copilot SDK disposal failed during shutdown " +
                $"({Describe(error)}).");
        }
    }

    private CopilotClient? DetachClient()
    {
        lock (_sync)
        {
            var client = _client;
            _client = null;
            return client;
        }
    }

    private static bool WaitForShutdown(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch
        {
            // Operation bodies normally consume failures. A completed faulted
            // task is still stopped and must not block process shutdown.
            return true;
        }
    }

    private async Task DisposeClientAsync(
        CopilotClient client,
        CancellationToken token = default)
    {
        try
        {
            await client.StopAsync()
                .WaitAsync(ClientShutdownTimeout, token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log?.Warn(
                "Copilot SDK graceful shutdown failed; forcing stop " +
                $"({Describe(error)}).");
            try
            {
                await client.ForceStopAsync()
                    .WaitAsync(ClientShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (Exception forceError)
            {
                _log?.Warn(
                    "Copilot SDK forced shutdown failed " +
                    $"({Describe(forceError)}).");
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    private CopilotUsageQuota? NormalizePremiumInteractions(
        IDictionary<string, AccountQuotaSnapshot>? snapshots)
    {
        if (snapshots is null ||
            !snapshots.TryGetValue("premium_interactions", out var snapshot) ||
            snapshot is null)
        {
            return null;
        }

        // Keep the server-provided reset timestamp observable; it must not be
        // inferred from the refresh time or the entitlement state.
        _log?.Info(
            "Copilot premium_interactions resetDate=" +
            (snapshot.ResetDate?.ToString("O", CultureInfo.InvariantCulture) ??
                "<null>"));

        return CopilotUsageNormalizer.Normalize(
            snapshot.EntitlementRequests,
            snapshot.IsUnlimitedEntitlement,
            snapshot.UsedRequests,
            snapshot.RemainingPercentage,
            snapshot.ResetDate,
            DateTimeOffset.UtcNow);
    }

    private void ReportFailure(
        long generation,
        Exception error,
        bool finishLogin)
    {
        var message = Describe(error);
        _log?.Warn($"Copilot usage refresh failed ({message}).");
        Publish(
            generation,
            state => state.Fail(
                $"Copilot usage is unavailable. {message}",
                finishLogin));
    }

    private void Publish(
        long generation,
        Func<CopilotUsageState, CopilotUsageState> update)
    {
        DispatcherQueue? dispatcher;
        lock (_sync)
            dispatcher = _dispatcher;

        dispatcher?.TryEnqueue(() =>
        {
            if (!IsCurrent(generation))
                return;

            Volatile.Write(ref _state, update(State));
            Changed?.Invoke();
        });
    }

    private bool IsCurrent(long generation)
    {
        lock (_sync)
            return _lifetime is not null && _generation == generation;
    }

    private static void TryTerminate(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Describe(Exception error)
    {
        var message = error.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (message.Length > 240)
            message = message[..240] + "…";
        return string.IsNullOrEmpty(message)
            ? error.GetType().Name
            : message;
    }
}

#pragma warning restore GHCP001
