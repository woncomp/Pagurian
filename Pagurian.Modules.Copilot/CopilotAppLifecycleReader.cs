namespace Pagurian.Modules.Copilot;

internal sealed class CopilotAppLifecycleReader : ICopilotAppLifecycleReader
{
    private readonly string _stateDirectory;
    private readonly string _ownerFile;
    private readonly ICopilotArchiveReader _database;
    private readonly ICopilotAppProcesses _processes;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _diagnostic;
    private IEnumerator<string>? _directories;
    private long _version;
    private int _roundRobin;

    public static ICopilotAppLifecycleReader Create(string stateDirectory, Action<string>? diagnostic) =>
        new CopilotAppLifecycleReader(stateDirectory,
            new CopilotArchiveReader(Path.Combine(Path.GetDirectoryName(stateDirectory)!, "data.db"), diagnostic),
            new CopilotAppProcesses(diagnostic), diagnostic: diagnostic);

    public CopilotAppLifecycleReader(string stateDirectory, ICopilotArchiveReader database,
        ICopilotAppProcesses processes, Func<DateTimeOffset>? clock = null, Action<string>? diagnostic = null)
    {
        _stateDirectory = stateDirectory;
        _ownerFile = Path.Combine(Path.GetDirectoryName(stateDirectory)!, "run", "single-instance.owner");
        _database = database;
        _processes = processes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _diagnostic = diagnostic;
    }

    public CopilotAppLifecycleSnapshot Scan(IReadOnlyCollection<string> observed, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var at = _clock();
        var app = _processes.ReadOwner(_ownerFile);
        if (app is null) _diagnostic?.Invoke("app-owner-unavailable");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var known = observed.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < Math.Min(128, known.Length); i++)
            ids.Add(known[_roundRobin++ % known.Length]);
        if (known.Length > 0) _roundRobin %= known.Length;
        // Positive loaded-session discovery has no age cutoff. Enumeration is
        // resumable, bounded, and never queries every historical DB row.
        try
        {
            _directories ??= Directory.EnumerateDirectories(_stateDirectory).GetEnumerator();
            for (int i = 0; i < 128; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!_directories.MoveNext())
                {
                    _directories.Dispose();
                    _directories = null;
                    break;
                }
                var id = Path.GetFileName(_directories.Current);
                if (CopilotSessionIdentityIndex.ValidId(id)) ids.Add(id);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _directories?.Dispose();
            _directories = null;
            _diagnostic?.Invoke("app-load-discovery-unavailable");
        }
        var loads = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (app is not null)
        {
            foreach (var id in ids)
            {
                cancellation.ThrowIfCancellationRequested();
                if (LoadedAt(id, app) is { } loaded) loads[id] = loaded;
            }
        }
        // Unknown/unloaded historical directories must not become restoration candidates.
        var candidates = ids.Where(id => observed.Contains(id) || loads.ContainsKey(id)).ToArray();
        var archive = _database.Read(candidates, cancellation);
        if (candidates.Any(id => archive.GetValueOrDefault(id, CopilotArchiveState.Unknown) == CopilotArchiveState.Unknown))
            _diagnostic?.Invoke("app-archive-unavailable");
        var exited = _processes.Exited();
        bool appExited = app is not null && exited.Any(p => p.Key == app.Key);
        var evidence = candidates.Select(id => new CopilotAppSessionEvidence(id,
            archive.GetValueOrDefault(id, CopilotArchiveState.Unknown),
            !appExited && loads.ContainsKey(id) ? app : null,
            !appExited && loads.TryGetValue(id, out var load) ? load : null)).ToArray();
        return new(++_version, at, evidence, exited);
    }

    private DateTimeOffset? LoadedAt(string id, CopilotAppInstance app)
    {
        try
        {
            var directory = Path.Combine(_stateDirectory, id);
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return null;
            DateTimeOffset? latest = null;
            foreach (var file in Directory.EnumerateFiles(directory, "inuse.*.lock").Take(16))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var parts = Path.GetFileName(file).Split('.');
                var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
                if (parts.Length == 3 && int.TryParse(parts[1], out int pid) &&
                    stamp >= app.StartedAt && _processes.IsLoaded(pid, stamp, app))
                    latest = latest is null || stamp > latest ? stamp : latest;
            }
            return latest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke("app-load-unavailable");
            return null;
        }
    }

    public void Dispose()
    {
        _directories?.Dispose();
        _directories = null;
        _processes.Dispose();
    }
}
