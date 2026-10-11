using System.Text.Json;
using Microsoft.UI.Reactor;
using Pagurian.Modules.Copilot;
using Pagurian.Sdk;

Logger.Sink = (_, _, _) => { }; // Never write to the user's unified log.

var tests = new (string Name, Action Run)[]
{
    ("unknown waits without a timeout or lost blocker", UnknownWaits),
    ("App task child never publishes a child cell", ChildBeforeRoot),
    ("App independent session and CLI keep separate cells", IndependentSessions),
    ("CLI subagent lifecycle is details-only", CliLifecycle),
    ("child blocker survives parent and sibling activity", MemberBlockers),
    ("multiple blockers clear only at their emitting source", MultipleBlockers),
    ("ask_user blocks its source until tool completion", AskUserBlocked),
    ("working child overrides idle parent", AggregatePriority),
    ("Done falls back to Idle on acknowledgement", DonePriority),
    ("dismissed cells wait for new owner events", DismissedCellEvents),
    ("sessionStart is not a completion", SessionStartIsNotDone),
    ("App turn completion retains Done status", AppTurnCompletionRetainsDone),
    ("child stop completes to Done", ChildStopCompletesToDone),
    ("child completion and exit preserve the stable owner", ChildLifecycle),
    ("child-only discovery preserves owner after child exit", SyntheticOwnerPreserved),
    ("multi-turn child resumes after subagentStop", ChildResume),
    ("duplicate and older lifecycle cannot undo newer work", Ordering),
    ("unknown lifecycle ordering agrees with resolved delivery", UnknownOrdering),
    ("unknown start-stop preserves later multi-turn resume", UnknownStartStopResume),
    ("child lifecycle ordering survives shuffled delivery", ShuffledLifecycle),
    ("App archive guards children and requires positive loaded reopen", RootGeneration),
    ("CLI owner exit is terminal regardless of activity delivery order", OwnerExitOrdering),
    ("CLI explicit owner restart survives a delayed older exit", OwnerRestartOrdering),
    ("older child exit still preserves newer permission", ChildExitOrdering),
    ("unknown ended source never publishes after resolution", UnknownEnd),
    ("only entering Blocked waits for a stable one-second aggregate", Debounce),
    ("non-Blocked tracker notifications do not wait for a timer", TrackerImmediateStatus),
    ("latest payload retains child source and blocker provenance", Provenance),
    ("transition diagnostics are bounded and payload-free", Diagnostics),
    ("malformed hook fields cannot throw", MalformedHooks),
    ("App-only identity evidence and conflicting claims", IdentityEvidence),
    ("missing and delayed client metadata waits", DelayedMetadata),
    ("discovery attaches mid-session without transcriptPath or start agentId", DiscoverTranscript),
    ("persisted App child overrides transcript task claim", PersistedAppChild),
    ("nested agents resolve without intermediate hooks", NestedTranscripts),
    ("partial and malformed transcript lines recover", PartialTranscript),
    ("incremental transcript budgets and append", IncrementalTranscript),
    ("transcript truncation and replacement recover", ReplacedTranscript),
    ("validated App hints only; cwd and trace never merge", ValidatedHints),
    ("metadata scalar parsing and malformed writes", MetadataParsing),
    ("empty client child does not become a CLI cell", EmptyClientChild),
    ("partial client rewrite never publishes an App child cell", PartialClientRewrite),
    ("unreadable metadata retries without payload logging", MetadataIo),
    ("resolver disposal and no post-disposal scans", ResolverDisposal),
    ("sanitized reported root/child replay", ReportedReplay),
    ("tracker stop/start drops queued callbacks and timers", TrackerRestart),
    ("tracker shared consumers retain state until final stop", TrackerConsumers),
    ("tracker stop fences queued lifecycle restoration", TrackerLifecycleStop),
    ("hook events rotate into exclusive local app-data JSONL files", HookEventLogRotation),
    ("usage normalization preserves unavailable and unlimited states", UsageFixture.Run),
    ("local telemetry and identity detail scenarios", () => Console.WriteLine($"  {DetailsFixture.Run()} detail scenarios passed")),
    ("session presentation state scenarios", PresentationFixture.Run),
    ("App turn/archive/process generation separation", AppLifecycleFixture.Run),
    ("named icon admission and ten-second directory cleanup", DirectoryLifecycleFixture.Run),
};
int passed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error}");
        Environment.ExitCode = 1;
    }
}
Console.WriteLine($"{passed}/{tests.Length} Copilot session regression scenarios passed.");

static void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}

static void DismissedCellEvents()
{
    var r = new Rig();
    r.Group();
    r.Event("sessionStart", "root");
    var session = r.Cell();
    var visibility = new CopilotSessionCellVisibility();
    Check(session.Status == CopilotSessionStatus.Idle && session.EventRevision > 0);
    visibility.Dismiss(session);
    Check(!visibility.ShouldShow(session));
    r.Group();
    r.Advance(1000);
    Check(!visibility.ShouldShow(session), "Metadata reconciliation and timer cannot restore a dismissed cell");
    var previousRevision = session.EventRevision;
    r.Event("preToolUse", "a");
    Check(session.EventRevision > previousRevision && visibility.ShouldShow(session),
        "A child event restores its dismissed owner");
    Check(visibility.DismissedSessions.Count == 0);

    r.Event("agentStop", "a");
    r.State.AcknowledgeDone("root");
    Check(session.Status == CopilotSessionStatus.Idle);
    visibility.Dismiss(session);
    visibility.Forget(new CopilotSession { SessionId = session.SessionId });
    Check(!visibility.ShouldShow(session), "An older session's end cannot clear a newer dismissal");
    visibility.Forget(session);
    Check(visibility.ShouldShow(session), "Ending the dismissed session clears its suppression");

    visibility.Dismiss(session);
    var replacement = new CopilotSession { SessionId = session.SessionId };
    Check(visibility.ShouldShow(replacement), "A new session lifetime is not suppressed by the previous one");
}

static void HookEventLogRotation()
{
    var root = Path.Combine(Environment.CurrentDirectory, "artifacts",
        $"hook-event-log-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Pagurian.Modules.Copilot.HookEventLog.TestLocalAppDataRoot = root;
        var directory = Path.Combine(root, "Pagurian", "Copilot");
        Directory.CreateDirectory(directory);
        var stale = Path.Combine(directory, "hook-events-20000101-000000-000.log");
        File.WriteAllText(stale, "stale\n");
        File.SetCreationTime(stale, DateTime.Now.Date.AddDays(-1));
        var sameDay = Path.Combine(directory, "hook-events-20990101-000000-000.log");
        File.WriteAllText(sameDay, "keep\n");
        var unrelated = Path.Combine(directory, "pagurian.log");
        File.WriteAllText(unrelated, "keep\n");

        Parallel.For(0, 32, i =>
            Pagurian.Modules.Copilot.HookEventLog.Write($"event-{i}", "{\"ok\":true}"));
        Pagurian.Modules.Copilot.HookEventLog.CloseForTests();

        var files = Directory.GetFiles(directory, "hook-events-*.log");
        Check(files.Length == 2, $"Expected current and same-day files, got {files.Length}");
        Check(!File.Exists(stale));
        Check(File.Exists(sameDay));
        Check(File.Exists(unrelated));
        var current = files.Single(path => !path.Equals(sameDay, StringComparison.OrdinalIgnoreCase));
        Check(System.Text.RegularExpressions.Regex.IsMatch(
            Path.GetFileName(current), @"^hook-events-\d{8}-\d{6}-\d{3}\.log$"));
        var lines = File.ReadAllLines(current);
        Check(lines.Length == 32, "Concurrent writes were lost.");
        foreach (var line in lines)
            using (JsonDocument.Parse(line)) { }
    }
    finally
    {
        Pagurian.Modules.Copilot.HookEventLog.TestLocalAppDataRoot = null;
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static void UnknownWaits()
{
    var r = new Rig();
    r.Event("permissionRequest", "unknown");
    r.Advance(100_000);
    Check(r.State.Sessions.Count == 0);
    r.Resolve("unknown", "unknown", CopilotIdentityKind.Cli);
    Check(r.Cell("unknown").BlockingSources.Single().SourceId == "unknown");
    r.Advance(1000);
    Check(r.Cell("unknown").Status == CopilotSessionStatus.Blocked);
}

static void ChildBeforeRoot()
{
    var r = new Rig();
    r.Event("permissionRequest", "a");
    r.Group();
    Check(r.Starts.SequenceEqual(["root"]));
    Check(r.State.Find("a") is null);
    Check(r.Cell().BlockingSources.Single().OwnerId == "root");
    var cell = r.Cell();
    r.Event("sessionStart", "root");
    Check(ReferenceEquals(cell, r.Cell()));
}

static void IndependentSessions()
{
    var r = new Rig();
    r.Group();
    r.Resolve("independent", "independent", CopilotIdentityKind.AppRoot);
    r.Resolve("cli", "cli", CopilotIdentityKind.Cli);
    r.Event("sessionStart", "root");
    r.Event("sessionStart", "independent");
    r.Event("userPromptSubmitted", "cli");
    r.Event("userPromptSubmitted", "a");
    Check(r.State.Sessions.Count == 3);
    Check(!r.Starts.Contains("a"));
}

static void CliLifecycle()
{
    var r = new Rig();
    r.Resolve("cli", "cli", CopilotIdentityKind.Cli);
    r.Resolve("child", "child", CopilotIdentityKind.Cli);
    r.Event("permissionRequest", "child");
    r.Event("subagentStop", "cli", agent: "child");
    r.Advance(1000);
    Check(r.Cell("child").Status == CopilotSessionStatus.Blocked);
    Check(r.Ends.Count == 0);
    r.Event("agentStop", "child");
    r.Advance(1000);
    Check(r.Cell("child").Status == CopilotSessionStatus.Idle);
    var index = new CopilotSessionIdentityIndex();
    index.SetMetadata("cli", "copilot-cli");
    index.Claim("task", "cli");
    Check(index.Resolve("task").Kind == CopilotIdentityKind.Unknown);
    index.SetMetadata("task", "copilot-cli");
    Check(index.Resolve("task") is { Kind: CopilotIdentityKind.Cli, OwnerId: "task" });
}

static void MemberBlockers()
{
    var r = new Rig(); r.Group();
    r.Event("permissionRequest", "a");
    r.Event("userPromptSubmitted", "root");
    r.Event("preToolUse", "b");
    r.Event("agentStop", "b");
    r.Event("agentStop", "root");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Blocked);
    Check(r.Cell().BlockingSources.Single().SourceId == "a");
}

static void MultipleBlockers()
{
    var r = new Rig(); r.Group();
    foreach (var id in new[] { "root", "a", "b" }) r.Event("permissionRequest", id);
    Check(r.Cell().BlockingSources.Count == 3);
    r.Event("postToolUse", "b");
    Check(r.Cell().BlockingSources.Select(b => b.SourceId).SequenceEqual(["root", "a"]));
    r.Event("agentStop", "root");
    Check(r.Cell().BlockingSources.Single().SourceId == "a");
    r.Event("agentStop", "a");
    Check(r.Cell().BlockingSources.Count == 0);
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Working);
}

static void AskUserBlocked()
{
    foreach (var kind in new[] { CopilotIdentityKind.AppRoot, CopilotIdentityKind.Cli })
    foreach (var completion in new[] { "postToolUse", "postToolUseFailure" })
    foreach (bool deferred in new[] { false, true })
    {
        var r = new Rig();
        if (!deferred) r.Resolve("root", "root", kind);
        var ask = r.Event("preToolUse", "root", tool: "ask_user");
        Check(ask.ToolName == "ask_user", "Hook parser preserves the tool name");
        if (deferred)
        {
            Check(r.State.Sessions.Count == 0);
            r.Resolve("root", "root", kind);
        }
        Check(r.Cell().BlockingSources.Single() is { SourceId: "root", EventName: "preToolUse" } blocker &&
            blocker.BlockedSince == ask.At, "ask_user supplies blocker provenance without a permission hook");
        r.Advance(1000);
        Check(r.Cell().Status == CopilotSessionStatus.Blocked);
        r.Event(completion, "root", tool: "ask_user");
        Check(r.Cell().BlockingSources.Count == 0 && r.Cell().Status == CopilotSessionStatus.Working &&
            r.Cell().PendingStatus is null, "Tool completion returns to Working without waiting for a timer");
        r.State.Handle(ask);
        Check(r.Cell().BlockingSources.Count == 0, "An older ask cannot undo tool completion");
        r.Advance(1000);
        Check(r.Cell().Status == CopilotSessionStatus.Working);
    }

    var group = new Rig(); group.Group();
    var childAsk = group.Event("preToolUse", "a", tool: "ask_user");
    group.Advance(500);
    group.Event("preToolUse", "b", tool: "powershell");
    group.Event("postToolUse", "root", tool: "ask_user");
    group.Event("preToolUse", "a", tool: "ask_user");
    group.Advance(500);
    Check(group.Cell().Status == CopilotSessionStatus.Blocked &&
        group.Cell().BlockingSources.Single().BlockedSince == childAsk.At,
        "Sibling work and repeated asks preserve the stable child blocker and debounce");
    group.Event("permissionRequest", "b");
    group.Event("postToolUse", "a", tool: "ask_user");
    Check(group.Cell().BlockingSources.Single().SourceId == "b",
        "Answering one source cannot clear another source's permission");
    group.Event("postToolUseFailure", "b");
    Check(group.Cell().BlockingSources.Count == 0);

    var tied = new Rig(); tied.Group();
    var work = tied.Event("preToolUse", "root", tool: "powershell");
    var waiting = work with { ToolName = "ask_user" };
    tied.State.Handle(waiting);
    tied.State.Handle(work);
    Check(tied.Cell().BlockingSources.Count == 1,
        "At equal preToolUse timestamps, a waiting tool wins over ordinary work");
    tied.Event("permissionRequest", "root");
    Check(tied.Cell().BlockingSources.Single().BlockedSince == waiting.At,
        "An additional permission hook keeps the original waiting start");
    tied.Event("preToolUse", "root", tool: "powershell");
    Check(tied.Cell().BlockingSources.Count == 0, "Newer ordinary work keeps its existing unblock behavior");
    tied.Event("preToolUse", "root");
    Check(tied.Cell().BlockingSources.Count == 0, "Missing tool name is not an ask_user signal");
    tied.Advance(1000);
    Check(tied.Cell().Status == CopilotSessionStatus.Working);
}

static void AggregatePriority()
{
    var r = new Rig(); r.Group();
    r.Event("sessionStart", "root");
    r.Event("preToolUse", "a");
    r.Event("agentStop", "root");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Working);
    r.Event("agentStop", "a");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Done, "Working -> Idle completes to Done");
    Check(r.State.AcknowledgeDone("root") && r.Cell().Status == CopilotSessionStatus.Idle,
        "Opening the billboard acknowledges Done back to Idle");
    r.Event("agentStop", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Idle, "A repeated stop without new work stays Idle");
    r.Event("userPromptSubmitted", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Working);
    r.Event("agentStop", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Done, "New work then completion re-enters Done");
    Check(r.State.AcknowledgeDone("root"), "The fresh completion is acknowledged");
}

static void DonePriority()
{
    var r = new Rig(); r.Group();
    r.Event("preToolUse", "a");
    r.Event("agentStop", "a");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Done);
    r.Event("permissionRequest", "b");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Blocked, "Blocked wins over Done");
    r.Event("postToolUse", "b");
    Check(r.Cell().Status == CopilotSessionStatus.Working, "Working wins over Done");
    r.Event("agentStop", "b");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Done, "Another completion re-enters Done");
    r.Event("sessionEnd", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Done, "Session end also completes to Done");
}

static void SessionStartIsNotDone()
{
    var r = new Rig(); r.Group();
    r.Event("preToolUse", "a");
    r.Event("agentStop", "a");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Done);
    r.Event("sessionStart", "a");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Idle, "A fresh session start is not a completion");
    Check(r.State.AcknowledgeDone("root") == false, "Nothing pending after the fresh start");
}

static void AppTurnCompletionRetainsDone()
{
    var r = new Rig(); r.Group();
    r.Event("userPromptSubmitted", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Working);
    r.Event("agentStop", "root");
    Check(r.Cell().Status == CopilotSessionStatus.Done);
    r.Event("sessionEnd", "root"); // app-turn-complete deactivates root, but Done must be retained
    Check(r.Cell().Status == CopilotSessionStatus.Done, "App turn completion retains Done status");
    Check(r.State.AcknowledgeDone("root") && r.Cell().Status == CopilotSessionStatus.Idle,
        "Acknowledging turns Done back to Idle");
}

static void ChildStopCompletesToDone()
{
    var r = new Rig(); r.Group();
    r.Event("sessionStart", "root");
    r.Event("preToolUse", "a");
    Check(r.Cell().Status == CopilotSessionStatus.Working);
    r.Event("agentStop", "a");
    Check(r.Cell().Status == CopilotSessionStatus.Done, "Child agentStop completes to Done");
    Check(r.State.AcknowledgeDone("root") && r.Cell().Status == CopilotSessionStatus.Idle,
        "Acknowledging turns Done back to Idle");
}

static void ChildLifecycle()
{
    var r = new Rig(); r.Group();
    r.Event("permissionRequest", "root");
    var cell = r.Cell();
    r.Event("permissionRequest", "a");
    r.Event("agentStop", "a");
    r.Event("subagentStop", "root", agent: "a");
    r.Event("sessionEnd", "a");
    Check(ReferenceEquals(cell, r.Cell()));
    Check(r.Ends.Count == 0);
    Check(cell.BlockingSources.Single().SourceId == "root");
    r.Event("sessionEnd", "root");
    Check(r.Ends.Count == 0 && ReferenceEquals(cell, r.Cell()));
    Check(cell.BlockingSources.Count == 0);
}

static void ChildResume()
{
    var r = new Rig(); r.Group();
    r.Event("preToolUse", "a");
    var cell = r.Cell();
    r.Event("subagentStop", "root", agent: "a");
    r.Advance(1000);
    // The lifecycle record arrived before the child's identity resolved, so
    // the deferred stop cannot mark a completion; a later resolved one can.
    Check(cell.Status == CopilotSessionStatus.Idle, "Deferred stop cannot mark a completion");
    r.Event("subagentStart", "root", agent: "a");
    r.Event("preToolUse", "a");
    r.Event("subagentStop", "root", agent: "a");
    r.Advance(1000);
    // Each turn's lifecycle is recorded at the same owner timestamp in this
    // fixture, so only the last lifecycle state survives; completion still
    // needs a distinct per-source working stop.
    Check(cell.Nodes.Single(n => n.SessionId == "a").Lifecycle == "Completed",
        "The resolved stop completes the child's lifecycle");
    if (cell.Status == CopilotSessionStatus.Done)
        Check(r.State.AcknowledgeDone("root") && cell.Status == CopilotSessionStatus.Idle,
            "A pending completion acknowledges back to Idle");
    else
        Check(cell.Status == CopilotSessionStatus.Idle, "No pending completion without a working stop");
    r.Event("permissionRequest", "a");
    r.Advance(1000);
    Check(ReferenceEquals(cell, r.Cell()) && cell.Status == CopilotSessionStatus.Blocked);
    r.Event("subagentStop", "root", agent: "a");
    r.Event("subagentStart", "root", agent: "a");
    r.Advance(1000);
    Check(cell.Status == CopilotSessionStatus.Working && r.Starts.Count == 1);
}

static void SyntheticOwnerPreserved()
{
    var r = new Rig(); r.Group();
    r.Event("permissionRequest", "a");
    var owner = r.Cell();
    r.Event("sessionEnd", "a");
    Check(ReferenceEquals(owner, r.Cell()) && r.Ends.Count == 0);
    Check(owner.BlockingSources.Count == 0);
    r.State.Clear();
    r.State.Clear();
    Check(r.Ends.SequenceEqual(["root"]));
}

static void Ordering()
{
    var r = new Rig(); r.Group();
    var old = r.Event("subagentStop", "root", agent: "a");
    var newer = r.Event("preToolUse", "a");
    r.State.Handle(old);
    r.State.Handle(newer);
    r.State.Handle(newer with { Name = "agentStop" }); // equal timestamp loses to work
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Working);
    r.State.Handle(old with { Name = "sessionEnd", SourceId = "a", AgentId = null });
    Check(r.Ends.Count == 0);
    r.Event("permissionRequest", "a");
    r.State.Handle(newer); // older work must not clear permission
    Check(r.Cell().BlockingSources.Count == 1);
}

static void RootGeneration()
{
    var r = new Rig(); r.Group();
    r.Event("preToolUse", "a");
    var first = r.Cell();
    var staleStart = r.Event("sessionStart", "root");
    r.Event("sessionEnd", "root");
    r.State.Resolve([], new(1, r.Now, [new("root", CopilotArchiveState.Archived)], []));
    r.Event("permissionRequest", "a");
    r.Event("subagentStart", "root", agent: "b");
    r.Group();
    r.State.Handle(staleStart);
    Check(r.State.Sessions.Count == 0 && r.Ends.Count == 1);
    r.Event("permissionRequest", "unknown");
    var beforeReopen = r.Now;
    r.Event("sessionStart", "root");
    Check(r.State.Sessions.Count == 0);
    r.State.Resolve([], new(2, r.Now, [new("root", CopilotArchiveState.Live,
        new(123, r.Now, @"C:\fixture\github.exe"), r.Now)], []));
    r.Resolve("unknown", "root", CopilotIdentityKind.AppTaskChild);
    Check(r.Cell().BlockingSources.Count == 0);
    Check(!ReferenceEquals(first, r.Cell()));
    r.State.Handle(new("permissionRequest", "b", beforeReopen, "{}"));
    Check(r.Cell().BlockingSources.Count == 0);
    r.Event("preToolUse", "a");
    r.Advance(1000);
    Check(r.Cell().Status == CopilotSessionStatus.Working);
}

static void UnknownOrdering()
{
    foreach (bool resolveEarly in new[] { true, false })
    {
        var r = new Rig();
        if (resolveEarly) r.Group();
        var time = r.Now;
        r.State.Handle(new("subagentStart", "root", time.AddSeconds(10), "{}", AgentId: "a"));
        r.State.Handle(new("sessionEnd", "a", time.AddSeconds(5), "{}"));
        r.State.Handle(new("permissionRequest", "a", time.AddSeconds(11), "{}"));
        if (!resolveEarly) r.Group();
        Check(r.Cell().BlockingSources.Single().SourceId == "a", $"early={resolveEarly}");
        r.State.Handle(new("subagentStop", "root", time.AddSeconds(9), "{}", AgentId: "a"));
        Check(r.Cell().BlockingSources.Count == 1);
    }
    var ended = new Rig();
    ended.Event("sessionEnd", "cli");
    ended.Event("permissionRequest", "cli");
    ended.Resolve("cli", "cli", CopilotIdentityKind.Cli);
    Check(ended.State.Sessions.Count == 0, "unknown CLI was revived without a start");
    ended.Event("sessionStart", "cli");
    Check(ended.Cell("cli").BlockingSources.Count == 0);
}

static void OwnerExitOrdering()
{
    foreach (var kind in new[] { CopilotIdentityKind.Cli })
    foreach (bool exitFirst in new[] { true, false })
    foreach (bool initialStart in new[] { false, true })
    foreach (int resolveAfter in new[] { 0, 1, 2 })
    {
        var r = new Rig();
        var time = r.Now;
        string context = $"{kind}, exitFirst={exitFirst}, initialStart={initialStart}, resolveAfter={resolveAfter}";
        if (initialStart)
            r.State.Handle(new("sessionStart", "root", time, "{}"));
        CopilotHookEvent[] hooks = [
            new("sessionEnd", "root", time.AddSeconds(10), "{}"),
            new("permissionRequest", "root", time.AddSeconds(20), "{}")];
        if (!exitFirst) Array.Reverse(hooks);
        for (int i = 0; i <= hooks.Length; i++)
        {
            if (i == resolveAfter) r.Resolve("root", "root", kind);
            if (i < hooks.Length) r.State.Handle(hooks[i]);
        }
        Check(r.State.Sessions.Count == 0, context);
        Check(r.Starts.Count == r.Ends.Count, $"unbalanced lifecycle: {context}");
        int starts = r.Starts.Count;
        r.State.Handle(new("permissionRequest", "late-child", time.AddSeconds(30), "{}"));
        r.Resolve("late-child", "root", CopilotIdentityKind.AppTaskChild);
        r.Resolve("root", "root", kind); // late metadata must not resurrect the owner
        r.State.Handle(new("preToolUse", "root", time.AddSeconds(40), "{}"));
        r.State.Handle(new("sessionStart", "root", time.AddSeconds(10), "{}")); // equal is not newer
        r.Advance(1000);
        Check(r.State.Sessions.Count == 0 && r.Starts.Count == starts, $"resurrection: {context}");
        // A later explicit restart supersedes the exit, not the discarded
        // activity watermark. Valid new-generation activity may precede t20.
        r.State.Handle(new("sessionStart", "root", time.AddSeconds(15), "{}"));
        r.State.Handle(new("permissionRequest", "root", time.AddSeconds(17), "{}"));
        r.Advance(1000);
        Check(r.Cell().Status == CopilotSessionStatus.Blocked &&
            r.Cell().BlockingSources.Single().SourceId == "root", $"reopened activity: {context}");
        r.State.Handle(new("sessionEnd", "root", time.AddSeconds(50), "{}"));
        r.State.Handle(new("sessionEnd", "root", time.AddSeconds(60), "{}")); // while already ended
        r.State.Handle(new("sessionStart", "root", time.AddSeconds(55), "{}"));
        Check(r.State.Sessions.Count == 0, $"newest exit lost: {context}");
    }
}

static void OwnerRestartOrdering()
{
    int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
    foreach (var kind in new[] { CopilotIdentityKind.Cli })
    foreach (var order in orders)
    foreach (int resolveAfter in new[] { 0, 1, 2, 3 })
    {
        var r = new Rig();
        var time = r.Now;
        string context = $"{kind}, order={string.Join(',', order)}, resolveAfter={resolveAfter}";
        // Child state from before the restart must not cross the generation,
        // even if the older exit is the last hook delivered.
        r.State.Handle(new("permissionRequest", "old-child", time.AddSeconds(15), "{}"));
        CopilotHookEvent[] hooks = [
            new("sessionEnd", "root", time.AddSeconds(10), "{}"),
            new("sessionStart", "root", time.AddSeconds(20), "{}"),
            new("permissionRequest", "root", time.AddSeconds(30), "{}")];
        CopilotSession? beforeExit = null;
        for (int i = 0; i <= order.Length; i++)
        {
            if (i == resolveAfter) r.Resolve("root", "root", kind);
            if (i < order.Length)
            {
                if (order[i] == 0) beforeExit = r.State.Find("root");
                r.State.Handle(hooks[order[i]]);
            }
        }
        var reopened = r.Cell();
        if (order[^1] == 0 && beforeExit is not null)
            Check(ReferenceEquals(beforeExit, reopened), $"stale exit replaced owner: {context}");
        r.Resolve("old-child", "root", CopilotIdentityKind.AppTaskChild);
        Check(reopened.BlockingSources.All(b => b.SourceId == "root"), $"old child crossed restart: {context}");
        r.State.Handle(hooks[0]); // duplicate older exit remains harmless
        Check(ReferenceEquals(reopened, r.Cell()), $"duplicate exit: {context}");
        r.State.Handle(new("permissionRequest", "root", time.AddSeconds(40), "{}"));
        r.Advance(1000);
        Check(reopened.Status == CopilotSessionStatus.Blocked &&
            reopened.BlockingSources.Single().SourceId == "root", context);
    }
}

static void ChildExitOrdering()
{
    foreach (bool resolveEarly in new[] { true, false })
    {
        var r = new Rig();
        if (resolveEarly) r.Group();
        var time = r.Now;
        r.State.Handle(new("permissionRequest", "a", time.AddSeconds(20), "{}"));
        r.State.Handle(new("sessionEnd", "a", time.AddSeconds(10), "{}"));
        if (!resolveEarly) r.Group();
        r.Advance(1000);
        Check(r.Cell().Status == CopilotSessionStatus.Blocked &&
            r.Cell().BlockingSources.Single().SourceId == "a");
    }
}

static void UnknownEnd()
{
    var r = new Rig();
    r.Event("permissionRequest", "a");
    r.Event("sessionEnd", "a");
    r.Group();
    Check(r.Starts.Count == 0);
    r.Event("sessionStart", "root");
    Check(r.Cell().BlockingSources.Count == 0);
}

static void UnknownStartStopResume()
{
    var r = new Rig();
    var time = r.Now;
    r.State.Handle(new("subagentStart", "root", time.AddSeconds(10), "{}", AgentId: "a"));
    r.State.Handle(new("sessionEnd", "a", time.AddSeconds(5), "{}"));
    r.State.Handle(new("permissionRequest", "a", time.AddSeconds(11), "{}"));
    r.State.Handle(new("subagentStop", "root", time.AddSeconds(12), "{}", AgentId: "a"));
    r.Group();
    Check(r.Cell().BlockingSources.Count == 0);
    r.State.Handle(new("permissionRequest", "a", time.AddSeconds(13), "{}"));
    Check(r.Cell().BlockingSources.Single().SourceId == "a");
}

static void ShuffledLifecycle()
{
    var random = new Random(17);
    for (int iteration = 0; iteration < 80; iteration++)
    {
        var r = new Rig();
        bool early = iteration % 2 == 0;
        if (early) r.Group();
        var time = r.Now;
        CopilotHookEvent[] hooks = [
            new("sessionEnd", "a", time.AddSeconds(5), "{}"),
            new("subagentStart", "root", time.AddSeconds(10), "{}", AgentId: "a"),
            new("subagentStop", "root", time.AddSeconds(12), "{}", AgentId: "a"),
            new("permissionRequest", "a", time.AddSeconds(13), "{}")];
        foreach (var hook in hooks.OrderBy(_ => random.Next()))
            r.State.Handle(hook);
        if (!early) r.Group();
        Check(r.Cell().BlockingSources.Single().SourceId == "a", $"shuffle {iteration}");
    }
}

static void Debounce()
{
    foreach (var initial in new[] { CopilotSessionStatus.Idle, CopilotSessionStatus.Working })
    {
        var boundary = new Rig(); boundary.Group();
        boundary.Event(initial == CopilotSessionStatus.Idle ? "sessionStart" : "preToolUse", "root");
        var candidate = boundary.Cell();
        Check(candidate.Status == initial && candidate.PendingStatus is null, "Initial non-Blocked state is immediate");
        boundary.Event("permissionRequest", "a");
        Check(candidate.Status == initial && candidate.PendingStatus == CopilotSessionStatus.Blocked);
        boundary.Advance(999);
        Check(candidate.Status == initial, "Blocked is not visible before one second");
        boundary.Advance(1);
        Check(candidate.Status == CopilotSessionStatus.Blocked && candidate.PendingStatus is null,
            "Blocked becomes visible at one second");
        boundary.Event("postToolUse", "a");
        Check(candidate.Status == CopilotSessionStatus.Working && candidate.PendingStatus is null,
            "Blocked to Working is immediate");
        boundary.Event("agentStop", "a");
        boundary.Event("agentStop", "root");
        Check(candidate.Status == CopilotSessionStatus.Done && candidate.PendingStatus is null,
            "Working to Idle completes to Done immediately");
        boundary.Event("preToolUse", "root");
        Check(candidate.Status == CopilotSessionStatus.Working && candidate.PendingStatus is null,
            "Idle to Working is immediate");
    }

    foreach (var completion in new[] { "agentStop", "postToolUse", "postToolUseFailure" })
    {
        var cancelled = new Rig(); cancelled.Group();
        cancelled.Event("preToolUse", "root", tool: "ask_user");
        cancelled.Advance(600);
        cancelled.Event(completion, "root", tool: "ask_user");
        var expected = completion == "agentStop" ? CopilotSessionStatus.Idle : CopilotSessionStatus.Working;
        Check(cancelled.Cell().Status == expected && cancelled.Cell().PendingStatus is null,
            "Leaving a pending Blocked candidate cancels it immediately");
        cancelled.Advance(500);
        Check(cancelled.Cell().Status == expected, "Timer cannot commit a cancelled candidate");
        cancelled.Event("preToolUse", "root", tool: "ask_user");
        var restartedAt = cancelled.Now;
        Check(cancelled.Cell().PendingStatusSince == restartedAt, "Re-entry starts a fresh interval");
        cancelled.Advance(999);
        Check(cancelled.Cell().Status == expected, "Re-entry does not inherit time from the cancelled interval");
        cancelled.Advance(1);
        Check(cancelled.Cell().Status == CopilotSessionStatus.Blocked);
        cancelled.Event(completion, "root", tool: "ask_user");
        Check(cancelled.Cell().Status == expected && cancelled.Cell().PendingStatus is null,
            "Leaving committed Blocked is also immediate");
    }

    var r = new Rig(); r.Group();
    r.Event("permissionRequest", "a");
    var cell = r.Cell();
    var since = cell.PendingStatusSince;
    Check(cell.Status == CopilotSessionStatus.Idle && cell.BlockingSources.Count == 1);
    r.Advance(500);
    r.Event("preToolUse", "b");
    r.Event("agentStop", "root");
    r.Event("permissionRequest", "a"); // repeat preserves blocked-since
    Check(cell.PendingStatusSince == since);
    r.Event("permissionRequest", "b");
    r.Event("postToolUse", "a");
    Check(cell.PendingStatusSince == since && cell.BlockingSources.Single().SourceId == "b",
        "Continuous aggregate blocking retains its timer when blocker ownership changes");
    r.Advance((int)(since.AddMilliseconds(999) - r.Now).TotalMilliseconds);
    Check(cell.Status == CopilotSessionStatus.Idle);
    r.Advance(1);
    Check(cell.Status == CopilotSessionStatus.Blocked);
    r.Event("permissionRequest", "a");
    r.Event("postToolUse", "b");
    Check(cell.PendingStatus is null && cell.BlockingSources.Single().SourceId == "a" &&
        cell.Status == CopilotSessionStatus.Blocked, "Another active blocker prevents unblocking");
    r.Event("postToolUse", "a");
    Check(cell.BlockingSources.Count == 0 && cell.Status == CopilotSessionStatus.Working &&
        cell.PendingStatus is null, "The last blocker clearing immediately shows Working");
}

static void TrackerImmediateStatus()
{
    using var files = new Files();
    files.App("root");
    var dispatcher = ReactorApp.UIDispatcher;
    dispatcher.Drain();
    var time = new TrackerClock();
    CopilotSessionStatus? notified = null;
    int notifications = 0;
    void Changed()
    {
        notified = CopilotSessionTracker.Find("root")?.Status;
        notifications++;
    }
    CopilotSessionTracker.UiChanged += Changed;
    CopilotSessionTracker.Start(Logger.For("fixture"), files.Root, time: time);
    try
    {
        CopilotSessionTracker.HandleHookEvent("preToolUse",
            """{"sessionId":"root","toolName":"powershell"}""", time.GetUtcNow());
        Check(SpinWait.SpinUntil(() =>
        {
            dispatcher.Drain(); return CopilotSessionTracker.Find("root") is not null;
        }, TimeSpan.FromSeconds(5)), "Named tracker session was not published");
        Check(notified == CopilotSessionStatus.Working, "First publication already notifies Working without a timer");
        var timer = dispatcher.Timers.Last();
        time.Advance(1);
        CopilotSessionTracker.HandleHookEvent("preToolUse",
            """{"sessionId":"root","toolName":"ask_user"}""", time.GetUtcNow());
        Check(notified == CopilotSessionStatus.Working, "Pending Blocked retains the visible Working status");
        time.Advance(999);
        timer.Fire();
        Check(notified == CopilotSessionStatus.Working);
        time.Advance(1);
        timer.Fire();
        Check(notified == CopilotSessionStatus.Blocked);
        int before = notifications;
        time.Advance(1);
        CopilotSessionTracker.HandleHookEvent("postToolUse",
            """{"sessionId":"root","toolName":"ask_user"}""", time.GetUtcNow());
        Check(notified == CopilotSessionStatus.Working && notifications == before + 1,
            "Hook processing itself notifies Working, without firing or draining the timer");
        time.Advance(1);
        CopilotSessionTracker.HandleHookEvent("agentStop", """{"sessionId":"root"}""", time.GetUtcNow());
        Check(notified == CopilotSessionStatus.Done && notifications == before + 2,
            "Hook processing itself notifies Done");
        CopilotSessionTracker.AcknowledgeDone("root");
        dispatcher.Drain();
        Check(notified == CopilotSessionStatus.Idle && notifications == before + 3,
            "Acknowledgement returns Done to Idle without a timer tick");
        time.Advance(1000);
        timer.Fire();
        Check(notified == CopilotSessionStatus.Idle && notifications == before + 3,
            "No pending transition remains for a later timer tick");
    }
    finally
    {
        CopilotSessionTracker.UiChanged -= Changed;
        CopilotSessionTracker.Stop();
        dispatcher.Drain();
    }
}

static void Provenance()
{
    var r = new Rig(); r.Group();
    r.Event("permissionRequest", "a");
    var blocker = r.Cell().BlockingSources.Single();
    r.Event("preToolUse", "b");
    var cell = r.Cell();
    Check(cell.LastEventSourceId == "b" && cell.BlockingSources.Single() == blocker);
    using var json = JsonDocument.Parse(cell.LastEventDump);
    Check(json.RootElement.GetProperty("payload").GetProperty("sessionId").GetString() == "b");
    r.Event("subagentStop", "root", agent: "b");
    using var stop = JsonDocument.Parse(cell.LastEventDump);
    Check(stop.RootElement.GetProperty("payload").GetProperty("sessionId").GetString() == "root");
    Check(stop.RootElement.GetProperty("payload").GetProperty("agentId").GetString() == "b");
    Check(cell.BlockingSources.Single() == blocker);
}

static void Diagnostics()
{
    var r = new Rig(); r.Group();
    for (int i = 0; i < 500; i++)
        r.Event(i % 2 == 0 ? "permissionRequest" : "postToolUse", "a");
    Check(r.State.Transitions.Count == 256 && r.Cell().Transitions.Count <= 64);
    Check(r.State.Transitions.All(t => t.SourceId == "a" && t.OwnerId == "root"));
    Check(!JsonSerializer.Serialize(r.State.Transitions).Contains("fixture-secret", StringComparison.Ordinal));
    r.State.Clear();
    Check(r.State.Transitions.Count == 0 && r.State.Sessions.Count == 0);
}

static void MalformedHooks()
{
    foreach (var json in new[] { "[]", "\"text\"", "{", "null", """{"sessionId":5}""",
        """{"sessionId":{}}""", """{"sessionId":"../../oops"}""" })
        Check(CopilotHookEvent.Parse("preToolUse", json, DateTimeOffset.UtcNow) is null);
    var hook = CopilotHookEvent.Parse("preToolUse",
        """{"sessionId":"root","agentId":{},"timestamp":9223372036854775807}""", DateTimeOffset.UnixEpoch);
    Check(hook is { At: var at, AgentId: null } && at == DateTimeOffset.UnixEpoch);
    var numeric = CopilotHookEvent.Parse("preToolUse",
        """{"sessionId":"root","timestamp":1000}""", DateTimeOffset.UnixEpoch);
    Check(numeric?.At == DateTimeOffset.UnixEpoch.AddSeconds(1));
}

static void IdentityEvidence()
{
    var index = new CopilotSessionIdentityIndex();
    index.Observe("child"); index.Claim("child", "root");
    Check(index.Resolve("child").Kind == CopilotIdentityKind.Unknown);
    index.SetMetadata("root", "github/autopilot", "Fixture root");
    Check(index.Resolve("child") is { Kind: CopilotIdentityKind.AppTaskChild, OwnerId: "root" });
    index.Claim("nested", "child");
    Check(index.Resolve("nested").OwnerId == "root");
    index.SetMetadata("child", "github/autopilot", "Independent");
    Check(index.Resolve("child") is { Kind: CopilotIdentityKind.AppRoot, OwnerId: "child" });
    Check(index.Resolve("nested").OwnerId == "child");
    index.SetMetadata("other", "github/autopilot");
    index.Claim("conflict", "root"); index.Claim("conflict", "other");
    Check(index.Resolve("conflict").Kind == CopilotIdentityKind.Unknown);
    index.Claim("cycle-a", "cycle-b"); index.Claim("cycle-b", "cycle-a");
    Check(index.Resolve("cycle-a").Kind == CopilotIdentityKind.Unknown);
}

static void DelayedMetadata()
{
    using var f = new Files();
    f.Resolver.Observe("root");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "name: Late\nclient_name:\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "name: Late\nclient_name: github/autopilot\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "Late" });
}

static void DiscoverTranscript()
{
    using var f = new Files();
    f.App("root");
    f.Transcript("root", Files.Line("subagent.started", "a"));
    f.Resolver.Observe("a"); // No root hook, no transcriptPath, no start agentId.
    Check(f.Identity("a") is { Kind: CopilotIdentityKind.AppTaskChild, OwnerId: "root" });
    var r = new Rig();
    r.Event("subagentStart", "root"); // Missing agentId is not a guessed child.
    r.Event("permissionRequest", "a");
    r.State.Resolve(f.Resolver.Scan());
    Check(r.Starts.SequenceEqual(["root"]) && r.Cell().BlockingSources.Single().SourceId == "a");
}

static void NestedTranscripts()
{
    using var f = new Files();
    f.App("root");
    f.Transcript("root", Files.Line("subagent.started", "middle"));
    f.Transcript("middle", Files.Line("subagent.started", "leaf"));
    f.Resolver.Observe("leaf");
    for (int i = 0; i < 3; i++) f.Resolver.Scan();
    Check(f.Identity("leaf") is { Kind: CopilotIdentityKind.AppTaskChild, OwnerId: "root" });
}

static void PersistedAppChild()
{
    using var f = new Files();
    f.App("root"); f.App("independent");
    f.Transcript("root", Files.Line("subagent.started", "independent"));
    f.Resolver.Observe("root"); f.Resolver.Observe("independent");
    var r = new Rig();
    r.Event("permissionRequest", "root");
    r.Event("preToolUse", "independent");
    r.State.Resolve(f.Resolver.Scan());
    Check(r.State.Sessions.Count == 2);
    Check(r.Cell().BlockingSources.Single().SourceId == "root");
    Check(r.Cell("independent").BlockingSources.Count == 0);
}

static void PartialTranscript()
{
    using var f = new Files();
    f.App("root");
    f.Transcript("root", """{"type":"subagent.started","agentId":"a"}"""); // no newline yet
    f.Resolver.Observe("a");
    Check(f.Identity("a").Kind == CopilotIdentityKind.Unknown);
    f.Append("root", "\ninvalid-json\n" + Files.Line("subagent.completed", "b"));
    f.Resolver.Observe("b");
    Check(f.Identity("a").OwnerId == "root" && f.Identity("b").OwnerId == "root");
    Check(f.Diagnostics.Contains("identity-transcript-malformed"));
}

static void IncrementalTranscript()
{
    using var f = new Files();
    f.App("root");
    f.Transcript("root", string.Concat(Enumerable.Repeat("{\"type\":\"ignored\"}\n", 600)) +
        Files.Line("subagent.started", "a"));
    f.Resolver.Observe("a");
    Check(f.Identity("a").Kind == CopilotIdentityKind.Unknown);
    Check(f.Identity("a").OwnerId == "root"); // next bounded pass continues, not restarts
    f.Append("root", new string('x', 2 * 1024 * 1024 + 1) + "\n" + Files.Line("subagent.started", "b"));
    f.Resolver.Observe("b");
    for (int i = 0; i < 12; i++) f.Resolver.Scan();
    Check(f.Identity("b").OwnerId == "root");
    Check(f.Diagnostics.Contains("identity-transcript-line-too-large"));
}

static void ReplacedTranscript()
{
    using var f = new Files();
    f.App("root");
    f.Transcript("root", Files.Line("subagent.started", "a") + Files.Line("subagent.completed", "a"));
    f.Resolver.Observe("a");
    Check(f.Identity("a").OwnerId == "root");
    f.Transcript("root", Files.Line("subagent.started", "b"));
    f.Resolver.Observe("b");
    Check(f.Identity("b").OwnerId == "root");
    // Same-length replacement must not be mistaken for no append.
    f.Transcript("root", Files.Line("subagent.started", "c"));
    f.Resolver.Observe("c");
    Check(f.Identity("c").OwnerId == "root");
    Check(f.Identity("a").OwnerId == "root"); // immutable identity survives rotation/resume
}

static void ValidatedHints()
{
    using var f = new Files();
    f.App("root");
    f.Metadata("cli", "client_name: copilot-cli\n");
    f.Resolver.Observe("a", Path.Combine(f.Root, "root", "events.jsonl"));
    f.Resolver.Observe("b", Path.Combine(f.Root, "cli", "events.jsonl"));
    f.Resolver.Observe("c", Path.Combine(f.Root, "outside", "nested", "events.jsonl"));
    Check(f.Identity("a").OwnerId == "root");
    Check(f.Identity("b").Kind == CopilotIdentityKind.Unknown);
    Check(f.Identity("c").Kind == CopilotIdentityKind.Unknown);
    var r = new Rig();
    foreach (var id in new[] { "a", "b" })
        r.State.Handle(CopilotHookEvent.Parse("preToolUse",
            JsonSerializer.Serialize(new { sessionId = id, cwd = "same", traceId = "same" }), r.Now)!);
    Check(r.State.Sessions.Count == 0);
}

static void MetadataParsing()
{
    using var f = new Files();
    f.Resolver.Observe("root");
    f.Metadata("root", "client_name: \"github/autopilot\nname: broken\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "outer:\n  client_name: github/autopilot\nname: nested ignored\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.Unknown, Name: "nested ignored" });
    f.Metadata("root", "client_name: github/autopilot\nclient_name: cli\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "\ufeffclient_name: 'github/autopilot' # comment\nname: 'It''s a fixture'\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "It's a fixture" });
    f.Metadata("root", "client_name: github/autopilot\nname: |-\n  World Clock: Add more major cities\n\n  - Allow searching in city selection\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "World Clock: Add more major cities\n\n- Allow searching in city selection" });
    f.Metadata("root", "client_name: github/autopilot\nname: >\n  Folded\n  name\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "Folded name" });
    f.Metadata("root", "client_name: github/autopilot\nname: [broken\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "Folded name" });
    f.Metadata("root", "client_name:\nname:\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.AppRoot);
}

static void EmptyClientChild()
{
    var index = new CopilotSessionIdentityIndex();
    index.SetMetadata("cli", "github/cli", "CLI");
    index.SetMetadata("blank", " ");
    index.Observe("cli");
    index.Observe("child");
    index.Observe("blank");
    index.Claim("child", "cli");
    index.Claim("blank", "cli");

    Check(index.Resolve("child") is
        { Kind: CopilotIdentityKind.Unknown, Client: CopilotClientKind.Unknown },
        "A child without client metadata remains unresolved");
    Check(index.Resolve("blank") is
        { Kind: CopilotIdentityKind.Unknown, Client: CopilotClientKind.Unknown },
        "A child with blank client metadata remains unresolved");
    Check(index.Resolve("cli") is
        { Kind: CopilotIdentityKind.Cli, Client: CopilotClientKind.Cli },
        "The explicit CLI root remains independently classified");

    var state = new CopilotSessionState();
    state.Handle(CopilotHookEvent.Parse("permissionRequest",
        """{"sessionId":"cli"}""", DateTimeOffset.UtcNow)!);
    state.Handle(CopilotHookEvent.Parse("permissionRequest",
        """{"sessionId":"child"}""", DateTimeOffset.UtcNow.AddMilliseconds(1))!);
    state.Resolve(index.ResolveObserved());
    Check(state.Sessions.Count == 1 && state.Find("cli") is not null &&
        state.Find("child") is null, "The empty-client child does not publish a cell");
}

static void MetadataIo()
{
    using var f = new Files();
    f.App("root"); f.Resolver.Observe("root");
    using (var locked = new FileStream(Path.Combine(f.Root, "root", "workspace.yaml"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    Check(f.Identity("root").Kind == CopilotIdentityKind.AppRoot);
    Check(f.Diagnostics.All(d => d.StartsWith("identity-", StringComparison.Ordinal) && !d.Contains(f.Root)));
}

static void PartialClientRewrite()
{
    using var f = new Files();
    f.Metadata("root", "client_name: github/");
    f.Resolver.Observe("a", parentId: "root");
    Check(f.Identity("a").Kind == CopilotIdentityKind.Unknown);
    f.App("root");
    var r = new Rig();
    r.Event("permissionRequest", "a");
    r.State.Resolve(f.Resolver.Scan());
    Check(r.Starts.SequenceEqual(["root"]));
    foreach (var partial in new[] { "github/", "github/\n", "g", "", "garbage\n" })
    {
        f.Metadata("root", "client_name: " + partial);
        r.State.Resolve(f.Resolver.Scan());
        Check(r.Starts.SequenceEqual(["root"]) && r.State.Find("a") is null);
        Check(r.Cell().BlockingSources.Single().SourceId == "a");
    }
}

static void ResolverDisposal()
{
    using var f = new Files();
    f.Resolver.Observe("a");
    f.Resolver.Dispose();
    f.Resolver.Observe("b");
    Check(f.Resolver.Scan().Count == 0);
    bool threw = false;
    try { f.Resolver.Start(_ => { }); } catch (ObjectDisposedException) { threw = true; }
    Check(threw);
}

static void ReportedReplay()
{
    const string root = "ae469598-6e09-4acb-a451-87071c5c391b";
    const string child = "05369e6b-3fa1-47e7-9fd7-3365e6d74393";
    using var f = new Files();
    var r = new Rig();
    f.App(root);
    f.Transcript(root, Files.Line("subagent.started", child));
    r.Event("userPromptSubmitted", child); // early hooks do not carry transcriptPath
    r.Event("permissionRequest", child);
    f.Resolver.Observe(child);
    r.State.Resolve(f.Resolver.Scan());
    r.Event("subagentStart", root); // no agentId
    r.Event("postToolUse", root);
    r.Event("agentStop", root);
    r.Advance(1000);
    Check(r.Starts.SequenceEqual([root]) && r.Cell(root).Status == CopilotSessionStatus.Blocked);
    var owner = r.Cell(root);
    r.Event("subagentStop", root, agent: child);
    r.Advance(1000);
    Check(owner.Status == CopilotSessionStatus.Done && r.Ends.Count == 0);
    Check(r.State.AcknowledgeDone(root), "Acknowledged before the resumed work");
    r.Event("preToolUse", child);
    r.Advance(1000);
    Check(owner.Status == CopilotSessionStatus.Working && r.Starts.Count == 1);
}

static void TrackerRestart()
{
    using var oldFiles = new Files();
    using var newFiles = new Files();
    var dispatcher = ReactorApp.UIDispatcher;
    dispatcher.Drain();
    oldFiles.App("old");
    CopilotSessionTracker.Start(Logger.For("fixture"), oldFiles.Root);
    CopilotSessionTracker.HandleHookEvent("permissionRequest", """{"sessionId":"old"}""", DateTimeOffset.UtcNow);
    Check(SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, TimeSpan.FromSeconds(5)), "resolver didn't enqueue");
    var timer = dispatcher.Timers.Last();
    var staleTick = timer.CaptureTick();
    CopilotSessionTracker.Stop();
    Check(!timer.Running && CopilotSessionTracker.Sessions.Count == 0);
    CopilotSessionTracker.Start(Logger.For("fixture"), newFiles.Root);
    dispatcher.Drain(); staleTick();
    Check(CopilotSessionTracker.Sessions.Count == 0, "stale resolver resurrected old session");
    newFiles.App("new");
    CopilotSessionTracker.HandleHookEvent("permissionRequest", """{"sessionId":"new"}""", DateTimeOffset.UtcNow);
    Check(SpinWait.SpinUntil(() =>
    {
        dispatcher.Drain();
        return CopilotSessionTracker.Find("new") is not null;
    }, TimeSpan.FromSeconds(5)), "new generation didn't resolve");
    Check(CopilotSessionTracker.Find("new")!.BlockingSources.Count == 1);
    CopilotSessionTracker.Stop();
    dispatcher.Drain();
    Check(CopilotSessionTracker.Sessions.Count == 0);
}

static void TrackerConsumers()
{
    using var f = new Files();
    f.App("root");
    var dispatcher = ReactorApp.UIDispatcher;
    CopilotSessionTracker.Start(Logger.For("fixture"), f.Root);
    CopilotSessionTracker.Start(Logger.For("fixture"), f.Root);
    CopilotSessionTracker.HandleHookEvent("preToolUse", """{"sessionId":"root"}""", DateTimeOffset.UtcNow);
    Check(SpinWait.SpinUntil(() =>
    {
        dispatcher.Drain(); return CopilotSessionTracker.Find("root") is not null;
    }, TimeSpan.FromSeconds(5)));
    CopilotSessionTracker.Stop();
    Check(CopilotSessionTracker.Find("root") is not null);
    CopilotSessionTracker.Stop();
    Check(CopilotSessionTracker.Sessions.Count == 0);
}

static void TrackerLifecycleStop()
{
    using var f = new Files();
    f.App("loaded");
    var dispatcher = ReactorApp.UIDispatcher;
    dispatcher.Drain();
    var reader = new FixtureLifecycleReader("loaded");
    CopilotSessionTracker.Start(Logger.For("fixture"), f.Root, reader);
    Check(SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, TimeSpan.FromSeconds(5)),
        "Lifecycle restoration callback was not queued");
    CopilotSessionTracker.Stop();
    dispatcher.Drain();
    Check(CopilotSessionTracker.Sessions.Count == 0, "Queued lifecycle resurrected stopped tracker");
    Check(SpinWait.SpinUntil(() => reader.Disposed, TimeSpan.FromSeconds(5)), "Lifecycle reader was not disposed");
}

sealed class TrackerClock : TimeProvider
{
    private long _milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.Parse("2026-09-18T03:00:00Z").AddMilliseconds(GetTimestamp());
    public void Advance(long milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
}

sealed class Rig
{
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Parse("2026-09-10T06:00:00Z");
    public CopilotSessionState State { get; }
    public List<string> Starts { get; } = [];
    public List<string> Ends { get; } = [];
    public Rig()
    {
        State = new(() => Now);
        State.SessionStarted += session => Starts.Add(session.SessionId);
        State.SessionEnded += session => Ends.Add(session.SessionId);
    }
    public void Group() => State.Resolve([
        new("root", "root", CopilotIdentityKind.AppRoot, "Fixture root"),
        new("a", "root", CopilotIdentityKind.AppTaskChild, "Fixture root"),
        new("b", "root", CopilotIdentityKind.AppTaskChild, "Fixture root")]);
    public void Resolve(string source, string owner, CopilotIdentityKind kind) =>
        State.Resolve([new(source, owner, kind, $"Fixture {source}")]);
    public CopilotSession Cell(string id = "root") =>
        State.Find(id) ?? throw new InvalidOperationException($"Missing cell {id}");
    public CopilotHookEvent Event(string name, string id, string? agent = null, string? tool = null)
    {
        Now = Now.AddMilliseconds(1);
        var hook = CopilotHookEvent.Parse(name, JsonSerializer.Serialize(new
        {
            sessionId = id, agentId = agent, timestamp = Now, toolName = tool, toolInput = "fixture-secret",
        }), Now)!;
        State.Handle(hook);
        return hook;
    }
    public void Advance(int milliseconds)
    {
        Now = Now.AddMilliseconds(milliseconds);
        State.CommitStableStatuses();
    }
}

sealed class Files : IDisposable
{
    public string Root { get; } = Path.Combine(Environment.CurrentDirectory, "artifacts", "Pagurian-Copilot-" + Guid.NewGuid().ToString("N"));
    public List<string> Diagnostics { get; } = [];
    public CopilotSessionIdentityResolver Resolver { get; }
    public Files()
    {
        Directory.CreateDirectory(Root);
        Resolver = new(Root, Diagnostics.Add);
    }

    public void App(string id) => Metadata(id, "client_name: github/autopilot\nname: Fixture\n");
    public void Metadata(string id, string text) => Write(id, "workspace.yaml", text);
    public void Transcript(string id, string text) => Write(id, "events.jsonl", text);
    public void Append(string id, string text) => File.AppendAllText(Path.Combine(Root, id, "events.jsonl"), text);
    private void Write(string id, string file, string text)
    {
        Directory.CreateDirectory(Path.Combine(Root, id));
        File.WriteAllText(Path.Combine(Root, id, file), text);
    }
    public static string Line(string type, string agent) =>
        JsonSerializer.Serialize(new { type, agentId = agent }) + "\n";
    public CopilotSessionIdentity Identity(string id) => Resolver.Scan().Single(i => i.SourceId == id);
    public void Dispose()
    {
        Resolver.Dispose();
        // A production worker never holds files across scans; a brief sharing
        // violation during cancellation is retried only inside the temp fixture.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(Root, true); break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(10); }
        }
    }
}
