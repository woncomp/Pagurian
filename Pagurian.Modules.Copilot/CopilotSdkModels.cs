namespace Pagurian.Modules.Copilot;

internal enum CopilotSdkReadHealth
{
    Healthy,
    Empty,
    Stale,
    Unsupported,
}

internal sealed record CopilotSdkPersistedEvent(
    string EventId,
    DateTimeOffset? At,
    string Type,
    string? AgentId,
    string? RequestId,
    string? ToolCallId,
    string? ToolName,
    bool ResolvedByHook,
    string? Lifecycle);
