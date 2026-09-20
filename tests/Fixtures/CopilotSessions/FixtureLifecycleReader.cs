using Pagurian.Modules.Copilot;

sealed class FixtureLifecycleReader(string id) : ICopilotAppLifecycleReader
{
    private long _version;
    public volatile bool Disposed;
    public CopilotAppLifecycleSnapshot Scan(IReadOnlyCollection<string> observed, CancellationToken cancellation) =>
        new(++_version, DateTimeOffset.UtcNow,
            [new(id, CopilotArchiveState.Live, new(1, DateTimeOffset.UtcNow.AddMinutes(-1),
                @"C:\fixture\github.exe"), DateTimeOffset.UtcNow)], []);
    public void Dispose() => Disposed = true;
}
