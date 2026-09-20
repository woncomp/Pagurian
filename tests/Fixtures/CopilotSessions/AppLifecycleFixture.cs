using System.Text.Json;
using Pagurian.Modules.Copilot;

internal static class AppLifecycleFixture
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
    private static int _assertions;
    private static void Check(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static CopilotSessionIdentity Root(string id) =>
        new(id, id, CopilotIdentityKind.AppRoot, id, CopilotClientKind.App, "github/autopilot");
    private static CopilotSessionIdentity Child(string id, string owner = "root") =>
        new(id, owner, CopilotIdentityKind.AppTaskChild, id, CopilotClientKind.App, ParentId: owner);
    private static CopilotAppInstance App(int pid = 1, int second = 0) =>
        new(pid, Time.AddSeconds(second), @"C:\fixture\github.exe");
    private static CopilotAppLifecycleSnapshot Snapshot(long version, int second,
        CopilotAppInstance? app, params (string Id, CopilotArchiveState Archive, int? Loaded)[] evidence) =>
        new(version, Time.AddSeconds(second), evidence.Select(e => new CopilotAppSessionEvidence(
            e.Id, e.Archive, e.Loaded.HasValue ? app : null,
            e.Loaded.HasValue ? Time.AddSeconds(e.Loaded.Value) : null)).ToArray(), []);

    public static void Run()
    {
        CompletionOrdering();
        Visibility();
        RestartAndTelemetry();
        HiddenHookOrdering();
        UnknownArchiveOwnership();
        Console.WriteLine($"  {_assertions} App lifecycle assertions passed");
    }

    private static void CompletionOrdering()
    {
        foreach (bool deferred in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        foreach (var reason in new string?[] { "complete", null, "unknown" })
        {
            var state = new CopilotSessionState(() => Time.AddMinutes(2));
            if (!deferred) state.Resolve([Root("root"), Child("a"), Child("b")]);
            state.Handle(new("permissionRequest", "a", Time.AddSeconds(1), ""));
            state.Handle(new("permissionRequest", "b", Time.AddSeconds(2), ""));
            CopilotHookEvent[] events = [
                new("agentStop", "root", Time.AddSeconds(3), ""),
                new("sessionEnd", "root", Time.AddSeconds(4), "", Reason: reason),
                new("userPromptSubmitted", "root", Time.AddSeconds(5), "")];
            foreach (var hook in reverse ? events.Reverse() : events) state.Handle(hook);
            if (deferred) state.Resolve([Root("root"), Child("a"), Child("b")]);
            Check(state.Find("root")?.BlockingSources.Count == 2, "Completion keeps child blockers");
            Check(state.Find("root")!.Nodes[0].Status == CopilotSessionStatus.Working,
                "Prompt without start survives completion ordering and deferred identity");
            state.Handle(new("sessionEnd", "a", Time.AddSeconds(6), "", Reason: "complete"));
            Check(state.Find("root")!.BlockingSources.Single().SourceId == "b", "Task completion is local");
            state.Handle(new("subagentStart", "root", Time.AddSeconds(7), "", AgentId: "a"));
            Check(state.Find("root")!.BlockingSources.Single().SourceId == "b", "Task resume cannot revive blocker");
        }
        var parsed = CopilotHookEvent.Parse("sessionEnd",
            """{"sessionId":"root","reason":"complete"}""", Time);
        Check(parsed?.Reason == "complete", "Parse reason without changing raw payload");
        foreach (var client in new[] { CopilotClientKind.Cli, CopilotClientKind.VSCode, CopilotClientKind.Other })
        {
            var state = new CopilotSessionState();
            state.Resolve([new("independent", "independent", CopilotIdentityKind.Cli, "", client)]);
            state.Handle(new("sessionStart", "independent", Time, ""));
            state.Handle(new("sessionEnd", "independent", Time.AddSeconds(1), "", Reason: "complete"));
            Check(state.Sessions.Count == 0, "Independent clients retain terminal hook policy");
        }
    }

    private static void Visibility()
    {
        var state = new CopilotSessionState();
        var one = App(); var two = App(2);
        var identities = new[] { Root("root"), Root("shared"), Root("other"), Root("history"), Child("task") };
        state.Resolve(identities, Snapshot(1, 10, one,
            ("root", CopilotArchiveState.Live, 1), ("shared", CopilotArchiveState.Live, 1),
            ("history", CopilotArchiveState.Live, null)));
        state.Resolve([], Snapshot(2, 11, two, ("other", CopilotArchiveState.Live, 2)));
        Check(state.Sessions.Count == 3 && state.Find("history") is null, "Only loaded roots restored");
        Check(state.Sessions.All(s => s.Status == CopilotSessionStatus.Idle), "Restoration initially Idle");
        state.Resolve([], Snapshot(3, 12, null, ("root", CopilotArchiveState.Unknown, null)));
        Check(state.Find("root") is not null, "Unknown DB/process evidence retains visible group");
        state.Resolve([], Snapshot(4, 13, null, ("root", CopilotArchiveState.Live, null)));
        Check(state.Find("root") is not null, "Routine detach without archive retains root");
        state.Resolve([], Snapshot(5, 14, one, ("root", CopilotArchiveState.Archived, 1)));
        Check(state.Find("root") is null && state.Find("shared") is not null, "No-hook archive isolates shared SDK roots");
        state.Handle(new("sessionStart", "root", Time.AddSeconds(15), ""));
        state.Handle(new("permissionRequest", "task", Time.AddSeconds(15), ""));
        state.Resolve(identities, Snapshot(4, 13, one, ("root", CopilotArchiveState.Live, 1)));
        Check(state.Find("root") is null, "Late hook and older snapshot cannot reopen archive");
        state.Resolve([], Snapshot(6, 16, one, ("root", CopilotArchiveState.Live, 1)));
        Check(state.Find("root") is null, "Unarchive with old load cannot reopen");
        state.Resolve([], Snapshot(7, 18, one, ("root", CopilotArchiveState.Live, 17)));
        Check(state.Find("root") is { Status: CopilotSessionStatus.Idle, BlockingSources.Count: 0 },
            "Unarchive with new positive load resets stale task work");
        state.Resolve([], new(8, Time.AddSeconds(20), [], [one]));
        Check(state.Find("root") is null && state.Find("shared") is null && state.Find("other") is not null,
            "No-hook verified exit removes only owning App instance");
        state.Resolve([], Snapshot(9, 21, one, ("root", CopilotArchiveState.Live, 21)));
        Check(state.Find("root") is null, "Exited process evidence never restores even with a fresh lock");
        // Reused PID, new creation time: this is a different App, not the old process.
        state.Resolve([], Snapshot(10, 31, App(1, 25), ("root", CopilotArchiveState.Live, 30)));
        Check(state.Find("root") is not null && state.Find("shared") is null, "PID reuse restores only newly loaded root");
        state.Clear();
        Check(state.Sessions.Count == 0, "Stop clears lifecycle evidence");
    }

    private static void HiddenHookOrdering()
    {
        foreach (int resolution in new[] { 0, 1, 2 })
        foreach (bool archive in new[] { false, true })
        {
            var state = new CopilotSessionState(() => Time.AddMinutes(2));
            var first = App();
            var replacement = archive ? first : App(2, 25);
            state.Resolve([Root("root"), Child("old")],
                Snapshot(1, 2, first, ("root", CopilotArchiveState.Live, 1)));
            state.Handle(new("permissionRequest", "old", Time.AddSeconds(3), ""));
            if (archive)
                state.Resolve([], Snapshot(2, 20, first, ("root", CopilotArchiveState.Archived, 1)));
            else
                state.Resolve([], new(2, Time.AddSeconds(20), [], [first]));
            var children = new[] { Child("blocked"), Child("working"), Child("completed"), Child("resumed") };
            if (resolution == 0) state.Resolve(children);
            state.Handle(new("permissionRequest", "old", Time.AddSeconds(21), ""));
            state.Handle(new("permissionRequest", "root", Time.AddSeconds(32), ""));
            state.Handle(new("permissionRequest", "blocked", Time.AddSeconds(33), ""));
            state.Handle(new("notification", "blocked", Time.AddSeconds(34), ""));
            state.Handle(new("preToolUse", "working", Time.AddSeconds(34), ""));
            state.Handle(new("subagentStart", "root", Time.AddSeconds(32), "", AgentId: "completed"));
            state.Handle(new("permissionRequest", "completed", Time.AddSeconds(33), ""));
            state.Handle(new("subagentStop", "root", Time.AddSeconds(35), "", AgentId: "completed"));
            state.Handle(new("sessionEnd", "resumed", Time.AddSeconds(32), ""));
            state.Handle(new("subagentStart", "root", Time.AddSeconds(34), "", AgentId: "resumed"));
            state.Handle(new("permissionRequest", "resumed", Time.AddSeconds(35), ""));
            state.Handle(new("agentStop", "blocked", Time.AddSeconds(31), ""));
            Check(state.Sessions.Count == 0, "Tentative hooks never show a hidden root");
            state.Resolve(resolution == 1 ? children : [],
                Snapshot(3, 31, replacement, ("root", CopilotArchiveState.Live, 30)));
            if (resolution == 2) state.Resolve(children);
            var root = state.Find("root")!;
            Check(root is not null, "Positive replacement load restores root");
            Check(root!.BlockingSources.Select(b => b.SourceId).Order().SequenceEqual(
                new[] { "blocked", "resumed", "root" }), "Fresh root and child blockers survive hidden delivery");
            Check(root.BlockingSources.Single(b => b.SourceId == "blocked") is
                { EventName: "permissionRequest", OwnerId: "root" } blocker &&
                blocker.BlockedSince == Time.AddSeconds(33) && blocker.EventAt == Time.AddSeconds(33),
                "Irrelevant and stale hooks preserve blocker provenance");
            Check(root.Nodes.Single(n => n.SessionId == "working").Status == CopilotSessionStatus.Working,
                "Fresh child work survives hidden delivery");
            Check(root.Nodes.Single(n => n.SessionId == "completed").Lifecycle == "Completed",
                "Hidden task start/stop ordering remains local");
            Check(root.BlockingSources.All(b => b.SourceId != "old"), "Pre-load tentative work is rejected");
            state.Handle(new("agentStop", "root", Time.AddSeconds(36), ""));
            Check(root.BlockingSources.Count == 2, "Root completion preserves restored child blockers");
            state.Handle(new("preToolUse", "root", Time.AddSeconds(37), ""));
            Check(root.Nodes[0].Status == CopilotSessionStatus.Working && root.BlockingSources.Count == 2,
                "Fresh root work retains sibling permissions");
            state.Resolve([], Snapshot(2, 20, first, ("root", CopilotArchiveState.Archived, 1)));
            Check(state.Find("root") == root, "Stale lifecycle snapshot cannot replace restored generation");
        }
        // Identity can arrive after restoration, with a task lifecycle but no own hook.
        var delayed = new CopilotSessionState();
        delayed.Resolve([Root("root")], Snapshot(1, 2, App(), ("root", CopilotArchiveState.Live, 1)));
        delayed.Resolve([], new(2, Time.AddSeconds(20), [], [App()]));
        delayed.Handle(new("subagentStart", "root", Time.AddSeconds(32), "", AgentId: "late"));
        delayed.Resolve([Child("late")], Snapshot(3, 31, App(2, 25), ("root", CopilotArchiveState.Live, 30)));
        Check(delayed.Find("root")!.Nodes.Single(n => n.SessionId == "late").Status == CopilotSessionStatus.Working,
            "Fresh lifecycle-only child survives deferred identity at restoration");
        var working = new CopilotSessionState();
        working.Resolve([Root("root")], Snapshot(1, 2, App(), ("root", CopilotArchiveState.Live, 1)));
        working.Resolve([], new(2, Time.AddSeconds(20), [], [App()]));
        working.Handle(new("preToolUse", "root", Time.AddSeconds(32), ""));
        working.Handle(new("notification", "root", Time.AddSeconds(33), ""));
        working.Resolve([], Snapshot(3, 31, App(2, 25), ("root", CopilotArchiveState.Live, 30)));
        Check(working.Find("root")!.Nodes[0].Status == CopilotSessionStatus.Working,
            "Fresh root work is retained even when latest hidden hook has no state");
    }

    private static void UnknownArchiveOwnership()
    {
        var state = new CopilotSessionState();
        state.Resolve([Root("root"), Root("history")]);
        state.Handle(new("permissionRequest", "root", Time.AddSeconds(3), ""));
        state.Resolve([], Snapshot(1, 4, App(),
            ("root", CopilotArchiveState.Unknown, 1), ("history", CopilotArchiveState.Unknown, 1)));
        Check(state.Find("root")!.BlockingSources.Count == 1, "Unknown archive binding preserves fresh hooks");
        Check(state.Find("history") is null, "Unknown archive ownership cannot create historical icons");
        state.Resolve([], new(2, Time.AddSeconds(10), [], [App()]));
        Check(state.Find("root") is null, "Confirmed Exit removes hook-created root despite Unknown archive");
        state.Resolve([], Snapshot(3, 11, null, ("root", CopilotArchiveState.Live, null)));
        Check(state.Find("root") is null, "Live unloaded recovery cannot revive exited root");
        state.Handle(new("permissionRequest", "root", Time.AddSeconds(32), ""));
        state.Resolve([], Snapshot(4, 33, App(2, 25), ("root", CopilotArchiveState.Unknown, 30)));
        Check(state.Find("root") is null, "Unknown archive with fresh hook/load still cannot restore hidden root");
        state.Resolve([], Snapshot(5, 34, App(2, 25), ("root", CopilotArchiveState.Live, 30)));
        Check(state.Find("root")!.BlockingSources.Single().EventAt == Time.AddSeconds(32),
            "Live recovery admits retained fresh permission");
        state.Resolve([], Snapshot(6, 44, App(3, 40), ("root", CopilotArchiveState.Unknown, 41)));
        Check(state.Find("root") is { BlockingSources.Count: 0, Status: CopilotSessionStatus.Idle },
            "Ownership binding to replacement while Unknown still resets prior runtime");
        state.Resolve([], new(7, Time.AddSeconds(45), [], [App(3, 40)]));
        Check(state.Find("root") is null, "Unknown replacement process ownership is retained for Exit");
    }

    private static void RestartAndTelemetry()
    {
        var now = Time;
        var state = new CopilotSessionState(() => now);
        var telemetry = new CopilotSessionDetailsReducer("root");
        using (var doc = JsonDocument.Parse("""
            {"id":"call","type":"model.model_call_success","timestamp":"2026-09-11T10:00:01Z",
             "data":{"requestId":"call","responseUsage":{"prompt_tokens":100,"completion_tokens":20}}}
            """)) telemetry.Observe(doc.RootElement);
        state.Resolve([Root("root") with { Details = telemetry.Snapshot }, Child("task")],
            Snapshot(1, 2, App(), ("root", CopilotArchiveState.Live, 1)));
        state.Handle(new("permissionRequest", "root", Time.AddSeconds(3), ""));
        state.Handle(new("permissionRequest", "task", Time.AddSeconds(4), ""));
        state.Resolve([], Snapshot(2, 20, App(2, 10), ("root", CopilotArchiveState.Live, 11)));
        Check(state.Find("root") is { Status: CopilotSessionStatus.Idle, PendingStatus: null, BlockingSources.Count: 0 },
            "New App instance resets stale work and debounce");
        Check(state.Find("root")!.GroupDetails.InputTokens == 100, "Runtime reset preserves conversation usage");
        Check(state.Find("root")!.Nodes.Count == 2, "Completed task history retained");
        state.Handle(new("permissionRequest", "task", Time.AddSeconds(5), ""));
        Check(state.Find("root")!.BlockingSources.Count == 0, "Prior runtime hook rejected");
        state.Handle(new("permissionRequest", "root", Time.AddSeconds(31), ""));
        now = Time.AddSeconds(32);
        state.CommitStableStatuses();
        // Snapshot began earlier than this hook but was delivered later.
        state.Resolve([], Snapshot(3, 30, App(3, 25), ("root", CopilotArchiveState.Live, 26)));
        Check(state.Find("root")!.BlockingSources.Single().SourceId == "root",
            "Fresh hooks beat delayed initial Idle");
        Check(state.Find("root")!.Status == CopilotSessionStatus.Blocked,
            "Initial snapshot cannot overwrite an already committed fresh status");
        Check(state.Find("root")!.GroupDetails.InputTokens == 100, "Fresh state does not change historical telemetry");
        using (var doc = JsonDocument.Parse("""
            {"id":"shutdown","type":"session.shutdown","timestamp":"2026-09-11T10:00:40Z","data":{}}
            """)) telemetry.Observe(doc.RootElement);
        state.Resolve([Root("root") with { Details = telemetry.Snapshot }]);
        Check(state.Find("root") is not null && state.Find("root")!.Nodes[0].Lifecycle != "Ended",
            "Routine root transcript shutdown is not persistent end or tree termination");
        var deferred = new CopilotSessionState();
        deferred.Resolve([], Snapshot(1, 10, App(), ("root", CopilotArchiveState.Archived, 1)));
        deferred.Handle(new("permissionRequest", "root", Time.AddSeconds(2), ""));
        deferred.Resolve([Root("root")]);
        Check(deferred.Find("root") is null, "Archive waits safely for deferred identity");
        var delayed = new CopilotSessionState();
        delayed.Resolve([Root("root")], Snapshot(1, 2, App(), ("root", CopilotArchiveState.Live, 1)));
        delayed.Handle(new("permissionRequest", "root", Time.AddSeconds(3), ""));
        delayed.Handle(new("permissionRequest", "root", Time.AddSeconds(31), ""));
        delayed.Resolve([], Snapshot(2, 30, App(2, 25), ("root", CopilotArchiveState.Live, 26)));
        Check(delayed.Find("root")!.BlockingSources.Single().BlockedSince == Time.AddSeconds(31),
            "Fresh permission does not inherit prior-process blocked-since provenance");
    }
}
