using System.Text.Json;

namespace Pagurian;

// The live display topology: which monitors are connected, their durable
// EDID identities, geometry, and whether a taskbar window exists on each.
// Refreshed on the controller's throttled cadence; structural changes raise
// Changed (a taskbar HWND alone being recreated by an Explorer restart is
// NOT structural — surfaces re-resolve HWNDs lazily through TaskbarHwndFor).
//
// Also owns monitors.json: the last-seen name/geometry per display identity,
// so the binding engine can reason about displays that are currently
// disconnected (fallback aspect-ratio matching, the settings picker's
// "disconnected" rows). config.json stays purely user-authored.
static class DisplayTopology
{
    private const int HistoryLimit = 64;

    private static readonly Dictionary<string, RecordedMonitor> _history = new();
    private static bool _historyLoaded;
    private static bool _historyDirty;

    public static IReadOnlyList<DisplayInfo> Displays { get; private set; } = [];

    // Raised on the caller's thread (the controller's UI-thread timer) when
    // the structural topology changed: identity set, geometry, primary flag,
    // or taskbar presence.
    public static event Action? Changed;

    private static string HistoryPath => Path.Combine(HostSettings.ConfigDir, "monitors.json");

    // Re-enumerates displays and taskbars. Returns true on structural change.
    public static bool Refresh()
    {
        IReadOnlyList<DisplayInfo> next;
        try
        {
            next = DisplayInterop.EnumerateDisplays();
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("displays: enumeration failed; keeping last topology", ex);
            return false;
        }

        var changed = StructuralDiff(Displays, next);
        Displays = next;
        if (!changed)
            return false;

        PagurianLog.Host(
            "displays: " + (next.Count == 0
                ? "no displays"
                : string.Join(", ", next.Select(d =>
                    $"{d.IdentityKey} \"{d.FriendlyName}\" {d.Rect.Width}x{d.Rect.Height}" +
                    $"{(d.IsPrimary ? " primary" : "")}{(d.HasTaskbar ? "" : " no-taskbar")}"))));
        RecordHistory(next);
        Changed?.Invoke();
        return true;
    }

    public static DisplayInfo? Find(string identityKey) =>
        Displays.FirstOrDefault(d => d.IdentityKey == identityKey);

    // Fresh taskbar HWND for a display; surfaces resolve this lazily because
    // Explorer recreates taskbar windows without a topology change.
    public static nint TaskbarHwndFor(string identityKey) => Find(identityKey)?.TaskbarHwnd ?? 0;

    public static bool TryGetRecorded(string identityKey, out RecordedMonitor recorded)
    {
        EnsureHistoryLoaded();
        return _history.TryGetValue(identityKey, out recorded!);
    }

    public static IReadOnlyList<RecordedMonitor> RecordedMonitors()
    {
        EnsureHistoryLoaded();
        return _history.Values.OrderByDescending(m => m.LastSeen).ToArray();
    }

    private static bool StructuralDiff(IReadOnlyList<DisplayInfo> a, IReadOnlyList<DisplayInfo> b)
    {
        if (a.Count != b.Count)
            return true;
        var key = new Func<DisplayInfo, string>(d =>
            $"{d.IdentityKey}|{d.Rect.Left},{d.Rect.Top},{d.Rect.Width},{d.Rect.Height}|{d.IsPrimary}|{d.HasTaskbar}");
        var before = a.Select(key).ToHashSet(StringComparer.Ordinal);
        return b.Select(key).Any(k => !before.Contains(k));
    }

    private static void RecordHistory(IReadOnlyList<DisplayInfo> displays)
    {
        EnsureHistoryLoaded();
        var now = DateTimeOffset.Now;
        foreach (var display in displays)
        {
            if (_history.TryGetValue(display.IdentityKey, out var existing) &&
                existing.Name == display.FriendlyName &&
                existing.Width == display.Rect.Width &&
                existing.Height == display.Rect.Height)
            {
                continue;
            }
            _history[display.IdentityKey] = new RecordedMonitor(
                display.IdentityKey, display.FriendlyName,
                display.Rect.Width, display.Rect.Height, now);
            _historyDirty = true;
        }
        if (_history.Count > HistoryLimit)
        {
            foreach (var stale in _history.Values.OrderBy(m => m.LastSeen).Take(_history.Count - HistoryLimit).ToArray())
                _history.Remove(stale.Key);
            _historyDirty = true;
        }
        if (_historyDirty)
            SaveHistory();
    }

    private static void EnsureHistoryLoaded()
    {
        if (_historyLoaded)
            return;
        _historyLoaded = true;
        try
        {
            if (!File.Exists(HistoryPath))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath));
            if (!doc.RootElement.TryGetProperty("monitors", out var monitors) ||
                monitors.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in monitors.EnumerateArray())
            {
                var key = item.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrEmpty(key))
                    continue;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
                var width = item.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                var height = item.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                var lastSeen = item.TryGetProperty("lastSeen", out var t) &&
                    DateTimeOffset.TryParse(t.GetString(), out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;
                _history[key] = new RecordedMonitor(key, name, width, height, lastSeen);
            }
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"displays: failed to load {HistoryPath}", ex);
        }
    }

    private static void SaveHistory()
    {
        try
        {
            var doc = new
            {
                monitors = _history.Values
                    .OrderByDescending(m => m.LastSeen)
                    .Select(m => new
                    {
                        key = m.Key,
                        name = m.Name,
                        width = m.Width,
                        height = m.Height,
                        lastSeen = m.LastSeen.ToString("O"),
                    }),
            };
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var tmp = HistoryPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, HistoryPath, overwrite: true);
            _historyDirty = false;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"displays: failed to save {HistoryPath}", ex);
        }
    }
}
