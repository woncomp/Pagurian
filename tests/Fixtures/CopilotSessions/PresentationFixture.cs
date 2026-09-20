using System.Text.Json;
using Pagurian.Modules.Copilot;

internal static class PresentationFixture
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var state = new CopilotSessionState(() => Epoch.AddHours(1));
        void Hook(string name, string id, int second, string? tool = null, string? child = null, string? cwd = null) =>
            state.Handle(CopilotHookEvent.Parse(name, JsonSerializer.Serialize(new
            { sessionId = id, timestamp = Epoch.AddSeconds(second), toolName = tool, agentId = child, cwd }),
                Epoch.AddSeconds(second))!);
        CopilotSession Cell() => state.Find("root")!;
        var identities = new[]
        {
            new CopilotSessionIdentity("root", "root", CopilotIdentityKind.AppRoot, "Main",
                CopilotClientKind.App, "github/autopilot", Cwd: @"D:\main\long-project\"),
            new CopilotSessionIdentity("middle", "root", CopilotIdentityKind.AppTaskChild, "Middle",
                CopilotClientKind.App, ParentId: "root"),
            new CopilotSessionIdentity("leaf", "root", CopilotIdentityKind.AppTaskChild, "Leaf",
                CopilotClientKind.App, ParentId: "middle"),
        };
        Hook("sessionStart", "root", 0);
        Hook("permissionRequest", "leaf", 1, "ask_user", cwd: @"D:\child");
        Hook("preToolUse", "middle", 2, "powershell");
        Check(state.Sessions.Count == 0, "Unknown identity must wait");
        state.Resolve(identities);
        Check(Cell().Nodes.Count == 3 && Cell().Nodes.Single(n => n.SessionId == "leaf").ParentId == "middle",
            "Immediate nested ancestry retained");
        Check(Cell().Nodes[0].Status == CopilotSessionStatus.Idle &&
            Cell().PendingStatus == CopilotSessionStatus.Blocked, "Root local state differs from aggregate");
        Check(Cell().ProjectName == "long-project" && Cell().Cwd == identities[0].Cwd, "Child cwd cannot replace main");
        Check(Cell().RecentHooks.Count == 3 && Cell().RecentHooks[0].Name == "preToolUse",
            "Unresolved received history survives attachment");
        Hook("subagentStop", "root", 3, child: "leaf");
        Check(Cell().Nodes.Single(n => n.SessionId == "leaf").Lifecycle == "Completed" &&
            Cell().BlockingSources.Count == 0 && Cell().Nodes.Count == 3, "Completed child retained, not active");
        Hook("subagentStart", "root", 4, child: "leaf");
        Check(Cell().Nodes.Single(n => n.SessionId == "leaf").Lifecycle == "Active", "Multi-turn node resumes");
        for (int i = 5; i < 12; i++) Hook("agentStop", "root", i);
        Check(Cell().RecentHooks.Count == 5 && Cell().RecentHooks[0].At == Epoch.AddSeconds(11),
            "Newest five only");
        Check(Cell().RecentHooks.All(h => h.ToolName == "-" &&
            h.Line == $"{h.At.ToLocalTime():HH:mm:ss} agentStop -"), "Exact local three-field hook line, no inherited tool");
        Hook("preToolUse", "leaf", 12, "bad\r\nname args");
        Check(!Cell().RecentHooks[0].Line.Contains('\n') && Cell().RecentHooks[0].ToolName == "bad__name_args",
            "Hook rows never wrap injected whitespace");
        Hook("sessionEnd", "root", 13);
        Hook("preToolUse", "leaf", 14, "late");
        Check(state.Sessions.Count == 1, "App turn end keeps the root and descendants");
        Hook("sessionStart", "root", 20);
        Check(Cell().Nodes.Count == 3 && Cell().RecentHooks.Count == 5 &&
            Cell().RecentHooks[0].Name == "sessionStart", "App turn start retains historical nodes and hook rows");
        state.Resolve(identities);
        Check(Cell().Nodes.Count == 3 && Cell().RecentHooks.Count == 5, "Turn start preserves conversation history");
        Hook("permissionRequest", "leaf", 21);
        Check(Cell().Nodes.Any(n => n.SessionId == "leaf"), "Fresh child resumes in new root generation");
        Check(Cell().Nodes.Single(n => n.SessionId == "middle").Status == CopilotSessionStatus.Working,
            "Parent turn completion never clears a different source");
        var telemetry = new CopilotSessionDetailsReducer("leaf");
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "call", type = "model.model_call_success", timestamp = Epoch.AddSeconds(21),
            data = new { requestId = "attributed-leaf-call", responseUsage = new
                { prompt_tokens = 100, completion_tokens = 20 }, copilotUsage = new { total_nano_aiu = 1000 } },
        }))) telemetry.Observe(doc.RootElement);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "completion", type = "subagent.completed", timestamp = Epoch.AddSeconds(22), data = new { },
        }))) telemetry.ObserveLifecycle(doc.RootElement, "Completed");
        state.Resolve([identities[2] with { Details = telemetry.Snapshot }]);
        Check(Cell().Nodes.Single(n => n.SessionId == "leaf").Lifecycle == "Completed" &&
            Cell().BlockingSources.Count == 0, "Newer transcript completion excludes active aggregate");
        Check(Cell().GroupDetails.InputTokens == 100 && Cell().GroupDetails.NanoAiu == 1000,
            "Completed child retains attributable usage in group totals");
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "resume", type = "subagent.started", timestamp = Epoch.AddSeconds(22.5), data = new { },
        }))) telemetry.ObserveLifecycle(doc.RootElement, "Active");
        state.Resolve([identities[2] with { Details = telemetry.Snapshot }]);
        Check(Cell().BlockingSources.Count == 0 &&
            Cell().Nodes.Single(n => n.SessionId == "leaf").Status is null,
            "Transcript resume cannot resurrect pre-completion blocker or invent local activity");
        var aggregate = Cell().GroupDetails;
        Hook("permissionRequest", "leaf", 23);
        Check(ReferenceEquals(aggregate, Cell().GroupDetails), "Hook-only change reuses aggregate telemetry");
        state.Resolve([identities[2] with { Details = telemetry.Snapshot }]);
        Check(Cell().Nodes.Single(n => n.SessionId == "leaf").Lifecycle == "Active" &&
            Cell().BlockingSources.Count == 1 &&
            Cell().BlockingSources[0].BlockedSince == Epoch.AddSeconds(23),
            "New permission has its own blocked-since after transcript completion");
        var late = new CopilotSessionState();
        late.Resolve(identities);
        late.Handle(new("sessionStart", "root", Epoch, ""));
        late.Handle(new("permissionRequest", "leaf", Epoch.AddSeconds(21), ""));
        late.Handle(new("permissionRequest", "leaf", Epoch.AddSeconds(23), ""));
        late.Resolve([identities[2] with { Details = telemetry.Snapshot }]);
        Check(late.Find("root")!.BlockingSources.Single().BlockedSince == Epoch.AddSeconds(23),
            "Late completion fences the previous blocker without clearing a newer permission");
        late.Handle(new("permissionRequest", "leaf", Epoch.AddSeconds(21), ""));
        Check(late.Find("root")!.BlockingSources.Single().BlockedSince == Epoch.AddSeconds(23),
            "Stale permission cannot cross the completion fence");

        Check(CopilotProjectName.FromCwd(null) == "Project unavailable", "Missing project");
        Check(CopilotProjectName.FromCwd(@"C:\") == "C:", "Drive root label");
        Check(CopilotProjectName.FromCwd(@"\\server\share\") == "share", "Share root label");
        Check(CopilotProjectName.FromCwd("/work/project/") == "project", "Slash basename");
        Check(CopilotProjectName.FromCwd("/") == "/", "Filesystem root label");

        var fallback = new CopilotSessionState();
        fallback.Resolve([new("cli", "cli", CopilotIdentityKind.Cli, null)]);
        fallback.Handle(new("sessionStart", "cli", Epoch, "", Cwd: @"D:\own"));
        fallback.Handle(new("preToolUse", "cli", Epoch.AddSeconds(2), "", Cwd: @"D:\new"));
        fallback.Handle(new("agentStop", "cli", Epoch.AddSeconds(1), "", Cwd: @"D:\stale"));
        Check(fallback.Find("cli")!.Cwd == @"D:\new", "Timestamped own cwd fallback rejects older delivery");
        fallback.Handle(new("first", "cli", Epoch.AddSeconds(3), ""));
        fallback.Handle(new("second", "cli", Epoch.AddSeconds(3), ""));
        Check(fallback.Find("cli")!.RecentHooks[0].Name == "second", "Arrival sequence breaks timestamp ties");
    }
}
