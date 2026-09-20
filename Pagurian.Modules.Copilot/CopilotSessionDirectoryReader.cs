namespace Pagurian.Modules.Copilot;

internal enum CopilotSessionDirectoryPresence { Unknown, Present, Missing }

internal sealed record CopilotSessionDirectoryTarget(string SessionId, long Revision, bool Recover = false);

internal sealed record CopilotSessionDirectoryEvidence(
    CopilotSessionDirectoryTarget Target, CopilotSessionDirectoryPresence Presence, string? Name = null);

internal sealed record CopilotSessionDirectorySnapshot(
    long Version, DateTimeOffset ObservedAt, IReadOnlyList<CopilotSessionDirectoryEvidence> Sessions);

internal sealed class CopilotSessionDirectoryReader
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Func<string, FileAttributes> _attributes;
    private readonly Action<string>? _diagnostic;
    private long _lastScan;
    private long _version;

    public CopilotSessionDirectoryReader(string root, Action<string>? diagnostic = null,
        TimeProvider? time = null, Func<string, FileAttributes>? attributes = null)
    {
        _root = Path.GetFullPath(root);
        _time = time ?? TimeProvider.System;
        _attributes = attributes ?? File.GetAttributes;
        _diagnostic = diagnostic;
        _lastScan = _time.GetTimestamp();
    }

    public CopilotSessionDirectorySnapshot? Scan(
        IReadOnlyList<CopilotSessionDirectoryTarget> targets, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var now = _time.GetTimestamp();
        bool due = _time.GetElapsedTime(_lastScan, now) >= Interval;
        if (due) _lastScan = now;
        var candidates = targets.Where(t => due || t.Recover).ToArray();
        if (candidates.Length == 0) return null;
        var at = _time.GetUtcNow();
        bool rootAvailable = RootAvailable();
        var evidence = new List<CopilotSessionDirectoryEvidence>(candidates.Length);
        foreach (var target in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var presence = rootAvailable ? Probe(target.SessionId) : CopilotSessionDirectoryPresence.Unknown;
            evidence.Add(new(target, presence));
        }
        return new(++_version, at, evidence);
    }

    private bool RootAvailable()
    {
        try
        {
            if (RegularDirectory(_attributes(_root))) return true;
        }
        catch (Exception e) when (Unavailable(e)) { }
        _diagnostic?.Invoke("session-directory-root-unavailable");
        return false;
    }

    private CopilotSessionDirectoryPresence Probe(string id)
    {
        if (!CopilotSessionIdentityIndex.ValidId(id))
        {
            _diagnostic?.Invoke("session-directory-invalid-id");
            return CopilotSessionDirectoryPresence.Unknown;
        }
        try
        {
            if (RegularDirectory(_attributes(Path.Combine(_root, id))))
                return CopilotSessionDirectoryPresence.Present;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // A disappearing/unavailable common root must never look like mass deletion.
            return RootAvailable() ? CopilotSessionDirectoryPresence.Missing : CopilotSessionDirectoryPresence.Unknown;
        }
        catch (Exception e) when (Unavailable(e)) { }
        _diagnostic?.Invoke("session-directory-unavailable");
        return CopilotSessionDirectoryPresence.Unknown;
    }

    private static bool RegularDirectory(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    private static bool Unavailable(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException;
}
