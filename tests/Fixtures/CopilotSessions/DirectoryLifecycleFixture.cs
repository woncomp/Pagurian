using Pagurian.Modules.Copilot;
using Microsoft.UI.Reactor;
using Pagurian.Sdk;
using System.Text.Json;

internal static class DirectoryLifecycleFixture
{
    private static int _assertions;
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-17T08:00:00Z");

    public static void Run()
    {
        NamesAndClients();
        DelayedOwnName();
        SchedulingAndProbeErrors();
        DeletionAndRecovery();
        StaleEvidenceAndLifecycle();
        ResolverRecovery();
        TrackerDirectoryCleanup();
        Console.WriteLine($"  {_assertions} directory/name lifecycle assertions passed");
    }

    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static CopilotSessionIdentity Root(string id = "root", string? name = "Named root",
        CopilotClientKind client = CopilotClientKind.App) =>
        new(id, id, client == CopilotClientKind.App ? CopilotIdentityKind.AppRoot : CopilotIdentityKind.Cli,
            name, client);

    private static CopilotSessionIdentity Child(string id = "child") =>
        new(id, "root", CopilotIdentityKind.AppTaskChild, null, CopilotClientKind.App, ParentId: "root");

    private static void NamesAndClients()
    {
        foreach (var client in new[] { CopilotClientKind.App, CopilotClientKind.Cli,
            CopilotClientKind.VSCode, CopilotClientKind.Other })
        foreach (string? missing in new string?[] { null, "", " \t\r\n" })
        {
            var time = new Clock();
            var state = new CopilotSessionState(time.GetUtcNow);
            int starts = 0;
            state.SessionStarted += _ => starts++;
            var identity = Root(name: missing, client: client);
            state.Resolve([identity]);
            state.Handle(new("permissionRequest", "root", time.GetUtcNow(), ""));
            time.Advance(1000);
            state.CommitStableStatuses();
            Check(state.Sessions.Count == 0 && starts == 0, "Unnamed clients never publish placeholder cells");
            Check(state.DirectoryTargets.Count == 0, "Unnamed clients do not enter icon cleanup");
            state.Resolve([identity with { Name = "Confirmed own name" }]);
            var cell = state.Find("root");
            Check(cell is { NameResolved: true, Name: "Confirmed own name", BlockingSources.Count: 1 },
                "Naming admits the existing source without losing its permission");
            time.Advance(1000);
            state.CommitStableStatuses();
            Check(cell.Status == CopilotSessionStatus.Blocked, "First named cell uses reduced work");
            state.Resolve([identity with { Name = "Renamed" }]);
            Check(starts == 1 && ReferenceEquals(cell, state.Find("root")) && cell.Name == "Renamed",
                "Rename updates one stable cell");
        }
        var ended = new CopilotSessionState();
        ended.Resolve([Root(name: null, client: CopilotClientKind.Cli)]);
        ended.Handle(new("sessionStart", "root", Epoch, ""));
        ended.Handle(new("sessionEnd", "root", Epoch.AddSeconds(1), ""));
        ended.Resolve([Root(client: CopilotClientKind.Cli)]);
        Check(ended.Sessions.Count == 0, "Naming cannot revive a terminal independent session");
        var historical = new CopilotSessionState();
        historical.Resolve([Root()]);
        Check(historical.Sessions.Count == 0, "Name alone is not admission evidence");
    }

    private static void DelayedOwnName()
    {
        using var f = new Files();
        f.Metadata("root", "client_name: github/autopilot\n");
        f.Metadata("child", "name: Child name\n");
        f.Resolver.Observe("root");
        f.Resolver.Observe("child", parentId: "root");
        var state = new CopilotSessionState();
        state.Handle(new("permissionRequest", "child", Epoch, ""));
        state.Resolve(f.Resolver.Scan());
        Check(state.Sessions.Count == 0, "A child's name cannot name its owner");
        f.Metadata("root", "client_name: github/autopilot\nname: Own root name\n");
        state.Resolve(f.Resolver.Scan());
        var cell = state.Find("root");
        Check(cell is { Name: "Own root name", BlockingSources.Count: 1 }, "Metadata polling admits a named root");
        f.Metadata("root", "client_name: github/autopilot\nname: ''\n");
        state.Resolve(f.Resolver.Scan());
        Check(ReferenceEquals(cell, state.Find("root")) && cell.Name == "Own root name",
            "Partial metadata does not erase a confirmed name");
        f.Metadata("root", "name: [broken\n");
        state.Resolve(f.Resolver.Scan());
        Check(ReferenceEquals(cell, state.Find("root")), "Malformed metadata does not remove a cell");
        state.Resolve([Child("unnamed")]);
        state.Handle(new("permissionRequest", "unnamed", Epoch.AddSeconds(1), ""));
        Check(state.Sessions.Count == 1 && cell.BlockingSources.Count == 2,
            "Unnamed task children still participate in the named group");
    }

    private static void SchedulingAndProbeErrors()
    {
        var time = new Clock();
        string root = Path.GetFullPath(Path.Combine("artifacts", "directory-probe-fixture"));
        var visits = new List<string>();
        var diagnostics = new List<string>();
        Func<string, FileAttributes> probe = _ => FileAttributes.Directory;
        var reader = new CopilotSessionDirectoryReader(root, diagnostics.Add, time, path =>
        {
            visits.Add(path);
            return probe(path);
        });
        CopilotSessionDirectoryTarget[] targets = [new("one", 1), new("two", 2)];
        Check(reader.Scan(targets, default) is null && visits.Count == 0, "No initial cleanup I/O");
        time.Advance(9999);
        for (int i = 0; i < 100; i++) Check(reader.Scan(targets, default) is null, "Hook wakes cannot accelerate cleanup");
        Check(visits.Count == 0, "No directory calls before ten seconds");
        time.Advance(1);
        var first = reader.Scan(targets, default)!;
        Check(visits.Count == 3 && first.Sessions.Count == 2 &&
            first.Sessions.All(e => e.Presence == CopilotSessionDirectoryPresence.Present),
            "At ten seconds check the root and every displayed owner once");
        visits.Clear();
        Check(reader.Scan(targets, default) is null, "No overlapping/repeated sweep at the same timestamp");
        time.Advance(9999);
        Check(reader.Scan(targets, default) is null && visits.Count == 0, "Next sweep not before twenty seconds");
        time.Advance(1);
        Check(reader.Scan(targets, default)!.Version > first.Version && visits.Count == 3, "Sweep at twenty seconds");

        CopilotSessionDirectoryPresence Next(Func<string, FileAttributes> next)
        {
            probe = next;
            visits.Clear();
            time.Advance(10000);
            return reader.Scan([targets[0]], default)!.Sessions.Single().Presence;
        }
        Check(Next(path => path == root ? FileAttributes.Directory : throw new FileNotFoundException()) ==
            CopilotSessionDirectoryPresence.Missing && visits.Count == 3, "Explicit missing path rechecks the common root");
        Check(Next(path => path == root ? FileAttributes.Directory : throw new DirectoryNotFoundException()) ==
            CopilotSessionDirectoryPresence.Missing, "Path-not-found under accessible root is missing");
        foreach (var error in new Exception[] { new UnauthorizedAccessException(), new IOException(),
            new System.Security.SecurityException() })
        {
            Check(Next(path => path == root ? FileAttributes.Directory : throw error) ==
                CopilotSessionDirectoryPresence.Unknown, "Target read errors are not deletion");
            Check(Next(_ => throw error) == CopilotSessionDirectoryPresence.Unknown && visits.Count == 1,
                "Unavailable root does not mass-delete icons");
        }
        Check(Next(_ => throw new DirectoryNotFoundException()) == CopilotSessionDirectoryPresence.Unknown,
            "Entire state directory absent is unavailable, not individual deletion");
        Check(Next(path => path == root ? FileAttributes.Directory : FileAttributes.Normal) ==
            CopilotSessionDirectoryPresence.Unknown, "Unexpected regular file is not a deleted directory");
        Check(Next(path => path == root ? FileAttributes.Directory : FileAttributes.Directory | FileAttributes.ReparsePoint) ==
            CopilotSessionDirectoryPresence.Unknown, "Do not follow target reparse points");
        Check(Next(_ => FileAttributes.Directory | FileAttributes.ReparsePoint) ==
            CopilotSessionDirectoryPresence.Unknown, "Do not follow a replaced root");
        int rootReads = 0;
        Check(Next(path => path == root && ++rootReads == 1
            ? FileAttributes.Directory : throw new DirectoryNotFoundException()) ==
            CopilotSessionDirectoryPresence.Unknown, "Root loss between checks cannot produce Missing");
        Check(diagnostics.Count > 0, "Unavailable evidence emits bounded reason codes");
        time.Advance(10000);
        visits.Clear();
        Check(reader.Scan([], default) is null && visits.Count == 0, "No icons means no cleanup I/O");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { reader.Scan(targets, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && visits.Count == 0, "Cancelled scans do not access paths");

        probe = _ => FileAttributes.Directory;
        var many = Enumerable.Range(0, 300).Select(i => new CopilotSessionDirectoryTarget($"visible-{i}", i)).ToArray();
        time.Advance(10000);
        Check(reader.Scan(many, default)!.Sessions.Count == 300 && visits.Count == 301,
            "A sweep covers all icons with linear attribute reads, not a 128-session truncation");
    }

    private static void DeletionAndRecovery()
    {
        foreach (var client in new[] { CopilotClientKind.App, CopilotClientKind.Cli,
            CopilotClientKind.VSCode, CopilotClientKind.Other })
        {
            var time = new Clock();
            var state = new CopilotSessionState(time.GetUtcNow);
            int starts = 0, ends = 0;
            state.SessionStarted += _ => starts++;
            state.SessionEnded += _ => ends++;
            var root = Root(client: client);
            state.Resolve([root, Root("other")]);
            state.Handle(new("permissionRequest", "root", time.GetUtcNow(), ""));
            state.Handle(new("sessionStart", "other", time.GetUtcNow(), ""));
            var target = state.DirectoryTargets.Single(t => t.SessionId == "root");
            time.Advance(10000);
            var missing = Snapshot(1, time, target, CopilotSessionDirectoryPresence.Missing);
            state.Resolve([], directories: missing);
            Check(state.Find("root") is null && state.Find("other") is not null && ends == 1,
                "Missing deletes only its icon even when blocked and without a terminal hook");
            Check(state.DirectoryTargets.All(t => t.SessionId != "root"), "Deleted icons leave periodic scan targets");
            state.Resolve([root], directories: missing);
            Check(ends == 1 && starts == 2, "Cached identities and duplicate snapshots cannot resurrect");
            state.Resolve([], directories: Snapshot(2, time, target, CopilotSessionDirectoryPresence.Present, "Cached"));
            Check(state.Find("root") is null, "Directory reappearance alone cannot resurrect");
            state.Handle(new("permissionRequest", "root", Epoch, ""));
            Check(state.DirectoryTargets.All(t => t.SessionId != "root"), "Late pre-deletion hooks do not request recovery");
            time.Advance(1);
            state.Handle(new("preToolUse", "root", time.GetUtcNow(), ""));
            var recovery = state.DirectoryTargets.Single(t => t.SessionId == "root");
            Check(recovery.Recover && state.Find("root") is null, "A fresh hook requests verification but not a cell");
            state.Resolve([root], directories: Snapshot(3, time, recovery, CopilotSessionDirectoryPresence.Present));
            Check(state.Find("root") is null, "Present with only cached name is insufficient to restore");
            state.Resolve([root], directories: Snapshot(4, time, recovery, CopilotSessionDirectoryPresence.Unknown, "Fresh"));
            Check(state.Find("root") is null, "Fresh name does not bypass unknown directory evidence");
            state.Resolve([root], directories: Snapshot(5, time, recovery, CopilotSessionDirectoryPresence.Present, "Fresh"));
            Check(state.Find("root") is { Name: "Fresh", BlockingSources.Count: 0 } &&
                state.Find("root")!.PendingStatus == CopilotSessionStatus.Working && starts == 3,
                "Fresh hook and fresh directory/name restore current work without old permission");
            state.Resolve([], directories: Snapshot(6, time, target, CopilotSessionDirectoryPresence.Missing));
            Check(state.Find("root") is not null && ends == 1, "Old revision cannot remove a rebuilt cell");
        }

        var groupTime = new Clock();
        var group = new CopilotSessionState(groupTime.GetUtcNow);
        var usage = new CopilotSessionDetailsReducer("root");
        using (var document = JsonDocument.Parse("""
            {"type":"model.model_call_success","id":"root-call","timestamp":"2026-09-17T08:00:00Z",
             "data":{"requestId":"root-call","responseUsage":{"prompt_tokens":10,"completion_tokens":2}}}
            """))
            usage.Observe(document.RootElement);
        group.Resolve([Root() with { Details = usage.Snapshot }, Child()]);
        group.Handle(new("permissionRequest", "child", groupTime.GetUtcNow(), ""));
        Check(group.DirectoryTargets.Count == 1 && group.DirectoryTargets[0].SessionId == "root",
            "Cleanup targets displayed owners, never child directories");
        var ownerTarget = group.DirectoryTargets[0];
        groupTime.Advance(10000);
        group.Resolve([], directories: Snapshot(1, groupTime, ownerTarget, CopilotSessionDirectoryPresence.Missing));
        groupTime.Advance(1);
        group.Handle(new("preToolUse", "root", groupTime.GetUtcNow(), ""));
        group.Resolve([], directories: Snapshot(2, groupTime, group.DirectoryTargets.Single(),
            CopilotSessionDirectoryPresence.Present, "Root restored"));
        Check(group.Find("root")!.BlockingSources.Count == 0 &&
            group.Find("root")!.Nodes.Single(n => n.SessionId == "child").Status is null,
            "Restored owner does not reactivate old child state");
        Check(group.Find("root")!.GroupDetails.InputTokens == 10 &&
            group.Find("root")!.Nodes.Count == 2, "Directory cleanup retains conversation usage and task history");
    }

    private static void StaleEvidenceAndLifecycle()
    {
        var time = new Clock();
        var state = new CopilotSessionState(time.GetUtcNow);
        state.Resolve([Root()]);
        state.Handle(new("sessionStart", "root", time.GetUtcNow(), ""));
        var oldTarget = state.DirectoryTargets.Single();
        time.Advance(10000);
        var stale = Snapshot(1, time, oldTarget, CopilotSessionDirectoryPresence.Missing);
        time.Advance(1);
        state.Handle(new("permissionRequest", "root", time.GetUtcNow(), ""));
        state.Resolve([], directories: stale);
        Check(state.Find("root")?.BlockingSources.Count == 1, "A new hook invalidates older in-flight cleanup");
        state.Resolve([], directories: Snapshot(2, time, state.DirectoryTargets.Single(), CopilotSessionDirectoryPresence.Unknown));
        Check(state.Find("root") is not null, "Unknown evidence preserves normal visibility");
        state.Resolve([], directories: Snapshot(3, time, state.DirectoryTargets.Single(), CopilotSessionDirectoryPresence.Missing));
        time.Advance(1);
        state.Handle(new("preToolUse", "root", time.GetUtcNow(), ""));
        var recovery = state.DirectoryTargets.Single();
        var app = new CopilotAppInstance(1, Epoch.AddMinutes(-1), @"C:\fixture\github.exe");
        state.Resolve([], new(1, time.GetUtcNow(),
            [new("root", CopilotArchiveState.Archived, app, time.GetUtcNow())], []),
            Snapshot(4, time, recovery, CopilotSessionDirectoryPresence.Present, "Restored"));
        Check(state.Find("root") is null, "Directory recovery does not bypass App archive");
        time.Advance(1);
        state.Resolve([], new(2, time.GetUtcNow(), [new("root", CopilotArchiveState.Live, app, time.GetUtcNow())], []));
        Check(state.Find("root") is not null, "Existing positive unarchive/load rules remain effective");
        state.Resolve([], directories: Snapshot(5, time, oldTarget, CopilotSessionDirectoryPresence.Missing));
        Check(state.Find("root") is not null, "Older cell lifetime cannot delete a reloaded App root");

        var deferred = new CopilotSessionState(time.GetUtcNow);
        deferred.Resolve([Root()]);
        deferred.Handle(new("sessionStart", "root", time.GetUtcNow(), ""));
        var beforeChild = deferred.DirectoryTargets.Single();
        time.Advance(1);
        deferred.Handle(new("permissionRequest", "child", time.GetUtcNow(), ""));
        deferred.Resolve([Child()], directories: Snapshot(1, time, beforeChild, CopilotSessionDirectoryPresence.Missing));
        Check(deferred.Find("root")?.BlockingSources.Count == 1,
            "Deferred child ownership invalidates cleanup captured before that group change");

        var terminal = new CopilotSessionState(time.GetUtcNow);
        terminal.Resolve([Root(client: CopilotClientKind.Cli)]);
        terminal.Handle(new("sessionStart", "root", time.GetUtcNow(), ""));
        time.Advance(10000);
        terminal.Resolve([], directories: Snapshot(1, time, terminal.DirectoryTargets.Single(), CopilotSessionDirectoryPresence.Missing));
        time.Advance(1);
        terminal.Handle(new("sessionEnd", "root", time.GetUtcNow(), ""));
        var terminalRecovery = terminal.DirectoryTargets.Single();
        terminal.Resolve([], directories: Snapshot(2, time, terminalRecovery, CopilotSessionDirectoryPresence.Present, "Name"));
        Check(terminal.Find("root") is null, "Directory recovery cannot bypass CLI terminal end");
    }

    private static void ResolverRecovery()
    {
        using var files = new Files();
        var time = new Clock();
        using var resolver = new CopilotSessionIdentityResolver(files.Root, time: time);
        var state = new CopilotSessionState(time.GetUtcNow);
        files.App("root");
        resolver.Observe("root");
        state.Handle(new("sessionStart", "root", time.GetUtcNow(), ""));
        void Scan()
        {
            resolver.SetDirectoryTargets(state.DirectoryTargets);
            var identities = resolver.Scan();
            state.Resolve(identities, resolver.LifecycleSnapshot, resolver.DirectorySnapshot);
        }
        Scan();
        var directory = Path.Combine(files.Root, "root");
        File.Delete(Path.Combine(directory, "workspace.yaml"));
        time.Advance(10000);
        Scan();
        Check(state.Find("root") is not null, "Missing metadata file alone never deletes an existing directory");
        Directory.Delete(directory);
        time.Advance(10000);
        Scan();
        Check(state.Find("root") is null, "Real directory deletion removes a published icon");
        files.App("root");
        time.Advance(10000);
        Scan();
        Check(state.Find("root") is null, "Ordinary metadata polling cannot revive a deleted icon");
        time.Advance(1);
        state.Handle(new("preToolUse", "root", time.GetUtcNow(), ""));
        resolver.Observe("root");
        files.Metadata("root", "client_name: github/autopilot\nname: ''\n");
        Scan();
        Check(state.Find("root") is null, "Recovery ignores cached nonempty name when the new file is blank");
        files.Metadata("root", "client_name: github/autopilot\nname: Fresh name\n");
        Scan();
        Check(state.Find("root") is { Name: "Fresh name" }, "New hook plus fresh metadata can recover immediately");
    }

    private static void TrackerDirectoryCleanup()
    {
        using var files = new Files();
        var time = new Clock();
        var dispatcher = ReactorApp.UIDispatcher;
        dispatcher.Drain();
        int uiThread = Environment.CurrentManagedThreadId, probes = 0, pendingMissing = 0;
        files.App("tracked");
        void DrainUntil(Func<bool> condition, string message)
        {
            Check(SpinWait.SpinUntil(() => { dispatcher.Drain(); return condition(); }, TimeSpan.FromSeconds(5)), message);
        }
        CopilotSessionTracker.Start(Logger.For("fixture"), files.Root, time: time, directoryAttributes: path =>
        {
            if (Environment.CurrentManagedThreadId == uiThread) throw new InvalidOperationException("UI file I/O");
            Interlocked.Increment(ref probes);
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { Interlocked.Increment(ref pendingMissing); throw; }
        });
        CopilotSessionTracker.Start(Logger.For("fixture"), files.Root);
        try
        {
            CopilotSessionTracker.HandleHookEvent("sessionStart", """{"sessionId":"tracked"}""", time.GetUtcNow());
            DrainUntil(() => CopilotSessionTracker.Find("tracked") is not null, "Tracker publishes named session");
            Check(probes == 0, "Tracker name creation performs no premature cleanup probe");
            CopilotSessionTracker.Stop();
            var directory = Path.Combine(files.Root, "tracked");
            File.Delete(Path.Combine(directory, "workspace.yaml"));
            Directory.Delete(directory);
            time.Advance(10000);
            DrainUntil(() => CopilotSessionTracker.Find("tracked") is null, "Shared tracker removes missing session at ten seconds");
            Check(probes == 3, "Shared consumers run one root/target/root cleanup");
            files.App("tracked");
            time.Advance(1);
            CopilotSessionTracker.HandleHookEvent("permissionRequest", """{"sessionId":"tracked"}""", time.GetUtcNow());
            DrainUntil(() => CopilotSessionTracker.Find("tracked") is not null, "Tracker restores only after fresh hook");
            var timer = dispatcher.Timers.Last();
            var staleTick = timer.CaptureTick();
            File.Delete(Path.Combine(directory, "workspace.yaml"));
            Directory.Delete(directory);
            time.Advance(10000);
            int previousMissing = pendingMissing;
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref pendingMissing) > previousMissing &&
                dispatcher.PendingCount > 0, TimeSpan.FromSeconds(5)), "Deletion callback queued");
            CopilotSessionTracker.Stop();
            files.App("replacement");
            CopilotSessionTracker.Start(Logger.For("fixture"), files.Root, time: time);
            dispatcher.Drain();
            staleTick();
            Check(CopilotSessionTracker.Sessions.Count == 0 && !timer.Running,
                "Stopped generation's deletion and timer callbacks cannot affect the new tracker");
            CopilotSessionTracker.HandleHookEvent("sessionStart", """{"sessionId":"replacement"}""", time.GetUtcNow());
            DrainUntil(() => CopilotSessionTracker.Find("replacement") is not null, "New tracker operates normally");
        }
        finally
        {
            CopilotSessionTracker.Stop();
            CopilotSessionTracker.Stop();
            dispatcher.Drain();
        }
    }

    private static CopilotSessionDirectorySnapshot Snapshot(long version, Clock time,
        CopilotSessionDirectoryTarget target, CopilotSessionDirectoryPresence presence, string? name = null) =>
        new(version, time.GetUtcNow(), [new(target, presence, name)]);

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(GetTimestamp());
        public void Advance(long milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
    }
}
