#pragma warning disable GHCP001

using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotSdkSessionMetadata(
    string SessionId,
    string? Name,
    string? ClientName,
    string? Cwd);

internal sealed record CopilotSdkDiscoverySnapshot(
    IReadOnlyList<CopilotSdkSessionMetadata> Sessions,
    IReadOnlySet<string> InUse,
    CopilotSdkReadHealth Health,
    string? Failure);

internal sealed record CopilotSdkEventSnapshot(
    string SessionId,
    IReadOnlyList<CopilotSdkPersistedEvent> Events,
    bool HasMore,
    string? CursorStatus,
    CopilotSdkReadHealth Health,
    string? Failure);

internal interface ICopilotSdkSessionSource : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<CopilotSdkDiscoverySnapshot> DiscoverAsync(
        IReadOnlyCollection<string> previouslyTracked,
        CancellationToken cancellationToken);
    Task<CopilotSdkEventSnapshot> ReadRecentAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

internal sealed class CopilotSdkSessionSource : ICopilotSdkSessionSource
{
    private const int RecentEventLimit = 10;
    private readonly object _sync = new();
    private CopilotClient? _client;
    private bool _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started)
                return;
            _started = true;
        }

        var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.CopilotCli,
            UseLoggedInUser = true,
            Connection = RuntimeConnection.ForStdio(CopilotModule.CopilotRuntimePath),
        });
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
                _client = client;
        }
        catch
        {
            lock (_sync)
                _started = false;
            client.Dispose();
            throw;
        }
    }

    public async Task<CopilotSdkDiscoverySnapshot> DiscoverAsync(
        IReadOnlyCollection<string> previouslyTracked,
        CancellationToken cancellationToken)
    {
        var client = GetClient();
        try
        {
            var list = await client.Rpc.Sessions.ListAsync(
                source: SessionSource.Local,
                metadataLimit: null,
                filter: null,
                includeDetached: false,
                throwOnError: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var entries = list.Sessions
                .Where(entry => !entry.IsRemote && !string.IsNullOrWhiteSpace(entry.SessionId))
                .Select(entry => new CopilotSdkSessionMetadata(
                    entry.SessionId,
                    Clean(entry.Name),
                    Clean(entry.ClientName),
                    Clean(entry.Context?.Cwd)))
                .ToArray();
            var ids = entries.Select(entry => entry.SessionId)
                .Concat(previouslyTracked)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var inUse = ids.Length == 0
                ? Array.Empty<string>()
                : (await client.Rpc.Sessions.CheckInUseAsync(ids, cancellationToken)
                    .ConfigureAwait(false)).InUse?.ToArray() ?? [];

            return new(entries, inUse.ToHashSet(StringComparer.Ordinal),
                entries.Length == 0 ? CopilotSdkReadHealth.Empty : CopilotSdkReadHealth.Healthy,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new([], new HashSet<string>(StringComparer.Ordinal),
                CopilotSdkReadHealth.Stale, exception.GetType().Name);
        }
    }

    public async Task<CopilotSdkEventSnapshot> ReadRecentAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var client = GetClient();
        try
        {
            var result = await client.Rpc.Sessions.ReadPersistedEventsAsync(
                sessionId,
                cursor: null,
                max: RecentEventLimit,
                direction: EventsReadDirection.Backward,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var events = result.Events
                .Select(Project)
                .Where(item => item is not null)
                .Cast<CopilotSdkPersistedEvent>()
                .ToArray();
            return new(sessionId, events, result.HasMore, result.CursorStatus.ToString(),
                events.Length == 0 ? CopilotSdkReadHealth.Empty : CopilotSdkReadHealth.Healthy,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(sessionId, [], false, null, CopilotSdkReadHealth.Stale,
                exception.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CopilotClient? client;
        lock (_sync)
        {
            client = _client;
            _client = null;
            _started = false;
        }

        if (client is null)
            return;
        try
        {
            await client.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    private CopilotClient GetClient()
    {
        lock (_sync)
        {
            return _client
                ?? throw new InvalidOperationException(
                    "The Copilot SDK monitor has not started.");
        }
    }

    private static CopilotSdkPersistedEvent? Project(object value)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var id = String(root, "id");
            var type = String(root, "type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
                return null;

            var data = Object(root, "data");
            var requestId = String(data, "requestId");
            var toolCallId = String(data, "toolCallId") ?? String(root, "toolCallId");
            var toolName = String(data, "toolName") ?? String(root, "toolName");
            var resolvedByHook = Boolean(data, "resolvedByHook");
            var lifecycle = type switch
            {
                "session.start" or "session.resume" => "Active",
                "session.shutdown" or "assistant.turn_end" or
                    "tool.execution_complete" or "subagent.completed" or
                    "subagent.failed" => "Completed",
                _ => null,
            };
            return new(id, DateTime(root, "timestamp"), type,
                String(root, "agentId"), requestId, toolCallId, toolName,
                resolvedByHook, lifecycle);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : default;

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool Boolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? DateTime(JsonElement value, string name) =>
        String(value, name) is { } text &&
        DateTimeOffset.TryParse(text, out var at)
            ? at
            : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
