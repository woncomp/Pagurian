using System.Globalization;
using System.Text.Json;

namespace Pagurian.Modules.Copilot;

enum CopilotSessionStatus { Idle, Working, Blocked, Unknown }

sealed record CopilotBlocker(
    string SourceId, string OwnerId, DateTimeOffset BlockedSince,
    string EventName, DateTimeOffset EventAt);

sealed record CopilotTransition(
    string SourceId, string OwnerId, string EventName, DateTimeOffset EventAt,
    CopilotSessionStatus Before, CopilotSessionStatus After, string Reason);

sealed record CopilotSessionNode(
    string SessionId, string? ParentId, string Name, CopilotSessionStatus? Status,
    string Lifecycle, CopilotBlocker? Blocker, CopilotSessionDetails Details);

sealed record CopilotRecentHook(DateTimeOffset At, string Name, string ToolName, long Sequence)
{
    public string Line => $"{At.ToLocalTime():HH:mm:ss} {Name} {ToolName}";
    internal static string Token(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" :
        string.Concat(value.Take(256).Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c));
}

static class CopilotProjectName
{
    // Windows paths must also work in the dependency-free fixture on other OSes.
    public static string FromCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Project unavailable";
        var trimmed = cwd.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return cwd[0].ToString();
        int separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return trimmed[(separator + 1)..];
    }
}

// Stable display-owner model. Source state and visual debounce deliberately
// live separately: a pending visual change must never lose a permission owner.
sealed class CopilotSession
{
    public required string SessionId { get; init; }
    public string Name { get; set; } = "";
    public bool NameResolved { get; set; }
    public CopilotSessionStatus Status { get; set; }
    public CopilotSessionStatus? PendingStatus { get; set; }
    public DateTimeOffset PendingStatusSince { get; set; }
    public string LastEventName { get; set; } = "";
    public string LastEventDump { get; set; } = "";
    public string LastEventSourceId { get; set; } = "";
    public IReadOnlyList<CopilotBlocker> BlockingSources { get; set; } = [];
    public IReadOnlyList<CopilotTransition> Transitions { get; set; } = [];
    public CopilotClientKind Client { get; set; }
    public string? ClientMarker { get; set; }
    public string? Cwd { get; set; }
    public string ProjectName => CopilotProjectName.FromCwd(Cwd);
    public IReadOnlyList<CopilotSessionNode> Nodes { get; set; } = [];
    public IReadOnlyList<CopilotRecentHook> RecentHooks { get; set; } = [];
    public CopilotSessionDetails Details { get; set; } = CopilotSessionDetails.Empty;
    public CopilotSessionDetails GroupDetails { get; set; } = CopilotSessionDetails.Empty;
    public bool IsSdk { get; set; }
    public CopilotSdkReadHealth SdkReadHealth { get; set; } = CopilotSdkReadHealth.Healthy;
    public string? SdkStatusReason { get; set; }
    public DateTimeOffset? SdkLastReadAt { get; set; }
    public bool SdkHistoryPartial { get; set; }
    public IReadOnlyList<CopilotSdkPersistedEvent> RecentPersistedEvents { get; set; } = [];
    public event Action? Changed;

    internal void NotifyChanged() => Changed?.Invoke();
}

// Only identity, lifecycle and presentation scalars are interpreted. Dump retains the
// original payload, notably sessionId on child events and agentId on stops.
sealed record CopilotHookEvent(
    string Name, string SourceId, DateTimeOffset At, string Dump,
    string? AgentId = null, string? ParentId = null, string? TranscriptPath = null,
    string? ToolName = null, string? Cwd = null, string? Reason = null)
{
    public static CopilotHookEvent? Parse(string name, string? json, DateTimeOffset receivedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
                return null;
            var source = Text(payload, "sessionId");
            if (!CopilotSessionIdentityIndex.ValidId(source))
                return null;
            var at = receivedAt;
            if (payload.TryGetProperty("timestamp", out var timestamp))
            {
                if (timestamp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed))
                    at = parsed;
                else if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var milliseconds))
                {
                    try { at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            var dump = JsonSerializer.Serialize(new { loggedAt = receivedAt, @event = name, payload },
                new JsonSerializerOptions { WriteIndented = true });
            return new(name, source!, at, dump, Text(payload, "agentId"),
                Text(payload, "parentSessionId"), Text(payload, "transcriptPath"),
                Text(payload, "toolName"), Text(payload, "cwd"), Text(payload, "reason"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}

// Pure, UI-thread-owned reducer. Unknown identities retain a reduced state, not
// an event queue. Timestamps order each source independently; arrival sequence
// breaks ties for latest details only. A stop at the same timestamp as work is
// conservatively ignored (hooks have no reliable per-tool correlation IDs).
sealed class CopilotSessionState
{
    private sealed class Source(string id)
    {
        public CopilotSessionIdentity Identity = new(id, id, CopilotIdentityKind.Unknown, null);
        public bool HasOwnHook;
        public bool Active;
        public bool Ended;
        public DateTimeOffset EndedAt = DateTimeOffset.MinValue;
        public CopilotSessionStatus Status;
        public CopilotBlocker? Blocker;
        public DateTimeOffset StateAt = DateTimeOffset.MinValue;
        public string StateEvent = "";
        // Own lifecycle evidence must survive activity's stale-event filter,
        // including while identity is unknown. App task starts are separate.
        public DateTimeOffset ExplicitStartAt = DateTimeOffset.MinValue;
        public CopilotHookEvent? ExplicitEnd;
        public DateTimeOffset Epoch = DateTimeOffset.MinValue;
        public CopilotHookEvent? Latest;
        public long LatestSequence;
        public (CopilotHookEvent Event, long Sequence)? Lifecycle;
        public (CopilotHookEvent Event, long Sequence)? LifecycleStart;
        public long AppliedLifecycle;
        public long AppliedLifecycleStart;
        public readonly List<CopilotRecentHook> Recent = [];
        public string? Cwd;
        public DateTimeOffset CwdAt = DateTimeOffset.MinValue;
        public CopilotSessionDetails? DetailsInput;
        public CopilotSessionDetails DetailsOutput = CopilotSessionDetails.Empty;
        public DateTimeOffset DetailsEpoch;
        public DateTimeOffset TranscriptTerminalAt = DateTimeOffset.MinValue;
        public CopilotAppInstance? App;
        public bool Hidden;
        public bool Restored;
        public DateTimeOffset VisibilityFence = DateTimeOffset.MinValue;
        public DateTimeOffset WorkEpoch = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CopilotSession> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<CopilotTransition> _transitions = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<CopilotTransition>? _diagnostic;
    private long _sequence;
    private long _lifecycleVersion;
    private readonly Dictionary<string, (CopilotAppSessionEvidence Evidence, DateTimeOffset At)> _appEvidence = new();
    private readonly HashSet<string> _exitedApps = new(StringComparer.Ordinal);
    private const int TransitionLimit = 256;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    public CopilotSessionState(Func<DateTimeOffset>? clock = null, Action<CopilotTransition>? diagnostic = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _diagnostic = diagnostic;
    }

    public event Action<CopilotSession>? SessionStarted;
    public event Action<CopilotSession>? SessionEnded;
    public IReadOnlyCollection<CopilotSession> Sessions => _sessions.Values;
    public IReadOnlyCollection<CopilotTransition> Transitions => _transitions;
    public CopilotSession? Find(string id) => _sessions.GetValueOrDefault(id);

    private Source Get(string id)
    {
        if (!_sources.TryGetValue(id, out var source))
            _sources[id] = source = new(id);
        return source;
    }

    public void Handle(CopilotHookEvent hook)
    {
        if (!CopilotSessionIdentityIndex.ValidId(hook.SourceId))
            return;
        var source = Get(hook.SourceId);
        long sequence = ++_sequence;
        var appOwner = source.Identity.Kind is CopilotIdentityKind.AppRoot or CopilotIdentityKind.AppTaskChild
            ? Get(source.Identity.OwnerId) : null;
        // Hidden is a presentation gate, not an event sink. Reduce tentative
        // work after the removal fence until positive load evidence can admit it.
        if (appOwner is not null && (hook.At < appOwner.WorkEpoch ||
            (appOwner.Hidden && hook.At <= appOwner.VisibilityFence)))
        {
            Trace(source, hook, source.Status, "app-visibility-or-runtime-fence");
            return;
        }
        // Ended roots cannot be revived by late work. Unknown/App-task sources
        // may retain tentative activity, but it stays inactive until an explicit
        // newer session/task start proves that the older child exit is stale.
        if ((source.Ended && source.Identity.Kind is (CopilotIdentityKind.Cli or CopilotIdentityKind.AppRoot) &&
                hook.Name != "sessionEnd" &&
                (hook.Name != "sessionStart" || hook.At <= source.EndedAt)) ||
            !InOwnerGeneration(source, hook.At))
        {
            Trace(source, hook, source.Status, "ended-generation");
            return;
        }
        source.HasOwnHook = true;
        source.Recent.Add(new(hook.At, CopilotRecentHook.Token(hook.Name),
            CopilotRecentHook.Token(hook.ToolName), sequence));
        source.Recent.Sort((a, b) =>
        {
            int time = b.At.CompareTo(a.At);
            return time != 0 ? time : b.Sequence.CompareTo(a.Sequence);
        });
        if (source.Recent.Count > 5) source.Recent.RemoveRange(5, source.Recent.Count - 5);
        if (!string.IsNullOrWhiteSpace(hook.Cwd) && hook.At >= source.CwdAt)
        {
            source.Cwd = hook.Cwd;
            source.CwdAt = hook.At;
        }
        SetLatest(source, hook, sequence);
        Apply(source, hook);
        if (hook.Name is "subagentStart" or "subagentStop" &&
            CopilotSessionIdentityIndex.ValidId(hook.AgentId) && hook.AgentId != hook.SourceId)
        {
            var child = Get(hook.AgentId!);
            if (hook.Name == "subagentStart" &&
                (child.LifecycleStart is not { } start || hook.At > start.Event.At))
                child.LifecycleStart = (hook, sequence);
            if (child.Lifecycle is not { } previous || hook.At > previous.Event.At ||
                (hook.At == previous.Event.At && hook.Name == "subagentStart" && previous.Event.Name != hook.Name))
                child.Lifecycle = (hook, sequence);
        }
        Reconcile();
    }

    public void Resolve(IReadOnlyList<CopilotSessionIdentity> identities, CopilotAppLifecycleSnapshot? snapshot = null)
    {
        if (snapshot is not null)
        {
            if (snapshot.Version <= _lifecycleVersion) return;
            _lifecycleVersion = snapshot.Version;
            foreach (var evidence in snapshot.Sessions)
                _appEvidence[evidence.SessionId] = (evidence, snapshot.ObservedAt);
            foreach (var app in snapshot.Exited) _exitedApps.Add(app.Key);
        }
        // Assign the complete batch before any cell notifications: a child is
        // never briefly published as a root while its owner is being attached.
        foreach (var identity in identities)
        {
            if (CopilotSessionIdentityIndex.ValidId(identity.SourceId) &&
                CopilotSessionIdentityIndex.ValidId(identity.OwnerId))
            {
                var source = Get(identity.SourceId);
                bool changed = source.Identity != identity;
                source.Identity = identity;
                if (source.Blocker is { } blocker)
                    source.Blocker = blocker with { OwnerId = identity.OwnerId };
                if (changed && source.Latest is { } latest)
                    Trace(source, latest, source.Status, "identity-attached");
            }
        }
        ReconcileAppLifecycle();
        Reconcile();
    }

    private void ReconcileAppLifecycle()
    {
        foreach (var owner in _sources.Values.Where(s => s.Identity.Kind == CopilotIdentityKind.AppRoot).ToArray())
        {
            _appEvidence.TryGetValue(owner.Identity.SourceId, out var entry);
            var evidence = entry.Evidence;
            if (evidence?.Archive == CopilotArchiveState.Archived)
            {
                HideApp(owner, entry.At, "app-archive");
                continue;
            }
            bool loaded = evidence is { App: not null, LoadedAt: not null }
                && !_exitedApps.Contains(evidence.App.Key);
            bool restore = loaded && evidence!.Archive == CopilotArchiveState.Live &&
                (!owner.Hidden || evidence.LoadedAt > owner.VisibilityFence);
            // Process ownership is useful even when SQLite is unavailable, but
            // it must not by itself admit a new or previously hidden cell.
            bool attach = loaded && !owner.Hidden && (_sessions.ContainsKey(owner.Identity.SourceId) ||
                owner.HasOwnHook || _sources.Values.Any(s => s.Identity.OwnerId == owner.Identity.SourceId &&
                    s.Active && !s.Ended));
            if (restore || attach)
            {
                var app = evidence!.App!;
                if (owner.App?.Key != app.Key || owner.Hidden)
                {
                    var floor = owner.Hidden && owner.VisibilityFence >= app.StartedAt
                        ? evidence.LoadedAt!.Value : app.StartedAt;
                    owner.App = app;
                    owner.WorkEpoch = floor;
                    owner.Hidden = false;
                    owner.Restored |= restore;
                    foreach (var member in _sources.Values.Where(s => s.Identity.OwnerId == owner.Identity.SourceId))
                    {
                        if (member.StateAt >= floor)
                        {
                            // Fresh hooks beat initial Idle, but not with a blocker
                            // timestamp or terminal fence inherited from the old run.
                            if (member.Blocker is { } blocker && blocker.BlockedSince < floor)
                                member.Blocker = blocker with
                                {
                                    BlockedSince = member.StateAt, EventAt = member.StateAt,
                                    EventName = member.StateEvent,
                                };
                            if (member.EndedAt < floor) member.Ended = false;
                            continue;
                        }
                        member.Status = CopilotSessionStatus.Idle;
                        member.Blocker = null;
                        member.Active = member == owner;
                        member.Ended = false;
                        member.EndedAt = floor;
                        member.StateAt = floor;
                        member.StateEvent = member == owner ? "appRestore" : "subagentStop";
                        if (member.Lifecycle is { } lifecycle && lifecycle.Event.At < floor)
                            member.AppliedLifecycle = lifecycle.Sequence;
                        if (member.LifecycleStart is { } start && start.Event.At < floor)
                            member.AppliedLifecycleStart = start.Sequence;
                    }
                    if (_sessions.TryGetValue(owner.Identity.SourceId, out var cell))
                    {
                        var active = _sources.Values.Where(s => s.Identity.OwnerId == owner.Identity.SourceId &&
                            s.Active && !s.Ended && s.StateAt >= floor).ToArray();
                        var candidate = active.Any(s => s.Blocker is not null) ? CopilotSessionStatus.Blocked
                            : active.Any(s => s.Status == CopilotSessionStatus.Working) ? CopilotSessionStatus.Working
                            : CopilotSessionStatus.Idle;
                        if (cell.Status != candidate) cell.Status = CopilotSessionStatus.Idle;
                        if (cell.PendingStatus != candidate || cell.PendingStatusSince < floor)
                            cell.PendingStatus = null;
                    }
                    Trace(owner, new("appRestore", owner.Identity.SourceId, entry.At, ""),
                        CopilotSessionStatus.Idle, "app-runtime-restored");
                }
            }
            if (owner.App is { } current && _exitedApps.Contains(current.Key))
                HideApp(owner, entry.At > current.StartedAt ? entry.At : _clock(), "app-exit");
        }
    }

    private void HideApp(Source owner, DateTimeOffset at, string reason)
    {
        if (owner.Hidden) return;
        owner.Hidden = true;
        owner.VisibilityFence = at;
        foreach (var member in _sources.Values.Where(s => s.Identity.OwnerId == owner.Identity.SourceId))
        {
            member.Active = false;
            member.Blocker = null;
            member.Status = CopilotSessionStatus.Idle;
        }
        Trace(owner, new("appVisibility", owner.Identity.SourceId, at, ""), owner.Status, reason);
    }

    private bool InOwnerGeneration(Source source, DateTimeOffset at)
    {
        if (source.Identity.Kind != CopilotIdentityKind.AppTaskChild)
            return true;
        var owner = Get(source.Identity.OwnerId);
        return !owner.Ended && (!owner.Hidden || at > owner.VisibilityFence) &&
            at >= owner.Epoch && at >= owner.WorkEpoch;
    }

    private void Apply(Source source, CopilotHookEvent hook, bool lifecycle = false)
    {
        if (hook.Name == "sessionStart" && hook.At > source.ExplicitStartAt)
            source.ExplicitStartAt = hook.At;
        if (hook.Name == "sessionEnd")
        {
            if (source.ExplicitEnd is null || hook.At > source.ExplicitEnd.At)
                source.ExplicitEnd = hook;
            if (source.Identity.Kind == CopilotIdentityKind.Cli)
            {
                ReconcileOwnerLifecycle(source);
                return;
            }
        }
        var status = hook.Name switch
        {
            "sessionStart" or "agentStop" or "sessionEnd" => (CopilotSessionStatus?)CopilotSessionStatus.Idle,
            "userPromptSubmitted" or "preToolUse" or "postToolUse" or "postToolUseFailure" => CopilotSessionStatus.Working,
            "permissionRequest" => CopilotSessionStatus.Blocked,
            "subagentStart" when lifecycle => CopilotSessionStatus.Working,
            "subagentStop" when lifecycle => CopilotSessionStatus.Idle,
            _ => null,
        };
        if (status is null)
            return;
        // Unknown/App-task sources may retain post-exit activity pending a newer App
        // task start. An explicit session start resets that tentative history.
        if (source.Ended && hook.Name == "sessionStart" && hook.At > source.EndedAt)
            source.StateAt = source.EndedAt;
        if (hook.At < source.StateAt ||
            (hook.At == source.StateAt && (source.StateEvent == hook.Name ||
                (status == CopilotSessionStatus.Idle && hook.Name != "sessionEnd"))))
        {
            Trace(source, hook, source.Status, "stale-or-duplicate");
            return;
        }
        var before = source.Status;
        bool wasActive = source.Active;
        source.StateAt = hook.At;
        source.StateEvent = hook.Name;
        source.Status = status.Value;
        source.Active = hook.Name is not ("sessionEnd" or "subagentStop");
        if (hook.Name == "sessionStart")
        {
            if (source.Ended)
                source.Epoch = hook.At;
            source.Ended = false;
        }
        else if (hook.Name == "sessionEnd")
        {
            source.Ended = source.Identity.Kind != CopilotIdentityKind.AppRoot;
            source.EndedAt = hook.At;
        }
        if (status == CopilotSessionStatus.Blocked)
            source.Blocker ??= new(source.Identity.SourceId, source.Identity.OwnerId,
                hook.At, hook.Name, hook.At);
        else
            source.Blocker = null;
        if (before != source.Status || wasActive != source.Active || hook.Name == "sessionEnd")
            Trace(source, hook, before, hook.Name == "sessionEnd" && source.Identity.Kind == CopilotIdentityKind.AppRoot
                ? hook.Reason == "complete" ? "app-turn-complete" : "app-end-awaiting-evidence"
                : hook.Name is "sessionEnd" or "subagentStop" ? "end" : "state");
    }

    private static void SetLatest(Source source, CopilotHookEvent hook, long sequence)
    {
        if (source.Latest is null || hook.At > source.Latest.At ||
            (hook.At == source.Latest.At && sequence > source.LatestSequence))
        {
            source.Latest = hook;
            source.LatestSequence = sequence;
        }
    }

    private void Reconcile()
    {
        // Identity may have been unknown when an exit lost the child/activity
        // timestamp comparison. Independent clients need an explicit newer
        // sessionStart; App completion never establishes that terminal fence.
        foreach (var source in _sources.Values)
            ReconcileOwnerLifecycle(source);

        foreach (var source in _sources.Values.ToArray())
        {
            if (source.Identity.Kind != CopilotIdentityKind.AppTaskChild)
                continue;
            ReconcileTranscriptTerminal(source);
            if (!InOwnerGeneration(source, source.StateAt))
            {
                if (source.Active || source.Blocker is not null)
                {
                    var before = source.Status;
                    source.Active = false;
                    source.Status = CopilotSessionStatus.Idle;
                    source.Blocker = null;
                    if (source.Latest is { } latest)
                        Trace(source, latest, before, "owner-generation-ended");
                }
            }
            // Retain the newest start as well as the latest lifecycle record:
            // start -> stop before identity resolves must still invalidate an
            // older child exit and permit a subsequent multi-turn resume.
            if (source.LifecycleStart is { } start && start.Sequence != source.AppliedLifecycleStart &&
                ApplyLifecycle(source, start))
                source.AppliedLifecycleStart = start.Sequence;
            if (source.Lifecycle is { } lifecycle && lifecycle.Sequence != source.AppliedLifecycle)
            {
                if (lifecycle.Sequence == source.AppliedLifecycleStart || ApplyLifecycle(source, lifecycle))
                    source.AppliedLifecycle = lifecycle.Sequence;
            }
        }

        var groups = _sources.Values
            .Where(s => s.Identity.Kind != CopilotIdentityKind.Unknown)
            .GroupBy(s => s.Identity.OwnerId)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ownerId, members) in groups)
        {
            var owner = Get(ownerId);
            if (owner.Ended || owner.Hidden || owner.Identity.Kind is not (CopilotIdentityKind.AppRoot or CopilotIdentityKind.Cli))
                continue;
            var active = members.Where(s => s.Active && !s.Ended && InOwnerGeneration(s, s.StateAt)).ToArray();
            if (!owner.HasOwnHook && !owner.Restored && !_sessions.ContainsKey(ownerId) && active.Length == 0)
                continue;
            desired.Add(ownerId);
            bool created = !_sessions.TryGetValue(ownerId, out var session);
            if (created)
                _sessions[ownerId] = session = new() { SessionId = ownerId, Name = ownerId };
            session!.Name = owner.Identity.Name ?? ownerId;
            session.NameResolved = !string.IsNullOrWhiteSpace(owner.Identity.Name);
            session.Client = owner.Identity.Client;
            session.ClientMarker = owner.Identity.ClientMarker;
            session.Cwd = owner.Identity.Cwd ?? (owner.CwdAt >= owner.Epoch ? owner.Cwd : null);
            var visible = members.Where(s => s == owner || owner.Epoch == DateTimeOffset.MinValue
                    || s.StateAt >= owner.Epoch || s.Latest?.At >= owner.Epoch
                    || s.Identity.Details?.Observations.Any(o => o.At >= owner.Epoch) == true)
                .Select(s => s.Identity.SourceId).ToHashSet(StringComparer.Ordinal);
            // A current descendant still needs its immediate ancestors, but
            // their previous-generation status/details must not leak back in.
            foreach (var member in members.Where(s => visible.Contains(s.Identity.SourceId)).ToArray())
            {
                var parent = member.Identity.ParentId;
                for (int depth = 0; parent is not null && depth < 64; depth++)
                {
                    var ancestor = members.FirstOrDefault(s => s.Identity.SourceId == parent);
                    if (ancestor is null || !visible.Add(parent)) break;
                    parent = ancestor.Identity.ParentId;
                }
            }
            var nodes = members.Where(s => visible.Contains(s.Identity.SourceId)).Select(s => Node(s, owner.Epoch, owner.WorkEpoch))
                .OrderBy(n => n.SessionId == ownerId ? 0 : 1)
                .ThenBy(n => n.SessionId, StringComparer.Ordinal).ToArray();
            bool detailChange = session.Nodes.Count != nodes.Length || nodes.Any(n =>
                !ReferenceEquals(session.Nodes.FirstOrDefault(old => old.SessionId == n.SessionId)?.Details, n.Details));
            session.Nodes = nodes;
            session.Details = nodes.Single(n => n.SessionId == ownerId).Details;
            if (detailChange)
                session.GroupDetails = CopilotSessionDetails.Aggregate(nodes.Select(n => n.Details));
            session.RecentHooks = members.SelectMany(s => s.Recent).Where(h => h.At >= owner.Epoch)
                .OrderByDescending(h => h.At).ThenByDescending(h => h.Sequence).Take(5).ToArray();
            session.BlockingSources = active.Where(s => s.Blocker is not null)
                .Select(s => s.Blocker! with { OwnerId = ownerId }).OrderBy(b => b.BlockedSince).ToArray();
            var latest = members.Where(s => s.Latest is not null && InOwnerGeneration(s, s.Latest.At))
                .OrderByDescending(s => s.Latest!.At).ThenByDescending(s => s.LatestSequence).FirstOrDefault();
            if (latest?.Latest is { } hook)
            {
                session.LastEventName = hook.Name;
                session.LastEventDump = hook.Dump;
                session.LastEventSourceId = hook.SourceId;
            }
            session.Transitions = _transitions.Where(t => t.OwnerId == ownerId ||
                members.Any(m => m.Identity.SourceId == t.SourceId)).TakeLast(64).ToArray();
            var candidate = session.BlockingSources.Count > 0 ? CopilotSessionStatus.Blocked
                : active.Any(s => s.Status == CopilotSessionStatus.Working) ? CopilotSessionStatus.Working
                : CopilotSessionStatus.Idle;
            UpdateCandidate(session, candidate, _clock());
            if (created)
                SessionStarted?.Invoke(session);
        }
        foreach (var id in _sessions.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            var ended = _sessions[id];
            _sessions.Remove(id);
            SessionEnded?.Invoke(ended);
        }
    }

    private void ReconcileTranscriptTerminal(Source source)
    {
        if (source.Identity.Details?.TerminalAt is not { } at ||
            at <= source.TranscriptTerminalAt || !InOwnerGeneration(source, at))
            return;
        // Keep the completion fence even after a newer transcript start replaces
        // the displayed lifecycle. Starting again cannot revive an old permission.
        source.TranscriptTerminalAt = at;
        if (source.StateAt <= at)
        {
            source.Active = false;
            source.Status = CopilotSessionStatus.Idle;
            source.Blocker = null;
            source.StateAt = at;
        }
        else if (source.Blocker is { } blocker && blocker.BlockedSince <= at)
        {
            source.Blocker = blocker with
            {
                BlockedSince = source.StateAt,
                EventAt = source.StateAt,
                EventName = source.StateEvent,
            };
        }
    }

    private static CopilotSessionNode Node(Source source, DateTimeOffset epoch, DateTimeOffset workEpoch)
    {
        var input = source.Identity.Details ?? CopilotSessionDetails.Empty;
        if (!ReferenceEquals(input, source.DetailsInput) || epoch != source.DetailsEpoch)
        {
            source.DetailsInput = input;
            source.DetailsEpoch = epoch;
            source.DetailsOutput = input.ForGeneration(epoch);
        }
        var details = source.DetailsOutput;
        bool hasCurrentState = source.StateAt >= epoch && source.StateAt >= workEpoch &&
            source.StateAt != DateTimeOffset.MinValue
            && source.StateAt > source.TranscriptTerminalAt;
        string lifecycle = !hasCurrentState ? "Unknown" : source.Ended ? "Ended"
            : source.StateEvent == "subagentStop" ? "Completed"
            : source.StateAt == DateTimeOffset.MinValue ? "Unknown" : "Active";
        if (source.Identity.Kind != CopilotIdentityKind.AppRoot &&
            details.LifecycleAt is { } at && at >= source.StateAt &&
            details.Lifecycle is "Completed" or "Ended")
            lifecycle = details.Lifecycle;
        return new(source.Identity.SourceId, source.Identity.ParentId,
            source.Identity.Name ?? source.Identity.SourceId,
            hasCurrentState ? source.Status : null,
            lifecycle, !hasCurrentState || lifecycle is "Completed" or "Ended" ? null : source.Blocker, details);
    }

    private void ReconcileOwnerLifecycle(Source source)
    {
        if (source.Identity.Kind == CopilotIdentityKind.AppRoot)
        {
            // A deferred end was reduced before client metadata was available.
            // App turn completion never establishes a conversation tombstone.
            source.Ended = false;
            source.Epoch = DateTimeOffset.MinValue;
            return;
        }
        if (source.Identity.Kind is not (CopilotIdentityKind.AppRoot or CopilotIdentityKind.Cli) ||
            source.ExplicitEnd is not { } exit)
            return;
        source.EndedAt = exit.At;
        if (source.ExplicitStartAt > exit.At)
        {
            source.Ended = false;
            // Also fence old members when the exit arrives AFTER its restart.
            source.Epoch = source.ExplicitStartAt;
            return;
        }
        var before = source.Status;
        bool changed = !source.Ended || source.Active || source.Blocker is not null;
        source.Ended = true;
        source.Active = false;
        source.StateAt = exit.At;
        source.StateEvent = "sessionEnd";
        source.Status = CopilotSessionStatus.Idle;
        source.Blocker = null;
        if (changed)
            Trace(source, exit, before, "end");
    }

    private bool ApplyLifecycle(Source source, (CopilotHookEvent Event, long Sequence) lifecycle)
    {
        var emitter = Get(lifecycle.Event.SourceId);
        if (emitter.Identity.Kind is not (CopilotIdentityKind.AppRoot or CopilotIdentityKind.AppTaskChild) ||
            emitter.Identity.OwnerId != source.Identity.OwnerId)
            return false; // CLI lifecycle records are details-only.
        if (!InOwnerGeneration(source, lifecycle.Event.At))
            return true;
        if (source.Ended)
        {
            if (lifecycle.Event.Name != "subagentStart" || lifecycle.Event.At <= source.EndedAt)
                return true;
            // Ordering must not depend on when identity became available:
            // a newer explicit task start invalidates an older child exit,
            // while reduced own events newer than that start stay intact.
            source.Ended = false;
        }
        Apply(source, lifecycle.Event, lifecycle: true);
        SetLatest(source, lifecycle.Event, lifecycle.Sequence);
        return true;
    }

    private void Trace(Source source, CopilotHookEvent hook, CopilotSessionStatus before, string reason)
    {
        // No prompt, tool arguments, cwd, transcript text or arbitrary event name.
        var eventName = hook.Name is "sessionStart" or "sessionEnd" or "agentStop" or "subagentStart" or
            "subagentStop" or "userPromptSubmitted" or "preToolUse" or "postToolUse" or
            "postToolUseFailure" or "permissionRequest" ? hook.Name : "other";
        var transition = new CopilotTransition(source.Identity.SourceId, source.Identity.OwnerId,
            eventName, hook.At, before, source.Status, reason);
        if (_transitions.Count == TransitionLimit)
            _transitions.Dequeue();
        _transitions.Enqueue(transition);
        _diagnostic?.Invoke(transition);
    }

    private static void UpdateCandidate(CopilotSession session, CopilotSessionStatus candidate, DateTimeOffset now)
    {
        if (candidate == session.Status)
            session.PendingStatus = null;
        else if (session.PendingStatus != candidate)
        {
            session.PendingStatus = candidate;
            session.PendingStatusSince = now;
        }
    }

    public bool CommitStableStatuses()
    {
        var now = _clock();
        bool changed = false;
        foreach (var session in _sessions.Values)
        {
            if (session.PendingStatus is not { } pending || now - session.PendingStatusSince < Debounce)
                continue;
            session.PendingStatus = null;
            session.Status = pending;
            changed = true;
        }
        return changed;
    }

    public void Clear()
    {
        var ended = _sessions.Values.ToArray();
        _sessions.Clear();
        _sources.Clear();
        _transitions.Clear();
        _sequence = 0;
        _lifecycleVersion = 0;
        _appEvidence.Clear();
        _exitedApps.Clear();
        foreach (var session in ended)
            SessionEnded?.Invoke(session);
    }
}
