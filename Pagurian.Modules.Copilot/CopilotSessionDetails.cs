using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Pagurian.Modules.Copilot;

// Only projected scalar telemetry survives parsing; no messages, prompts or responses.
internal sealed record CopilotSessionDetails
{
    public static CopilotSessionDetails Empty { get; } = new();
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ContextTier { get; init; }
    public long? ContextTokens { get; init; }
    public long? ContextLimit { get; init; }
    public long? PromptLimit { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CachedTokens { get; init; }
    public long? CacheCreationTokens { get; init; }
    public long? NanoAiu { get; init; }
    public decimal? PremiumRequests { get; init; }
    // Session-wide cumulative checkpoints may include tasks: never own/additive usage.
    public long? ReportedNanoAiu { get; init; }
    public decimal? ReportedPremiumRequests { get; init; }
    public bool IsPartial { get; init; }
    public string? Lifecycle { get; init; }
    public DateTimeOffset? LifecycleAt { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    internal ImmutableArray<CopilotDetailObservation> Observations { get; init; } = [];

    public CopilotSessionDetails ForGeneration(DateTimeOffset epoch)
    {
        if (epoch == DateTimeOffset.MinValue) return this;
        if (Observations.IsEmpty)
            return this == Empty ? this : Empty with { IsPartial = true };
        var retained = Observations.Where(observation => observation.At >= epoch).ToImmutableArray();
        // A timestamped checkpoint still includes earlier generations. Only a
        // fresh session.start can establish its accounting interval.
        bool startsHere = retained.Any(observation => observation.Kind == "start");
        if (retained.Length == Observations.Length && (startsHere || !retained.Any(observation => observation.Kind == "checkpoint")))
            return this;
        return Create(retained.Where(observation => observation.Kind != "checkpoint" || startsHere)
            .ToImmutableArray(), IsPartial || !startsHere);
    }

    public static CopilotSessionDetails Aggregate(IEnumerable<CopilotSessionDetails> snapshots)
    {
        var nodes = snapshots.ToArray();
        if (nodes.Length == 0)
            return Empty;
        var calls = nodes.SelectMany(node => node.Observations).Where(observation => observation.Kind == "call")
            .ToImmutableArray();
        var total = Create(calls, nodes.Any(node => node.IsPartial || node.Observations.IsEmpty));
        // Checkpoints can overlap nodes or include unobserved children. Even
        // their maximum is not an attributable group total.
        if (nodes.Any(node => node.ReportedNanoAiu.HasValue || node.ReportedPremiumRequests.HasValue))
            total = total with { IsPartial = true };
        return total with { Model = null, ReasoningEffort = null, ContextTier = null,
            ContextTokens = null, ContextLimit = null, PromptLimit = null, Lifecycle = null, LifecycleAt = null,
            TerminalAt = null };
    }

    internal static CopilotSessionDetails Create(ImmutableArray<CopilotDetailObservation> observations, bool partial)
    {
        if (observations.IsEmpty)
            return partial ? Empty with { IsPartial = true } : Empty;
        var ordered = observations.OrderBy(observation => observation.At).ToArray();
        var candidates = ordered.Where(observation => observation.Kind == "call").ToArray();
        var parents = Enumerable.Range(0, candidates.Length).ToArray();
        var aliases = new Dictionary<string, int>(StringComparer.Ordinal);
        int Find(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        for (int index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            foreach (var alias in new[] { candidate.CallId, candidate.ApiCallId, candidate.ServiceRequestId })
            {
                if (alias is null) continue;
                if (aliases.TryGetValue(alias, out var previous))
                    parents[Find(index)] = Find(previous);
                else
                    aliases[alias] = index;
            }
        }
        var calls = candidates.Select((call, index) => (call, index))
            .GroupBy(entry => Find(entry.index), entry => entry.call)
            .Select(group =>
            {
                if (group.Select(call => (call.InputTokens, call.OutputTokens, call.CachedTokens,
                    call.CacheCreationTokens, call.NanoAiu, call.PremiumRequests)).Distinct().Skip(1).Any())
                    partial = true;
                return group.Last() with
                {
                    InputTokens = group.LastOrDefault(call => call.InputTokens.HasValue)?.InputTokens,
                    OutputTokens = group.LastOrDefault(call => call.OutputTokens.HasValue)?.OutputTokens,
                    CachedTokens = group.LastOrDefault(call => call.CachedTokens.HasValue)?.CachedTokens,
                    CacheCreationTokens = group.LastOrDefault(call => call.CacheCreationTokens.HasValue)?.CacheCreationTokens,
                    NanoAiu = group.LastOrDefault(call => call.NanoAiu.HasValue)?.NanoAiu,
                    PremiumRequests = group.LastOrDefault(call => call.PremiumRequests.HasValue)?.PremiumRequests,
                };
            }).ToArray();
        var checkpoint = ordered.LastOrDefault(observation => observation.Kind == "checkpoint");
        var model = ordered.LastOrDefault(observation => observation.Model is not null);
        var reasoning = ordered.LastOrDefault(observation => observation.HasReasoning);
        var tier = ordered.LastOrDefault(observation => observation.HasContextTier);
        var context = ordered.LastOrDefault(observation => observation.ContextTokens.HasValue);
        var limit = ordered.LastOrDefault(observation => observation.ContextLimit.HasValue);
        var promptLimit = ordered.LastOrDefault(observation => observation.PromptLimit.HasValue);
        // A model switch invalidates the old model's limits until reported again.
        bool CurrentLimit(CopilotDetailObservation? value) => value is not null &&
            (model is null || value.At >= model.At || value.Model == model.Model);
        var lifecycle = ordered.LastOrDefault(observation => observation.Lifecycle is not null);
        var terminal = ordered.LastOrDefault(observation => observation.Lifecycle is "Completed" or "Ended");
        long? Sum(Func<CopilotDetailObservation, long?> select)
        {
            var values = calls.Select(select).ToArray();
            if (!values.Any(value => value.HasValue))
                return null;
            if (values.Any(value => !value.HasValue))
                partial = true;
            try { return values.Where(value => value.HasValue).Sum(value => value!.Value); }
            catch (OverflowException) { partial = true; return null; }
        }
        var input = Sum(call => call.InputTokens);
        var output = Sum(call => call.OutputTokens);
        var cached = Sum(call => call.CachedTokens);
        var creation = Sum(call => call.CacheCreationTokens);
        var nano = Sum(call => call.NanoAiu);
        decimal? premium = null;
        try
        {
            if (calls.Any(call => call.PremiumRequests.HasValue))
                premium = calls.Sum(call => call.PremiumRequests ?? 0);
        }
        catch (OverflowException) { partial = true; }
        if (checkpoint is not null)
        {
            // The latest cumulative sample supersedes older cumulative samples.
            // It is not added to the call ledger or advertised as own-agent usage.
            partial = true;
        }
        return new()
        {
            Observations = observations, Model = model?.Model, ReasoningEffort = reasoning?.ReasoningEffort,
            ContextTier = tier?.ContextTier, ContextTokens = context?.ContextTokens,
            ContextLimit = CurrentLimit(limit) ? limit?.ContextLimit : null,
            PromptLimit = CurrentLimit(promptLimit) ? promptLimit?.PromptLimit : null,
            InputTokens = input, OutputTokens = output, CachedTokens = cached, CacheCreationTokens = creation,
            NanoAiu = nano, PremiumRequests = premium,
            ReportedNanoAiu = checkpoint?.NanoAiu, ReportedPremiumRequests = checkpoint?.PremiumRequests,
            IsPartial = partial || calls.Length > 0, // persisted calls are a known lower bound, not lifetime accounting
            Lifecycle = lifecycle?.Lifecycle, LifecycleAt = lifecycle?.At,
            TerminalAt = terminal?.At,
        };
    }
}

internal sealed record CopilotDetailObservation(
    string SourceId, string EventId, DateTimeOffset At, string Kind,
    string? CallId = null, string? Model = null, string? ReasoningEffort = null,
    bool HasReasoning = false, string? ContextTier = null, bool HasContextTier = false,
    long? ContextTokens = null, long? ContextLimit = null,
    long? InputTokens = null, long? OutputTokens = null, long? CachedTokens = null,
    long? CacheCreationTokens = null, long? NanoAiu = null, decimal? PremiumRequests = null,
    string? Lifecycle = null, string? OriginId = null, string? ApiCallId = null, string? ServiceRequestId = null,
    long? PromptLimit = null);

internal sealed class CopilotSessionDetailsReducer(string sourceId, int maximumObservations = 8192)
{
    private readonly Dictionary<string, CopilotDetailObservation> _observations = new(StringComparer.Ordinal);
    private CopilotSessionDetails? _snapshot;
    private CopilotSessionDetails? _ownSnapshot;
    private bool _partial;
    public CopilotSessionDetails Snapshot => _snapshot ??= CopilotSessionDetails.Create(
        _observations.Values.OrderBy(observation => observation.At).ThenBy(observation => observation.EventId,
            StringComparer.Ordinal).ToImmutableArray(), _partial);

    public CopilotSessionDetails ForIdentity(bool taskChild) => taskChild ? Snapshot
        : _ownSnapshot ??= CopilotSessionDetails.Create(
            _observations.Values.Where(observation => observation.OriginId == sourceId)
                .OrderBy(observation => observation.At).ThenBy(observation => observation.EventId,
                    StringComparer.Ordinal).ToImmutableArray(), _partial);

    public void MarkPartial()
    {
        if (!_partial) { _partial = true; _snapshot = null; _ownSnapshot = null; }
    }

    public void Reset()
    {
        _observations.Clear();
        _snapshot = null;
        _ownSnapshot = null;
        _partial = true;
    }

    public void RemoveOrigin(string origin)
    {
        var removed = _observations.Where(pair => pair.Value.OriginId == origin).Select(pair => pair.Key).ToArray();
        foreach (var id in removed)
            _observations.Remove(id);
        if (removed.Length > 0) { _partial = true; _snapshot = null; _ownSnapshot = null; }
    }

    public bool TrimToBudget(int maximum)
    {
        if (_observations.Count <= maximum)
            return false;
        foreach (var key in _observations.OrderBy(pair => pair.Value.At)
            .Take(_observations.Count - maximum).Select(pair => pair.Key).ToArray())
            _observations.Remove(key);
        _partial = true;
        _snapshot = null;
        _ownSnapshot = null;
        return true;
    }

    public void ObserveLifecycle(JsonElement record, string lifecycle, string? origin = null)
    {
        if (Header(record, out var id, out var at))
            Add(new(sourceId, id, at, "lifecycle", Lifecycle: lifecycle, OriginId: origin ?? sourceId,
                Model: Text(Object(record, "data"), "model")));
    }

    public void Observe(JsonElement record, string? origin = null)
    {
        var type = Text(record, "type");
        if (type is not ("session.start" or "session.resume" or "session.shutdown" or "session.model_change"
            or "session.usage_info" or "session.compaction_complete" or "subagent.configured"
            or "session.usage_checkpoint" or "assistant.usage" or "model.model_call_started" or "model.model_call_success"))
            return;
        if (!Header(record, out var id, out var at)) { MarkPartial(); return; }
        var data = Object(record, "data");
        var item = new CopilotDetailObservation(sourceId, id, at, "metadata", OriginId: origin ?? sourceId);
        switch (type)
        {
            case "session.start":
            case "session.resume":
                item = item with { Kind = type == "session.start" ? "start" : "metadata",
                    Model = Text(data, "selectedModel"), Lifecycle = "Active",
                    ReasoningEffort = Text(data, "reasoningEffort"), HasReasoning = Has(data, "reasoningEffort"),
                    ContextTier = Text(data, "contextTier"), HasContextTier = Has(data, "contextTier") };
                break;
            case "session.model_change":
                item = item with { Model = Text(data, "newModel"),
                    ReasoningEffort = Text(data, "reasoningEffort"), HasReasoning = Has(data, "reasoningEffort"),
                    ContextTier = Text(data, "contextTier"), HasContextTier = Has(data, "contextTier") };
                break;
            case "model.model_call_started":
                item = item with { Model = Text(data, "model"),
                    ContextLimit = Integer(Object(Object(Object(data, "modelInfo"), "capabilities"), "limits"),
                        "max_context_window_tokens"),
                    PromptLimit = Integer(Object(Object(Object(data, "modelInfo"), "capabilities"), "limits"),
                        "max_prompt_tokens") };
                break;
            case "subagent.configured":
                item = item with { Model = Text(data, "model"),
                    ReasoningEffort = Text(data, "reasoningEffort"), HasReasoning = Has(data, "reasoningEffort"),
                    ContextTier = Text(data, "contextTier"), HasContextTier = Has(data, "contextTier") };
                break;
            case "session.usage_info":
                item = item with { ContextTokens = Integer(data, "currentTokens"), ContextLimit = Integer(data, "tokenLimit") };
                break;
            case "session.compaction_complete":
                if (!Has(data, "success") || data.GetProperty("success").ValueKind != JsonValueKind.True) return;
                item = item with { ContextTokens = Integer(data, "postCompactionTokens"), ContextLimit = Integer(data, "tokenLimit") };
                break;
            case "session.shutdown":
                item = item with { Lifecycle = "Ended", Model = Text(data, "currentModel"),
                    ContextTokens = Integer(data, "currentTokens") };
                break;
            case "session.usage_checkpoint":
                item = item with { Kind = "checkpoint", NanoAiu = Integer(data, "totalNanoAiu"),
                    PremiumRequests = Number(data, "totalPremiumRequests") };
                break;
            case "assistant.usage":
                // SDK providerCallId is the x-github-request-id header, called
                // requestId by model.model_call_success. Retain aliases because
                // either projection may omit one of the provider/service IDs.
                var apiId = Text(data, "providerCallId") ?? Text(data, "serviceRequestId") ?? Text(data, "apiCallId");
                if (apiId is null) { MarkPartial(); return; }
                item = item with { Kind = "call", CallId = apiId, Model = Text(data, "model"),
                    InputTokens = Integer(data, "inputTokens"), OutputTokens = Integer(data, "outputTokens"),
                    CachedTokens = Integer(data, "cacheReadTokens"), CacheCreationTokens = Integer(data, "cacheWriteTokens"),
                    NanoAiu = Integer(Object(data, "copilotUsage"), "totalNanoAiu"),
                    ApiCallId = Text(data, "apiCallId"), ServiceRequestId = Text(data, "serviceRequestId"),
                    ReasoningEffort = Text(data, "reasoningEffort"), HasReasoning = Has(data, "reasoningEffort") };
                break;
            case "model.model_call_success":
                var call = Object(data, "modelCall");
                var request = Text(data, "requestId") ?? Text(call, "request_id")
                    ?? Text(data, "serviceRequestId") ?? Text(call, "service_request_id") ?? Text(call, "api_id");
                if (request is null) { MarkPartial(); return; }
                var usage = Object(data, "responseUsage");
                var cache = Object(usage, "prompt_tokens_details");
                item = item with { Kind = "call", CallId = request, Model = Text(call, "model"),
                    ReasoningEffort = Text(data, "reasoningEffort"), HasReasoning = Has(data, "reasoningEffort"),
                    InputTokens = Integer(usage, "prompt_tokens"), OutputTokens = Integer(usage, "completion_tokens"),
                    CachedTokens = Integer(cache, "cached_tokens"),
                    CacheCreationTokens = Integer(cache, "cache_creation_tokens") ?? Integer(cache, "cache_write_tokens"),
                    NanoAiu = Integer(Object(data, "copilotUsage"), "total_nano_aiu"),
                    ApiCallId = Text(call, "api_id"),
                    ServiceRequestId = Text(data, "serviceRequestId") ?? Text(call, "service_request_id") };
                break;
        }
        Add(item);
    }

    private void Add(CopilotDetailObservation item)
    {
        if (maximumObservations == 0) { MarkPartial(); return; }
        var key = item.OriginId + ":" + item.EventId;
        if (_observations.TryGetValue(key, out var previous) && previous == item)
            return;
        if (previous is not null) MarkPartial();
        _observations[key] = item;
        if (_observations.Count > maximumObservations)
        {
            _observations.Remove(_observations.MinBy(pair => pair.Value.At).Key);
            _partial = true;
        }
        _snapshot = null;
        _ownSnapshot = null;
    }

    private static bool Header(JsonElement record, out string id, out DateTimeOffset at)
    {
        id = Text(record, "id") ?? "";
        return DateTimeOffset.TryParse(Text(record, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out at) && id.Length > 0;
    }

    private static bool Has(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out _);
    private static JsonElement Object(JsonElement element, string key) =>
        Has(element, key) && element.GetProperty(key).ValueKind == JsonValueKind.Object ? element.GetProperty(key) : default;
    private static string? Text(JsonElement element, string key) =>
        Has(element, key) && element.GetProperty(key).ValueKind == JsonValueKind.String
            && element.GetProperty(key).GetString() is { Length: > 0 and <= 1024 } text ? text : null;
    private static long? Integer(JsonElement element, string key) =>
        Has(element, key) && element.GetProperty(key).ValueKind == JsonValueKind.Number
            && element.GetProperty(key).TryGetInt64(out var value) && value >= 0 ? value : null;
    private static decimal? Number(JsonElement element, string key) =>
        Has(element, key) && element.GetProperty(key).ValueKind == JsonValueKind.Number
            && element.GetProperty(key).TryGetDecimal(out var value) && value >= 0 ? value : null;
}
