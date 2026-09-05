using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;

namespace Pagurian.Modules.Copilot;

// Lifecycle status of a Copilot CLI session, derived from hook events (the
// same mapping as the CopilotHookMonitor reference project):
//  - userPromptSubmitted / preToolUse / postToolUse / postToolUseFailure
//    -> Working (a turn is in flight)
//  - permissionRequest -> Blocked (waiting on the human)
//  - agentStop -> Idle (turn finished, waiting for the next prompt)
//  - sessionStart -> Idle; sessionEnd REMOVES the session instead of
//    changing its status — a cell exists only while its session is alive
//    (sessionEnd is always a session's last event, so nothing is lost).
//  - all other events leave the status unchanged.
enum CopilotSessionStatus
{
    Idle,
    Working,
    Blocked,
}

// One tracked Copilot CLI session. Mutated on the UI thread only (hook
// messages are marshaled via UIDispatcher.TryEnqueue before touching state).
sealed class CopilotSession
{
    public required string SessionId { get; init; }
    public string Name { get; set; } = "";
    public bool NameResolved { get; set; } // false while Name is the fallback session id
    public CopilotSessionStatus Status { get; set; } = CopilotSessionStatus.Idle;
    public CopilotSessionStatus? PendingStatus { get; set; }
    public DateTimeOffset PendingStatusSince { get; set; }
    public string LastEventName { get; set; } = "";
    public string LastEventDump { get; set; } = ""; // pretty-printed envelope JSON
}

// Keeps the per-session state that backs the session cells, tooltips, and
// billboards, fed by CopilotShell.OnMessage (hook events posted by the CLI).
// Sessions are discovered from ANY event carrying an unknown sessionId and
// removed the moment their sessionEnd event arrives — a cell exists only
// while its session is alive. The shell subscribes to SessionStarted/
// SessionEnded to attach/detach the matching cell.
static class CopilotSessionTracker
{
    private static readonly Dictionary<string, CopilotSession> _sessions = new();

    private static DispatcherQueueTimer? _statusDebounceTimer;

    private static readonly TimeSpan StatusDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatusDebounceTimerInterval = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Bumped on every state change. Cells and billboards subscribe to
    // UiChanged and re-render with the new Version (the Tea refresh-counter
    // pattern); raised on the UI thread.
    public static int Version { get; private set; }
    public static event Action? UiChanged;

    // Raised on the UI thread when a session appears (any event with an
    // unknown sessionId) / ends (sessionEnd).
    public static event Action<CopilotSession>? SessionStarted;
    public static event Action<CopilotSession>? SessionEnded;

    public static CopilotSession? Find(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public static void Start()
    {
        _statusDebounceTimer = ReactorApp.UIDispatcher!.CreateTimer();
        _statusDebounceTimer.Interval = StatusDebounceTimerInterval;
        _statusDebounceTimer.IsRepeating = true;
        _statusDebounceTimer.Tick += (_, _) => CommitStableStatuses();
        _statusDebounceTimer.Start();
    }

    public static void Stop()
    {
        _statusDebounceTimer?.Stop();
        _statusDebounceTimer = null;
    }

    // UI thread. One hook event: its name and the raw payload JSON the CLI
    // piped to the posting process (null when the event carried no payload).
    public static void HandleHookEvent(string eventName, string? payloadJson)
    {
        JsonNode? payload = null;
        if (payloadJson != null)
        {
            try { payload = JsonNode.Parse(payloadJson); }
            catch { /* unparseable payloads still drive the state machine */ }
        }

        var sessionId = payload?["sessionId"]?.GetValue<string>() ?? "";
        if (sessionId.Length == 0)
            return; // events without a session can't get a cell

        // The cell lives only while the session does: sessionEnd drops it
        // (and an end event for a session we never saw creates nothing).
        if (eventName == "sessionEnd")
        {
            if (_sessions.Remove(sessionId, out var ended))
            {
                SessionEnded?.Invoke(ended);
                NotifyChanged();
            }
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            session = new CopilotSession { SessionId = sessionId, Name = sessionId };
            _sessions[sessionId] = session;
            SessionStarted?.Invoke(session);
        }

        session.LastEventName = eventName;
        session.LastEventDump = PrettyPrint(eventName, payload);
        var mappedStatus = eventName switch
        {
            "sessionStart" => (CopilotSessionStatus?)CopilotSessionStatus.Idle,
            "userPromptSubmitted" => CopilotSessionStatus.Working,
            "preToolUse" or "postToolUse" or "postToolUseFailure" => CopilotSessionStatus.Working,
            "permissionRequest" => CopilotSessionStatus.Blocked,
            "agentStop" => CopilotSessionStatus.Idle,
            // sessionEnd is handled above (removal). userPromptTransformed,
            // subagentStart/Stop, errorOccurred, preCompact, notification:
            // recorded for the dump only.
            _ => null,
        };

        if (mappedStatus.HasValue)
            UpdateStatusCandidate(session, mappedStatus.Value, DateTimeOffset.UtcNow);

        if (!session.NameResolved || eventName is "sessionStart" or "userPromptTransformed")
            TryResolveName(session);

        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        Version++;
        UiChanged?.Invoke();
    }

    // Hook events arrive on the UI thread, so candidate transitions can be
    // tracked without locks. Repeated events for the same candidate leave its
    // original timestamp intact; only a different candidate restarts the
    // debounce window.
    private static void UpdateStatusCandidate(
        CopilotSession session,
        CopilotSessionStatus candidate,
        DateTimeOffset now)
    {
        if (candidate == session.Status)
        {
            session.PendingStatus = null;
            return;
        }

        if (session.PendingStatus != candidate)
        {
            session.PendingStatus = candidate;
            session.PendingStatusSince = now;
        }
    }

    // Runs on the UI dispatcher and commits only candidates that have stayed
    // unchanged for the full debounce interval. A single notification covers
    // all statuses committed by this tick.
    private static void CommitStableStatuses()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var session in _sessions.Values)
        {
            if (!session.PendingStatus.HasValue)
                continue;

            if (now - session.PendingStatusSince < StatusDebounce)
                continue;

            var pending = session.PendingStatus.Value;
            session.PendingStatus = null;
            if (pending == session.Status)
                continue;

            session.Status = pending;
            changed = true;
        }

        if (changed)
            NotifyChanged();
    }

    // The billboard's last-event dump keeps the old envelope shape:
    // {"loggedAt":...,"event":...,"payload":{...}}.
    private static string PrettyPrint(string eventName, JsonNode? payload)
    {
        try
        {
            var envelope = new JsonObject
            {
                ["loggedAt"] = DateTime.Now.ToString("o"),
                ["event"] = eventName,
                ["payload"] = payload?.DeepClone(),
            };
            return envelope.ToJsonString(IndentedJson);
        }
        catch
        {
            return $"{{\"event\":\"{eventName}\"}}";
        }
    }

    // Session display name = the "name" field of
    // %USERPROFILE%\.copilot\session-state\{id}\workspace.yaml (plain line
    // parse — no YAML dependency). Falls back to the session id when the file
    // or field is missing, and is retried on every event until resolved
    // because the file can lag slightly behind sessionStart.
    private static void TryResolveName(CopilotSession session)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "session-state", session.SessionId, "workspace.yaml");
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("name:", StringComparison.Ordinal))
                    continue;
                var value = line["name:".Length..].Trim().Trim('"', '\'');
                if (value.Length > 0 && !value.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    session.Name = value;
                    session.NameResolved = true;
                }
                return;
            }
        }
        catch
        {
            // name resolution is best-effort
        }
    }
}
