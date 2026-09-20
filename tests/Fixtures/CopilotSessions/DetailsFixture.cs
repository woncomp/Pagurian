using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Pagurian.Modules.Copilot;

internal static class DetailsFixture
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    public static int Run()
    {
        var tests = new Action[]
        {
            ClientMarkers, OwnMetadataAndAncestry, NoHistoricalRoots, ConflictingImmediateParents,
            UnavailableIsNotZero, SupportedMetadata, AttributedCalls, MirroredCalls,
            CumulativeCheckpoints, GenerationFiltering, TimestampedLifecycle,
            LargeIncrementalTranscript, ReplacementAndPartialLines, PublicationCoalescing,
            MalformedTelemetry, OverflowIsPartial, CrossSchemaAliases, HintIsNotImmediateParent,
            IndependentMetadataRejectsForeignLifecycle, DocumentedTaskParentAndName,
        };
        foreach (var test in tests)
            test();
        return tests.Length;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static string Event(string type, object data, string id = "event", int seconds = 1, string? agentId = null) =>
        JsonSerializer.Serialize(new { type, data, id, timestamp = Epoch.AddSeconds(seconds), agentId });

    private static void Feed(CopilotSessionDetailsReducer reducer, string json)
    {
        using var document = JsonDocument.Parse(json);
        reducer.Observe(document.RootElement);
    }

    private static string Call(string id, string request, int seconds = 1, long input = 10, string? agent = null) =>
        Event("model.model_call_success", new
        {
            requestId = request, modelCall = new { model = "fixture-model", request_id = request },
            responseUsage = new { prompt_tokens = input, completion_tokens = 3,
                prompt_tokens_details = new { cached_tokens = 4, cache_creation_tokens = 5 } },
            copilotUsage = new { total_nano_aiu = 12345678901234567L },
            reasoningEffort = "high",
        }, id, seconds, agent);

    private static void ClientMarkers()
    {
        Check(CopilotSessionIdentityIndex.ClassifyClient("github/cli") == CopilotClientKind.Cli, "CLI producer constant");
        Check(CopilotSessionIdentityIndex.ClassifyClient("vscode") == CopilotClientKind.VSCode, "VS Code producer marker");
        Check(CopilotSessionIdentityIndex.ClassifyClient("vscode-agent-host") == CopilotClientKind.VSCode, "VS Code agent-host marker");
        Check(CopilotSessionIdentityIndex.ClassifyClient("github/autopilot") == CopilotClientKind.App, "App marker");
        Check(CopilotSessionIdentityIndex.ClassifyClient("cli") == CopilotClientKind.Other, "Legacy fixture string is not the producer marker");
        Check(CopilotSessionIdentityIndex.ClassifyClient(null) == CopilotClientKind.Unknown, "Absent marker");
        var index = new CopilotSessionIdentityIndex();
        index.SetMetadata("independent", "unrecognized-client");
        Check(index.Resolve("independent").Kind == CopilotIdentityKind.Cli, "Unknown explicit clients remain independent owners");
    }

    private static void OwnMetadataAndAncestry()
    {
        var index = new CopilotSessionIdentityIndex();
        index.SetMetadata("root", "github/autopilot", "Main", @"D:\main");
        index.SetMetadata("middle", null, "Middle", @"D:\middle");
        index.SetMetadata("leaf", null, "Leaf");
        index.Claim("middle", "root");
        index.Claim("leaf", "middle");
        index.Observe("leaf");
        var nodes = index.ResolveObserved();
        Check(nodes.Count == 3, "Hookless ancestor must be published");
        var leaf = nodes.Single(node => node.SourceId == "leaf");
        Check(leaf.Name == "Leaf" && leaf.OwnerId == "root" && leaf.ParentId == "middle"
            && leaf.Client == CopilotClientKind.App && leaf.Cwd is null, "Names/cwd are not inherited; parent is immediate");
        Check(nodes.Single(node => node.SourceId == "middle").Cwd == @"D:\middle", "Own cwd retained");
    }

    private static void NoHistoricalRoots()
    {
        var index = new CopilotSessionIdentityIndex();
        index.SetMetadata("historical", "github/autopilot");
        index.Claim("old-task", "historical");
        Check(index.ResolveObserved().Count == 0, "Discovery cannot create historical cells");
        index.Observe("historical");
        Check(index.ResolveObserved().Count == 2, "Observed group includes known hookless tasks");
    }

    private static void ConflictingImmediateParents()
    {
        var index = new CopilotSessionIdentityIndex();
        index.SetMetadata("root", "github/autopilot");
        index.Claim("middle", "root");
        index.Claim("leaf", "middle");
        index.Claim("leaf", "root");
        Check(index.Resolve("leaf").Kind == CopilotIdentityKind.Unknown, "Same-owner conflicting immediate parents are ambiguous");
    }

    private static void UnavailableIsNotZero()
    {
        var empty = CopilotSessionDetails.Empty;
        Check(empty.InputTokens is null && empty.NanoAiu is null && empty.ContextTokens is null, "Missing is unavailable");
        Check(CopilotSessionDetails.Aggregate([empty]).InputTokens is null, "Aggregate missing is unavailable");
    }

    private static void SupportedMetadata()
    {
        var reducer = new CopilotSessionDetailsReducer("root");
        Feed(reducer, Event("session.start", new { selectedModel = "one", reasoningEffort = "low", contextTier = "long_context" }, "start", 0));
        Feed(reducer, Event("model.model_call_started", new { model = "one", modelInfo = new
            { capabilities = new { limits = new { max_context_window_tokens = 400000, max_prompt_tokens = 390000 } } } }, "started"));
        Check(reducer.Snapshot.ContextLimit == 400000 && reducer.Snapshot.ContextTokens is null, "Model limit is available without inventing context occupancy");
        Check(reducer.Snapshot.PromptLimit == 390000, "Reported prompt limit");
        Feed(reducer, Event("session.usage_info", new { currentTokens = 900, tokenLimit = 400000 }, "context", 2));
        Feed(reducer, Event("session.model_change", new { newModel = "two", reasoningEffort = "high", contextTier = "default" }, "model", 3));
        Check(reducer.Snapshot.Model == "two" && reducer.Snapshot.ReasoningEffort == "high"
            && reducer.Snapshot.ContextTier == "default" && reducer.Snapshot.ContextTokens == 900, "Supported metadata fields");
        Check(reducer.Snapshot.ContextLimit is null && reducer.Snapshot.PromptLimit is null, "Model switch invalidates stale limits");
        Feed(reducer, Event("session.compaction_complete", new { success = true, postCompactionTokens = 100, tokenLimit = 400000 }, "compact", 4));
        Check(reducer.Snapshot.ContextTokens == 100, "Compaction reports current context");
    }

    private static void AttributedCalls()
    {
        var reducer = new CopilotSessionDetailsReducer("root");
        Feed(reducer, Call("call1", "request1"));
        Feed(reducer, Call("call2", "request2", 2));
        Check(reducer.Snapshot.InputTokens == 20 && reducer.Snapshot.OutputTokens == 6
            && reducer.Snapshot.CachedTokens == 8 && reducer.Snapshot.CacheCreationTokens == 10
            && reducer.Snapshot.NanoAiu == 24691357802469134L && reducer.Snapshot.IsPartial, "Exact integer per-call totals");
    }

    private static void MirroredCalls()
    {
        var root = new CopilotSessionDetailsReducer("root");
        var child = new CopilotSessionDetailsReducer("child");
        Feed(root, Call("event-root", "same-request"));
        Feed(child, Call("event-child", "same-request"));
        Feed(child, Call("another", "disjoint-request", 2));
        var total = CopilotSessionDetails.Aggregate([root.Snapshot, child.Snapshot]);
        Check(total.InputTokens == 20 && total.NanoAiu == 24691357802469134L, "Mirrored calls deduplicate by stable provider request");
        Check(total.Model is null && total.ContextTokens is null && total.ContextLimit is null, "No additive model/context metadata");
    }

    private static void CrossSchemaAliases()
    {
        var root = new CopilotSessionDetailsReducer("root");
        var child = new CopilotSessionDetailsReducer("child");
        Feed(root, Event("model.model_call_success", new
        {
            requestId = "provider", serviceRequestId = "service",
            modelCall = new { api_id = "completion" },
            responseUsage = new { prompt_tokens = 12, completion_tokens = 3 },
        }, "model"));
        Feed(child, Event("assistant.usage", new
        {
            apiCallId = "completion", model = "fixture", inputTokens = 12, outputTokens = 3,
            copilotUsage = new { totalNanoAiu = 100 },
        }, "assistant"));
        var total = CopilotSessionDetails.Aggregate([root.Snapshot, child.Snapshot]);
        Check(total.InputTokens == 12 && total.NanoAiu == 100, "SDK and model-call records deduplicate by retained completion alias");
    }

    private static void HintIsNotImmediateParent()
    {
        var index = new CopilotSessionIdentityIndex();
        index.SetMetadata("root", "github/autopilot");
        index.HintOwner("leaf", "root");
        Check(index.Resolve("leaf").OwnerId == "root" && index.Resolve("leaf").ParentId is null, "Transcript hint proves owner, not immediate parent");
        index.Claim("middle", "root");
        index.Claim("leaf", "middle");
        Check(index.Resolve("leaf").ParentId == "middle", "Immediate relationship supersedes owner-only hint");
    }

    private static void IndependentMetadataRejectsForeignLifecycle()
    {
        var reducer = new CopilotSessionDetailsReducer("independent");
        using var completion = JsonDocument.Parse(Event("subagent.completed", new { }, "foreign"));
        reducer.ObserveLifecycle(completion.RootElement, "Completed", "other-root");
        Check(reducer.ForIdentity(false).Lifecycle is null, "Independent persisted sessions reject another owner's lifecycle");
        Check(reducer.ForIdentity(true).Lifecycle == "Completed", "Confirmed tasks accept their owner's timestamped lifecycle");
        reducer.RemoveOrigin("other-root");
        Check(reducer.Snapshot.Lifecycle is null, "Replacement removes origin-attributed descendant projections");
    }

    private static void CumulativeCheckpoints()
    {
        var root = new CopilotSessionDetailsReducer("root");
        var child = new CopilotSessionDetailsReducer("child");
        Feed(root, Event("session.usage_checkpoint", new { totalNanoAiu = 100, totalPremiumRequests = 1.25m }, "old"));
        Feed(root, Event("session.usage_checkpoint", new { totalNanoAiu = 150, totalPremiumRequests = 1.5m }, "new", 2));
        Feed(child, Event("session.usage_checkpoint", new { totalNanoAiu = 50, totalPremiumRequests = 0.5m }, "child"));
        Check(root.Snapshot.ReportedNanoAiu == 150 && root.Snapshot.NanoAiu is null,
            "Latest checkpoint supersedes previous checkpoint without pretending to be own usage");
        var total = CopilotSessionDetails.Aggregate([root.Snapshot, child.Snapshot]);
        Check(total.NanoAiu is null && total.PremiumRequests is null && total.IsPartial,
            "Overlapping session checkpoints are not attributable group totals");
    }

    private static void GenerationFiltering()
    {
        var reducer = new CopilotSessionDetailsReducer("root");
        Feed(reducer, Event("session.start", new { selectedModel = "old" }, "start", -20));
        Feed(reducer, Call("old", "old-request", -10));
        Feed(reducer, Call("new", "new-request", 10));
        Feed(reducer, Event("session.usage_checkpoint", new { totalNanoAiu = 999 }, "checkpoint", 20));
        var current = reducer.Snapshot.ForGeneration(Epoch);
        Check(current.InputTokens == 10 && !current.Observations.Any(observation => observation.Kind == "checkpoint"),
            "New timestamp on cumulative counter does not erase its old accounting interval");
        Check(current.Observations.All(observation => observation.At >= Epoch), "No prior generation observations");
    }

    private static void TimestampedLifecycle()
    {
        var reducer = new CopilotSessionDetailsReducer("child");
        using (var completion = JsonDocument.Parse(Event("subagent.completed", new { }, "complete", 3)))
            reducer.ObserveLifecycle(completion.RootElement, "Completed");
        using (var older = JsonDocument.Parse(Event("subagent.started", new { }, "older", 1)))
            reducer.ObserveLifecycle(older.RootElement, "Active");
        Check(reducer.Snapshot.Lifecycle == "Completed" && reducer.Snapshot.LifecycleAt == Epoch.AddSeconds(3), "Older lifecycle cannot replace newer");
        Feed(reducer, Event("session.resume", new { }, "resume", 5));
        Check(reducer.Snapshot.Lifecycle == "Active", "Timestamped resume activates node");
        Feed(reducer, "{\"type\":\"session.shutdown\",\"id\":\"missing-time\",\"data\":{}}");
        Check(reducer.Snapshot.Lifecycle == "Active", "Undated lifecycle is not trusted");
    }

    private static void WithDirectory(Action<string> run)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "copilot-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { run(path); }
        finally { Directory.Delete(path, true); }
    }

    private static string Workspace(string directory)
    {
        var root = Path.Combine(directory, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "workspace.yaml"), "client_name: github/autopilot\nname: Main\ncwd: 'D:\\fixture'\n");
        return Path.Combine(root, "events.jsonl");
    }

    private static void LargeIncrementalTranscript() => WithDirectory(directory =>
    {
        var path = Workspace(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = "model.model_call_success", id = "large", timestamp = Epoch.AddSeconds(1),
            data = new
            {
                requestId = "large-request", modelCall = new { model = "fixture" },
                responseUsage = new { prompt_tokens = 42, completion_tokens = 3 },
                requestMessages = new string('x', 600000),
            },
        }) + "\n" + Event("subagent.started", new { }, "task", 2, "child") + "\n");
        using var resolver = new CopilotSessionIdentityResolver(directory);
        resolver.Observe("root");
        CopilotSessionIdentity[] nodes = [];
        for (int i = 0; i < 5; i++) nodes = resolver.Scan().ToArray();
        Check(nodes.Single(node => node.SourceId == "root").Details?.InputTokens == 42, "500KB-plus call survives incremental byte budgets");
        Check(nodes.Single(node => node.SourceId == "child").Details?.Lifecycle == "Active", "Hookless task lifecycle published");
        Check(nodes.Single(node => node.SourceId == "root").Cwd == @"D:\fixture", "Metadata cwd");
    });

    private static void DocumentedTaskParentAndName() => WithDirectory(directory =>
    {
        var path = Workspace(directory);
        File.WriteAllText(path, Event("subagent.started", new { parentId = "middle", agentDisplayName = "Own task name", model = "task-model" },
            "started", 1, "leaf") + "\n" + Event("subagent.completed", new { agentDisplayName = "Own task name" }, "completed", 3, "leaf") + "\n");
        using var resolver = new CopilotSessionIdentityResolver(directory);
        resolver.Observe("root");
        var nodes = resolver.Scan();
        var leaf = nodes.Single(node => node.SourceId == "leaf");
        Check(leaf.ParentId == "middle" && leaf.OwnerId == "root" && leaf.Name == "Own task name"
            && leaf.Details?.Lifecycle == "Completed" && leaf.Details?.Model == "task-model",
            "Documented task parent and own display-name retain nested ancestry on completion");
        Check(nodes.Any(node => node.SourceId == "middle" && node.Kind == CopilotIdentityKind.AppTaskChild), "Documented hookless task ancestor is published");
    });

    private static void ReplacementAndPartialLines() => WithDirectory(directory =>
    {
        var path = Workspace(directory);
        File.WriteAllText(path, Call("first", "request1") + "\n");
        using var resolver = new CopilotSessionIdentityResolver(directory);
        resolver.Observe("root");
        Check(resolver.Scan().Single().Details?.InputTokens == 10, "Initial telemetry");
        File.WriteAllText(path, Call("second", "request2", 2, 25));
        Check(resolver.Scan().Single().Details?.InputTokens is null, "Replacement unfinished line cannot expose old telemetry");
        File.AppendAllText(path, "\n");
        Check(resolver.Scan().Single().Details?.InputTokens == 25, "Completed replacement line consumed once");
    });

    private static void PublicationCoalescing() => WithDirectory(directory =>
    {
        var path = Workspace(directory);
        File.WriteAllText(path, Call("one", "request") + "\n");
        using var resolver = new CopilotSessionIdentityResolver(directory);
        resolver.Observe("root");
        var initial = resolver.Scan();
        Check(initial.SequenceEqual(resolver.Scan()), "Unchanged scan does not churn snapshot equality");
        File.AppendAllText(path, Call("one", "request") + "\n");
        Check(initial.SequenceEqual(resolver.Scan()), "Duplicate observation does not churn snapshot equality");
    });

    private static void MalformedTelemetry()
    {
        var reducer = new CopilotSessionDetailsReducer("root");
        Feed(reducer, Event("model.model_call_success", new
        {
            requestId = "request", responseUsage = new { prompt_tokens = -10, completion_tokens = "unknown" },
            copilotUsage = new { total_nano_aiu = decimal.MaxValue },
        }));
        Check(reducer.Snapshot.InputTokens is null && reducer.Snapshot.OutputTokens is null
            && reducer.Snapshot.NanoAiu is null && reducer.Snapshot.IsPartial, "Invalid numeric values remain unavailable");
    }

    private static void OverflowIsPartial()
    {
        var reducer = new CopilotSessionDetailsReducer("root");
        for (int i = 0; i < 8200; i++) Feed(reducer, Call("event-" + i, "request-" + i, i));
        Check(reducer.Snapshot.Observations.Length == 8192 && reducer.Snapshot.IsPartial, "Bounded ledger reports incomplete coverage");
    }
}
