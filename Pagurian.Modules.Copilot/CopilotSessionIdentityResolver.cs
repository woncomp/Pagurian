using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pagurian.Modules.Copilot;

internal enum CopilotIdentityKind
{
    Unknown,
    Cli,
    AppRoot,
    AppTaskChild,
}

internal sealed record CopilotSessionIdentity(
    string SourceId, string OwnerId, CopilotIdentityKind Kind, string? Name);

// Pure evidence index. No filesystem, clock, dispatcher, or lifecycle reduction.
// The resolver serializes access; fixtures can use this directly.
internal sealed class CopilotSessionIdentityIndex
{
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CopilotIdentityKind Kind, string? Name)> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _parents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflicted = new(StringComparer.Ordinal);
    private readonly Action<string>? _diagnostic;

    public CopilotSessionIdentityIndex(Action<string>? diagnostic = null) => _diagnostic = diagnostic;

    public void Observe(string sourceId)
    {
        if (ValidId(sourceId))
            _observed.Add(sourceId);
    }

    public void SetMetadata(string sourceId, string? clientName, string? name = null)
    {
        if (!ValidId(sourceId))
            return;
        _metadata.TryGetValue(sourceId, out var previous);
        const string appClient = "github/autopilot";
        var client = clientName?.Trim();
        // A session's client identity is immutable. In particular a reader
        // catching "github/" during a rewrite must not turn a known App root
        // (and all its task children) into independent CLI cells.
        var kind = previous.Kind;
        if (kind == CopilotIdentityKind.Unknown && !string.IsNullOrWhiteSpace(client))
        {
            if (string.Equals(client, appClient, StringComparison.OrdinalIgnoreCase))
                kind = CopilotIdentityKind.AppRoot;
            else if (!appClient.StartsWith(client, StringComparison.OrdinalIgnoreCase))
                kind = CopilotIdentityKind.Cli;
        }
        // Missing/partially rewritten metadata cannot erase positive evidence.
        _metadata[sourceId] = (kind, string.IsNullOrWhiteSpace(name) ? previous.Name : name);
    }

    public void Claim(string childId, string parentId)
    {
        if (!ValidId(childId) || !ValidId(parentId) || childId == parentId)
            return;
        if (!_parents.TryGetValue(childId, out var parents))
            _parents[childId] = parents = new(StringComparer.Ordinal);
        if (parents.Contains(parentId))
            return;
        // A corrupt stream cannot grow a single identity's competing claims forever.
        if (parents.Count >= 8)
            _conflicted.Add(childId);
        else
            parents.Add(parentId);
    }

    public bool IsAppRoot(string sourceId) =>
        _metadata.TryGetValue(sourceId, out var metadata) && metadata.Kind == CopilotIdentityKind.AppRoot;

    public void MarkConflict(string sourceId)
    {
        if (ValidId(sourceId))
            _conflicted.Add(sourceId);
    }

    public CopilotSessionIdentity Resolve(string sourceId) =>
        Resolve(sourceId, new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, CopilotSessionIdentity>(StringComparer.Ordinal));

    public IReadOnlyList<CopilotSessionIdentity> ResolveObserved()
    {
        var cache = new Dictionary<string, CopilotSessionIdentity>(StringComparer.Ordinal);
        var result = new Dictionary<string, CopilotSessionIdentity>(StringComparer.Ordinal);
        foreach (var source in _observed.OrderBy(id => id, StringComparer.Ordinal))
        {
            var identity = Resolve(source, new HashSet<string>(StringComparer.Ordinal), cache);
            result[source] = identity;
            if (identity.Kind == CopilotIdentityKind.AppTaskChild)
                result[identity.OwnerId] = Resolve(identity.OwnerId, new HashSet<string>(StringComparer.Ordinal), cache);
        }
        return result.Values.OrderBy(identity => identity.SourceId, StringComparer.Ordinal).ToArray();
    }

    private CopilotSessionIdentity Resolve(string source, HashSet<string> path,
        Dictionary<string, CopilotSessionIdentity> cache)
    {
        if (cache.TryGetValue(source, out var cached))
            return cached;
        _metadata.TryGetValue(source, out var metadata);
        // A separately persisted session is independent, even if a task or UI parent claims it.
        if (metadata.Kind is CopilotIdentityKind.AppRoot or CopilotIdentityKind.Cli)
            return cache[source] = new(source, source, metadata.Kind, metadata.Name);
        var unknown = new CopilotSessionIdentity(source, source, CopilotIdentityKind.Unknown, metadata.Name);
        if (_conflicted.Contains(source) || path.Count >= 64 || !path.Add(source))
        {
            _diagnostic?.Invoke("identity-conflict");
            return unknown;
        }
        string? owner = null;
        string? ownerName = null;
        bool conflict = false;
        bool cli = false;
        if (_parents.TryGetValue(source, out var parents))
        {
            foreach (var parent in parents.OrderBy(id => id, StringComparer.Ordinal))
            {
                var candidate = Resolve(parent, path, cache);
                if (candidate.Kind == CopilotIdentityKind.Cli)
                {
                    cli = true;
                    continue;
                }
                if (candidate.Kind is not (CopilotIdentityKind.AppRoot or CopilotIdentityKind.AppTaskChild))
                {
                    conflict = true;
                    continue;
                }
                if (owner is not null && owner != candidate.OwnerId)
                    conflict = true;
                owner = candidate.OwnerId;
                ownerName = candidate.Name;
            }
        }
        path.Remove(source);
        if (cli && owner is not null)
            conflict = true;
        if (conflict)
            _diagnostic?.Invoke("identity-conflict");
        return cache[source] = conflict ? unknown
            : owner is not null ? new(source, owner, CopilotIdentityKind.AppTaskChild, ownerName)
            : cli ? new(source, source, CopilotIdentityKind.Cli, metadata.Name) : unknown;
    }

    internal static bool ValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

// Observe only updates bounded-per-source evidence in memory. All I/O, including
// initial discovery, happens in Scan (the background worker in production).
internal sealed class CopilotSessionIdentityResolver : IDisposable
{
    private const int MetadataBytes = 64 * 1024;
    private const int DirectoriesPerScan = 256;
    private const int TranscriptsPerScan = 16;
    private const int BytesPerTranscriptScan = 256 * 1024;
    private const int LinesPerTranscriptScan = 512;
    private const int MaxLineBytes = 64 * 1024;
    private static readonly TimeSpan DiscoveryAge = TimeSpan.FromDays(7);
    private readonly string _stateDirectory;
    private readonly Action<string>? _diagnostic;
    private readonly object _observationsGate = new();
    private readonly object _scanGate = new();
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly CopilotSessionIdentityIndex _index;
    private readonly HashSet<string> _metadataCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TranscriptCursor> _transcripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _diagnosticTimes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IEnumerator<string>? _directories;
    private Task? _worker;
    private bool _disposed;
    private int _transcriptRoundRobin;
    private int _metadataRoundRobin;
    private IReadOnlyList<CopilotSessionIdentity> _lastPublished = Array.Empty<CopilotSessionIdentity>();

    private sealed class Observation
    {
        public HashSet<string> Parents { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Hints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Overflow { get; set; }
    }

    private sealed class TranscriptCursor
    {
        public long Offset;
        public long Length;
        public DateTime Creation;
        public DateTime LastWrite;
        public byte[] Prefix = Array.Empty<byte>();
        public byte[] Tail = Array.Empty<byte>();
        public MemoryStream Line { get; } = new();
        public bool SkipLine;

        public void Reset()
        {
            Offset = 0;
            Length = 0;
            Prefix = Array.Empty<byte>();
            Tail = Array.Empty<byte>();
            Line.SetLength(0);
            SkipLine = false;
        }
    }

    public CopilotSessionIdentityResolver(string stateDirectory, Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _diagnostic = diagnostic;
        _index = new CopilotSessionIdentityIndex(Diagnose);
    }

    public void Observe(string sourceId, string? transcriptPath = null, string? parentId = null, string? childId = null)
    {
        if (!CopilotSessionIdentityIndex.ValidId(sourceId))
            return;
        lock (_observationsGate)
        {
            if (_disposed)
                return;
            var source = GetObservation(sourceId);
            if (CopilotSessionIdentityIndex.ValidId(parentId) && parentId != sourceId)
                AddParent(source, parentId!);
            if (CopilotSessionIdentityIndex.ValidId(childId) && childId != sourceId)
                AddParent(GetObservation(childId!), sourceId);
            if (!string.IsNullOrWhiteSpace(transcriptPath) && transcriptPath.Length <= 4096 && source.Hints.Count < 4)
                source.Hints.Add(transcriptPath);
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
    }

    public void Start(Action<IReadOnlyList<CopilotSessionIdentity>> onResolved)
    {
        ArgumentNullException.ThrowIfNull(onResolved);
        lock (_observationsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
                throw new InvalidOperationException("The identity resolver has already started.");
            _worker = Task.Run(() => RunAsync(onResolved));
        }
    }

    // One bounded pass, returning a complete snapshot (Unknown included), not a
    // delta. Large fixtures may need several passes to exhaust the fixed budgets.
    public IReadOnlyList<CopilotSessionIdentity> Scan()
    {
        lock (_scanGate)
        {
            KeyValuePair<string, Observation>[] observations;
            lock (_observationsGate)
            {
                if (_disposed)
                    return Array.Empty<CopilotSessionIdentity>();
                observations = _observations.Select(pair =>
                {
                    var copy = new Observation { Overflow = pair.Value.Overflow };
                    copy.Parents.UnionWith(pair.Value.Parents);
                    copy.Hints.UnionWith(pair.Value.Hints);
                    return new KeyValuePair<string, Observation>(pair.Key, copy);
                }).ToArray();
            }

            foreach (var (source, observation) in observations)
            {
                _index.Observe(source);
                _metadataCandidates.Add(source);
                foreach (var parent in observation.Parents)
                {
                    _metadataCandidates.Add(parent);
                    _index.Claim(source, parent);
                }
                if (observation.Overflow)
                {
                    _index.MarkConflict(source);
                    Diagnose("identity-conflict");
                }
                foreach (var hint in observation.Hints)
                {
                    var owner = ValidateHint(hint);
                    if (owner is not null)
                        _metadataCandidates.Add(owner);
                }
            }

            RefreshMetadata();
            DiscoverRoots();
            foreach (var (source, observation) in observations)
            {
                foreach (var hint in observation.Hints)
                {
                    var owner = ValidateHint(hint);
                    if (owner is null)
                        continue;
                    if (_index.IsAppRoot(owner))
                        _index.Claim(source, owner);
                }
            }
            foreach (var (source, _) in observations)
            {
                if (_index.Resolve(source).Kind == CopilotIdentityKind.AppTaskChild)
                    _transcripts.TryAdd(source, new TranscriptCursor());
            }
            ScanTranscripts();
            var identities = _index.ResolveObserved();
            if (identities.Any(identity => identity.Kind == CopilotIdentityKind.Unknown))
                Diagnose("identity-unresolved");
            return identities;
        }
    }

    private Observation GetObservation(string id)
    {
        if (!_observations.TryGetValue(id, out var observation))
            _observations[id] = observation = new();
        return observation;
    }

    private static void AddParent(Observation observation, string parent)
    {
        if (observation.Parents.Contains(parent))
            return;
        if (observation.Parents.Count >= 8)
            observation.Overflow = true;
        else
            observation.Parents.Add(parent);
    }

    private async Task RunAsync(Action<IReadOnlyList<CopilotSessionIdentity>> onResolved)
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                IReadOnlyList<CopilotSessionIdentity> identities;
                try { identities = Scan(); }
                catch (Exception exception) when (IsFileError(exception))
                {
                    Diagnose("identity-scan-unavailable");
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), _cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                if (_cancellation.IsCancellationRequested)
                    break;
                if (!identities.SequenceEqual(_lastPublished))
                {
                    _lastPublished = identities;
                    try { onResolved(identities); }
                    catch { Diagnose("identity-callback-failed"); }
                }
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        finally
        {
            ReleaseResources();
        }
    }

    private void DiscoverRoots()
    {
        try
        {
            _directories ??= Directory.EnumerateDirectories(_stateDirectory).GetEnumerator();
            for (int i = 0; i < DirectoriesPerScan && !_cancellation.IsCancellationRequested; i++)
            {
                if (!_directories.MoveNext())
                {
                    _directories.Dispose();
                    _directories = null;
                    break;
                }
                var directory = _directories.Current;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var id = Path.GetFileName(directory);
                if (!CopilotSessionIdentityIndex.ValidId(id))
                    continue;
                // Metadata alone does not identify liveness. Recent transcript
                // writes also admit long-running roots whose workspace is old.
                var cutoff = DateTime.UtcNow - DiscoveryAge;
                if (File.GetLastWriteTimeUtc(Path.Combine(directory, "workspace.yaml")) >= cutoff
                    || File.GetLastWriteTimeUtc(Path.Combine(directory, "events.jsonl")) >= cutoff)
                    ReadMetadata(id, true);
            }
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            _directories?.Dispose();
            _directories = null;
            Diagnose("identity-discovery-unavailable");
        }
    }

    private void RefreshMetadata()
    {
        var candidates = _metadataCandidates.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            return;
        for (int i = 0; i < Math.Min(DirectoriesPerScan, candidates.Length) && !_cancellation.IsCancellationRequested; i++)
        {
            var candidate = candidates[_metadataRoundRobin++ % candidates.Length];
            ReadMetadata(candidate, true);
            if (_index.Resolve(candidate).Kind == CopilotIdentityKind.AppTaskChild)
                _transcripts.TryAdd(candidate, new TranscriptCursor());
        }
        _metadataRoundRobin %= candidates.Length;
    }

    private void ReadMetadata(string id, bool indexTranscript)
    {
        try
        {
            var directory = Path.Combine(_stateDirectory, id);
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return;
            var file = Path.Combine(directory, "workspace.yaml");
            if (!File.Exists(file))
                return;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                return;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MetadataBytes)
            {
                Diagnose("identity-metadata-too-large");
                return;
            }
            var bytes = new byte[MetadataBytes + 1];
            int count = stream.ReadAtLeast(bytes, 1, throwOnEndOfStream: false);
            while (count < bytes.Length)
            {
                int read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                    break;
                count += read;
            }
            if (count > MetadataBytes)
                return;
            var text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (!TryMetadata(text, out var client, out var name))
            {
                Diagnose("identity-metadata-malformed");
                return;
            }
            _index.SetMetadata(id, client, name);
            if (indexTranscript && _index.IsAppRoot(id))
                _transcripts.TryAdd(id, new TranscriptCursor());
        }
        catch (Exception exception) when (IsFileError(exception) || exception is DecoderFallbackException)
        {
            Diagnose("identity-metadata-unavailable");
        }
    }

    private string? ValidateHint(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(full), "events.jsonl", StringComparison.OrdinalIgnoreCase))
                return null;
            var directory = Path.GetDirectoryName(full);
            if (directory is null || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(directory) ?? ""),
                Path.TrimEndingDirectorySeparator(_stateDirectory), StringComparison.OrdinalIgnoreCase))
                return null;
            var owner = Path.GetFileName(directory);
            return CopilotSessionIdentityIndex.ValidId(owner) ? owner : null;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return null;
        }
    }

    private void ScanTranscripts()
    {
        var roots = _transcripts.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (roots.Length == 0)
            return;
        for (int i = 0; i < Math.Min(TranscriptsPerScan, roots.Length) && !_cancellation.IsCancellationRequested; i++)
        {
            var root = roots[_transcriptRoundRobin++ % roots.Length];
            if (_index.Resolve(root).Kind is CopilotIdentityKind.AppRoot or CopilotIdentityKind.AppTaskChild)
                ReadTranscript(root, _transcripts[root]);
        }
        _transcriptRoundRobin %= roots.Length;
    }

    private void ReadTranscript(string root, TranscriptCursor cursor)
    {
        try
        {
            var file = Path.Combine(_stateDirectory, root, "events.jsonl");
            if (!File.Exists(file))
                return;
            if ((File.GetAttributes(Path.GetDirectoryName(file)!) & FileAttributes.ReparsePoint) != 0)
                return;
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            bool replaced = length < cursor.Offset || length < cursor.Length
                || (cursor.Creation != default && cursor.Creation != info.CreationTimeUtc)
                || (length == cursor.Length && cursor.LastWrite != default && cursor.LastWrite != info.LastWriteTimeUtc)
                || !Matches(stream, 0, cursor.Prefix)
                || !Matches(stream, cursor.Offset - cursor.Tail.Length, cursor.Tail);
            if (replaced)
                cursor.Reset();
            cursor.Creation = info.CreationTimeUtc;
            cursor.LastWrite = info.LastWriteTimeUtc;
            cursor.Length = length;
            stream.Position = cursor.Offset;
            int bytes = 0;
            int lines = 0;
            var buffer = new byte[8192];
            while (bytes < BytesPerTranscriptScan && lines < LinesPerTranscriptScan
                && !_cancellation.IsCancellationRequested)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, BytesPerTranscriptScan - bytes));
                if (read == 0)
                    break;
                for (int i = 0; i < read; i++)
                {
                    byte value = buffer[i];
                    cursor.Offset++;
                    bytes++;
                    if (value == (byte)'\n')
                    {
                        if (!cursor.SkipLine && cursor.Line.Length > 0)
                            ReadIdentityLine(root, cursor.Line.GetBuffer().AsMemory(0, (int)cursor.Line.Length));
                        cursor.Line.SetLength(0);
                        cursor.SkipLine = false;
                        if (++lines >= LinesPerTranscriptScan)
                            break;
                    }
                    else if (!cursor.SkipLine)
                    {
                        if (cursor.Line.Length >= MaxLineBytes)
                        {
                            cursor.Line.SetLength(0);
                            cursor.SkipLine = true;
                            Diagnose("identity-transcript-line-too-large");
                        }
                        else
                            cursor.Line.WriteByte(value);
                    }
                }
            }
            // Fingerprints cover only consumed bytes, so completing an unfinished
            // line is an append, not a false replacement. Payloads never leave here.
            cursor.Prefix = ReadBytes(stream, 0, (int)Math.Min(cursor.Offset, 256));
            cursor.Tail = ReadBytes(stream, Math.Max(0, cursor.Offset - 256), (int)Math.Min(cursor.Offset, 256));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            Diagnose("identity-transcript-unavailable");
        }
    }

    private void ReadIdentityLine(string root, ReadOnlyMemory<byte> line)
    {
        try
        {
            if (line.Length >= 3 && line.Span[0] == 0xef && line.Span[1] == 0xbb && line.Span[2] == 0xbf)
                line = line[3..];
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("subagent.started" or "subagent.completed")
                || !record.TryGetProperty("agentId", out var child) || child.ValueKind != JsonValueKind.String)
                return;
            var childId = child.GetString();
            if (!CopilotSessionIdentityIndex.ValidId(childId))
                return;
            // Top-level agentId is the child. Do not inspect prompts, tool output,
            // names, cwd, trace IDs, or arbitrary nested data for relationships.
            _index.Claim(childId!, root);
            _metadataCandidates.Add(childId!);
        }
        catch (JsonException)
        {
            Diagnose("identity-transcript-malformed");
        }
    }

    private static bool Matches(FileStream stream, long position, byte[] expected) =>
        expected.Length == 0 || (position >= 0 && ReadBytes(stream, position, expected.Length).AsSpan().SequenceEqual(expected));

    private static byte[] ReadBytes(FileStream stream, long position, int count)
    {
        stream.Position = position;
        var bytes = new byte[count];
        int read = stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
        return read == count ? bytes : bytes[..read];
    }

    private static bool TryMetadata(string text, out string? client, out string? name)
    {
        client = null;
        name = null;
        bool hasClient = false;
        bool hasName = false;
        var lines = text.TrimStart('\ufeff').Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
                continue;
            int separator = line.IndexOf(':');
            if (separator < 0)
                continue;
            string key = line[..separator];
            if (key is not ("client_name" or "name"))
                continue;
            if (!TryScalar(line[(separator + 1)..], out var value))
                return false;
            if (key == "client_name")
            {
                if (hasClient)
                    return false;
                // A plain scalar still being appended is not complete evidence
                // of an arbitrary CLI client. The exact App marker is safe.
                client = lineIndex == lines.Length - 1 && value != "github/autopilot"
                    ? null : value;
                hasClient = true;
            }
            else
            {
                if (hasName)
                    return false;
                name = value;
                hasName = true;
            }
        }
        return true;
    }

    private static bool TryScalar(string text, out string? value)
    {
        value = null;
        text = text.Trim();
        if (text.Length == 0 || text[0] == '#')
            return true;
        if (text[0] is '\'' or '"')
        {
            char quote = text[0];
            var scalar = new StringBuilder();
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        scalar.Append('\'');
                        i++;
                        continue;
                    }
                    string rest = text[(i + 1)..].TrimStart();
                    if (rest.Length != 0 && rest[0] != '#')
                        return false;
                    value = scalar.ToString();
                    return true;
                }
                if (quote == '"' && c == '\\')
                {
                    if (++i >= text.Length)
                        return false;
                    c = text[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => '\0' };
                    if (c == '\0')
                        return false;
                }
                scalar.Append(c);
            }
            return false;
        }
        if (text[0] is '|' or '>' or '[' or '{' or '&' or '*' or '!' || text.Contains(": ", StringComparison.Ordinal))
            return false;
        int comment = text.IndexOf(" #", StringComparison.Ordinal);
        value = (comment < 0 ? text : text[..comment]).TrimEnd();
        if (value is "~" or "null" or "Null" or "NULL")
            value = null;
        return true;
    }

    private static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    private void Diagnose(string reason)
    {
        // Fixed reason codes only: never paths, IDs, names, exception messages,
        // hook payloads, transcript contents, prompts, or tool results.
        if (_diagnostic is null)
            return;
        lock (_diagnosticTimes)
        {
            var now = DateTime.UtcNow;
            if (_diagnosticTimes.TryGetValue(reason, out var previous) && now - previous < TimeSpan.FromMinutes(1))
                return;
            _diagnosticTimes[reason] = now;
        }
        try { _diagnostic(reason); }
        catch { /* Diagnostics must not stop resolution. */ }
    }

    public void Dispose()
    {
        lock (_observationsGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cancellation.Cancel();
        }
        // Never synchronously wait for disk I/O or a callback on the UI thread.
        // The consumer must reject an already-in-flight callback by generation.
        if (_worker is null)
            _ = Task.Run(ReleaseResources);
    }

    private void ReleaseResources()
    {
        lock (_scanGate)
        {
            _directories?.Dispose();
            _directories = null;
            foreach (var cursor in _transcripts.Values)
                cursor.Line.Dispose();
        }
        lock (_observationsGate)
        {
            _disposed = true;
            _wake.Dispose();
            _cancellation.Dispose();
        }
    }
}
