using Microsoft.UI.Reactor;

namespace Pagurian;

// One physical presentation surface: a live display's taskbar at one edge.
// Owns the injected tray window session and the surface's sampled theme, and
// renders whatever cells TrayManager's binding assigns to it (its own tray,
// plus any trays the fallback chain moved here). Purely presentational —
// logical tray state lives in TrayManager, topology in DisplayTopology.
//
// Taskbar HWNDs are re-resolved on every query (Explorer recreates taskbar
// windows without a topology change); a display vanishing makes the surface
// unwanted and the controller closes it.
internal sealed class TraySurface
{
    private readonly TrayWindowSession _session;

    internal TraySurface(SurfaceKey key)
    {
        Key = key;
        _session = new TrayWindowSession(
            getSurface: GetSurface,
            getParent: () => DisplayTopology.TaskbarHwndFor(Key.DisplayKey),
            themeSink: Theme.Apply,
            content: layout => new TaskbarTrayWindow(layout, Key),
            windowKey: WindowKey.Of($"pagurian-tray-{DisplayInterop.SanitizeKey(Key.DisplayKey)}-{TrayId.EdgeName(Key.Edge)}"));
    }

    internal SurfaceKey Key { get; }

    // This surface's sampled taskbar theme. Trays bound here share it.
    internal ThemeService Theme { get; } = new();

    internal TrayWindowSessionState State => _session.State;
    internal nint Hwnd => _session.Hwnd;
    internal ReactorWindow? Window => _session.Window;
    internal TrayLayoutSnapshot? Snapshot => _session.Snapshot;

    internal void Start()
    {
        TrayManager.RegisterSurfaceTheme(Key, Theme);
        _session.Start();
    }

    // False when the session can no longer find its taskbar; the controller
    // closes and forgets the surface (a later reconcile recreates it).
    internal bool CheckEnvironment() => _session.CheckEnvironment();

    internal void Close()
    {
        TrayManager.UnregisterSurfaceTheme(Key);
        _session.Close();
    }

    internal IEnumerable<(string Key, TaskbarInterop.RECT Rect)> CellRects =>
        State == TrayWindowSessionState.Visible && Snapshot is { } snapshot
            ? snapshot.Cells.Select(c => (c.Key, c.BoundsPx))
            : [];

    // Physical bounds of this surface's display (tooltip edge flipping).
    internal TaskbarInterop.RECT MonitorRect =>
        DisplayTopology.Find(Key.DisplayKey) is { } display ? display.Rect : default;

    private TaskbarTrayPlacement.Surface? GetSurface()
    {
        var taskbar = DisplayTopology.TaskbarHwndFor(Key.DisplayKey);
        return taskbar != 0 && TaskbarTrayPlacement.TryGetSurface(taskbar, Key.Edge, out var surface)
            ? surface
            : null;
    }
}
