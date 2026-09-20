namespace Pagurian.Modules.Copilot;

internal enum CopilotArchiveState { Unknown, Live, Archived }

internal sealed record CopilotAppInstance(int Pid, DateTimeOffset StartedAt, string Path)
{
    public string Key => $"{Pid}:{StartedAt.UtcTicks}:{Path.ToUpperInvariant()}";
}

internal sealed record CopilotAppSessionEvidence(
    string SessionId, CopilotArchiveState Archive, CopilotAppInstance? App = null,
    DateTimeOffset? LoadedAt = null);

// Version orders worker snapshots; ObservedAt fences events already in flight.
// LoadedAt is positive runtime evidence, never a DB is_running flag.
internal sealed record CopilotAppLifecycleSnapshot(
    long Version, DateTimeOffset ObservedAt,
    IReadOnlyList<CopilotAppSessionEvidence> Sessions,
    IReadOnlyList<CopilotAppInstance> Exited);

internal interface ICopilotAppLifecycleReader : IDisposable
{
    CopilotAppLifecycleSnapshot Scan(IReadOnlyCollection<string> observed, CancellationToken cancellation);
}

internal interface ICopilotArchiveReader
{
    IReadOnlyDictionary<string, CopilotArchiveState> Read(IReadOnlyCollection<string> ids,
        CancellationToken cancellation);
}

internal interface ICopilotAppProcesses : IDisposable
{
    CopilotAppInstance? ReadOwner(string ownerFile);
    bool IsLoaded(int sdkPid, DateTimeOffset lockWrittenAt, CopilotAppInstance app);
    IReadOnlyList<CopilotAppInstance> Exited();
}
