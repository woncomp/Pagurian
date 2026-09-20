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
    ("working child overrides idle parent", AggregatePriority),
    ("child completion and exit preserve the stable owner", ChildLifecycle),
    ("child-only discovery preserves owner after child exit", SyntheticOwnerPreserved),
    ("multi-turn child resumes after subagentStop", ChildResume),
    ("duplicate and older lifecycle cannot undo newer work", Ordering),
    ("unknown lifecycle ordering agrees with resolved delivery", UnknownOrdering),
    ("unknown start-stop preserves later multi-turn resume", UnknownStartStopResume),
    ("child lifecycle ordering survives shuffled delivery", ShuffledLifecycle),
    ("root exit guards children, resolver, and explicit reopen", RootGeneration),
    ("owner exit is terminal regardless of activity delivery order", OwnerExitOrdering),
    ("explicit owner restart survives a delayed older exit", OwnerRestartOrdering),
    ("older child exit still preserves newer permission", ChildExitOrdering),
    ("unknown ended source never publishes after resolution", UnknownEnd),
    ("one-second aggregate debounce is independent of siblings", Debounce),
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
    ("partial client rewrite never publishes an App child cell", PartialClientRewrite),
    ("unreadable metadata retries without payload logging", MetadataIo),
    ("resolver disposal and no post-disposal scans", ResolverDisposal),
    ("sanitized reported root/child replay", ReportedReplay),
    ("tracker stop/start drops queued callbacks and timers", TrackerRestart),
    ("tracker shared consumers retain state until final stop", TrackerConsumers),
    ("hook events rotate into exclusive local app-data JSONL files", HookEventLogRotation),
    ("usage normalization preserves unavailable and unlimited states", UsageFixture.Run),
    ("local telemetry and identity detail scenarios", () => Console.WriteLine($"  {DetailsFixture.Run()} detail scenarios passed")),
    ("session presentation state scenarios", PresentationFixture.Run),
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
    Check(r.Cell().Status == CopilotSessionStatus.Idle);
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
    Check(r.Ends.SequenceEqual(["root"]));
}

static void ChildResume()
{
    var r = new Rig(); r.Group();
    r.Event("preToolUse", "a");
    var cell = r.Cell();
    r.Event("subagentStop", "root", agent: "a");
    r.Advance(1000);
    Check(cell.Status == CopilotSessionStatus.Idle);
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
    r.Event("permissionRequest", "a");
    r.Event("subagentStart", "root", agent: "b");
    r.Group();
    r.State.Handle(staleStart);
    Check(r.State.Sessions.Count == 0 && r.Ends.Count == 1);
    r.Event("permissionRequest", "unknown");
    var beforeReopen = r.Now;
    r.Event("sessionStart", "root");
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
    foreach (var kind in new[] { CopilotIdentityKind.AppRoot, CopilotIdentityKind.Cli })
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
    foreach (var kind in new[] { CopilotIdentityKind.AppRoot, CopilotIdentityKind.Cli })
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
    r.Advance(490);
    Check(cell.Status == CopilotSessionStatus.Idle);
    r.Advance(10);
    Check(cell.Status == CopilotSessionStatus.Blocked);
    r.Event("postToolUse", "a");
    Check(cell.BlockingSources.Count == 0 && cell.Status == CopilotSessionStatus.Blocked);
    r.Advance(500);
    r.Event("permissionRequest", "a");
    Check(cell.PendingStatus is null && cell.BlockingSources.Count == 1);
    r.Advance(1000);
    Check(cell.Status == CopilotSessionStatus.Blocked);
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
    f.Metadata("root", "  client_name: github/autopilot\nname: nested ignored\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "client_name: github/autopilot\nclient_name: cli\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.Unknown);
    f.Metadata("root", "\ufeffclient_name: 'github/autopilot' # comment\nname: 'It''s a fixture'\n");
    Check(f.Identity("root") is { Kind: CopilotIdentityKind.AppRoot, Name: "It's a fixture" });
    f.Metadata("root", "client_name:\nname:\n");
    Check(f.Identity("root").Kind == CopilotIdentityKind.AppRoot);
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
    Check(owner.Status == CopilotSessionStatus.Idle && r.Ends.Count == 0);
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
        State.Resolve([new(source, owner, kind, null)]);
    public CopilotSession Cell(string id = "root") =>
        State.Find(id) ?? throw new InvalidOperationException($"Missing cell {id}");
    public CopilotHookEvent Event(string name, string id, string? agent = null)
    {
        Now = Now.AddMilliseconds(1);
        var hook = CopilotHookEvent.Parse(name, JsonSerializer.Serialize(new
        {
            sessionId = id, agentId = agent, timestamp = Now, toolInput = "fixture-secret",
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
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Pagurian-Copilot-" + Guid.NewGuid().ToString("N"));
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
