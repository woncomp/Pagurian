namespace Pagurian;

// One physically present display: GDI enumeration geometry joined with the
// active DisplayConfig path's EDID identity. IdentityKey is the durable
// hardware identity persisted in config.json; DeviceName (\\.\DISPLAYn) is a
// per-boot GDI ordinal used only to correlate live objects — never persisted.
internal sealed record DisplayInfo(
    string IdentityKey,
    string FriendlyName,
    string DeviceName,
    nint Handle,
    TaskbarInterop.RECT Rect,
    TaskbarInterop.RECT WorkRect,
    bool IsPrimary,
    nint TaskbarHwnd)
{
    // The Windows "show taskbar on all displays" toggle and auto-hide states
    // control whether a taskbar window exists on this display right now.
    internal bool HasTaskbar => TaskbarHwnd != 0;
}

// Last-seen geometry/name of a display that may currently be disconnected.
// Persisted in monitors.json so the binding engine can still reason about
// absent displays (the aspect-ratio step of the fallback chain needs the
// missing display's shape, which only history can provide).
internal sealed record RecordedMonitor(
    string Key,
    string Name,
    int Width,
    int Height,
    DateTimeOffset LastSeen);
