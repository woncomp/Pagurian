using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;

namespace Pagurian;

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

// One tracked Copilot CLI session. Mutated on the UI thread only (pipe
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

// Receives Copilot hook events from the short-lived bridge processes
// (CopilotHookBridge) over a named pipe and keeps the per-session state that
// backs the taskbar widget cells, tooltips, and session popups. Sessions are
// discovered from ANY event carrying an unknown sessionId, kept in first-seen
// order (that is the left-to-right cell order), and removed the moment their
// sessionEnd event arrives — a cell exists only while its session is alive.
static class CopilotSessionTracker
{
    private static readonly Dictionary<string, CopilotSession> _sessions = new();
    private static readonly List<string> _order = new(); // first-seen order = cell order
    private static IReadOnlyList<CopilotSession>? _snapshot;

    private static CancellationTokenSource? _cts;
    private static DispatcherQueueTimer? _statusDebounceTimer;

    private static readonly TimeSpan StatusDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatusDebounceTimerInterval = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Bumped on every state change. Windows subscribe to UiChanged and
    // re-render with the new Version (the Tea refresh-counter pattern); both
    // are raised on the UI thread.
    public static int Version { get; private set; }
    public static event Action? UiChanged;

    public static IReadOnlyList<CopilotSession> Sessions =>
        _snapshot ??= _order.Select(id => _sessions[id]).ToList();

    public static CopilotSession? Find(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public static void Start()
    {
        _cts = new CancellationTokenSource();
        _statusDebounceTimer = ReactorApp.UIDispatcher!.CreateTimer();
        _statusDebounceTimer.Interval = StatusDebounceTimerInterval;
        _statusDebounceTimer.IsRepeating = true;
        _statusDebounceTimer.Tick += (_, _) => CommitStableStatuses();
        _statusDebounceTimer.Start();

        var ct = _cts.Token;
        Task.Run(() => PipeServerLoop(ct), ct);
    }

    public static void Stop()
    {
        _statusDebounceTimer?.Stop();
        _statusDebounceTimer = null;
        _cts?.Cancel();
    }

    // Lets non-tracker code (the theme flip in TaskbarController) force the
    // subscribed windows to re-render.
    public static void NotifyChanged()
    {
        _snapshot = null;
        Version++;
        UiChanged?.Invoke();
    }

    // Background thread. Accepts bridge connections and processes one
    // single-line JSON message per connection.
    private static async Task PipeServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    CopilotHookBridge.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    using var msg = JsonDocument.Parse(line);
                    var root = msg.RootElement.Clone();
                    ReactorApp.UIDispatcher?.TryEnqueue(() => HandleHookMessage(root));
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // UI thread. One envelope: {"loggedAt":...,"event":...,"payload":{...}}.
    private static void HandleHookMessage(JsonElement root)
    {
        var eventName = root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String
            ? ev.GetString() ?? "unknown"
            : "unknown";
        var hasPayload = root.TryGetProperty("payload", out var payload) &&
                         payload.ValueKind == JsonValueKind.Object;
        var sessionId = hasPayload &&
                        payload.TryGetProperty("sessionId", out var sid) &&
                        sid.ValueKind == JsonValueKind.String
            ? sid.GetString() ?? ""
            : "";
        if (sessionId.Length == 0)
            return; // events without a session can't get a cell

        // The cell lives only while the session does: sessionEnd drops it
        // (and an end event for a session we never saw creates nothing).
        if (eventName == "sessionEnd")
        {
            if (_sessions.Remove(sessionId))
            {
                _order.Remove(sessionId);
                NotifyChanged();
            }
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            session = new CopilotSession { SessionId = sessionId, Name = sessionId };
            _sessions[sessionId] = session;
            _order.Add(sessionId);
        }

        session.LastEventName = eventName;
        session.LastEventDump = PrettyPrint(root);
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

    private static string PrettyPrint(JsonElement root)
    {
        try
        {
            return JsonSerializer.Serialize(root, IndentedJson);
        }
        catch
        {
            return root.GetRawText();
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
