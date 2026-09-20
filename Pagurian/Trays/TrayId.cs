namespace Pagurian;

// Which edge of a display's taskbar a tray anchors to. Right-edge trays are a
// planned extension: the identity and config model carry the edge from day
// one, but the binding engine currently normalizes every tray to the left
// surface (see TrayManager) until right-edge placement is designed (it must
// avoid the notification area/clock).
internal enum TrayEdge { Left, Right }

// Identity of a logical tray: the display it belongs to plus the taskbar
// edge. MonitorKey is either the "primary" alias (follows whichever display
// is the primary one) or a display's durable EDID identity key
// (DisplayInfo.IdentityKey, e.g. "DEL40A6-UID4354") — never a GDI ordinal.
internal readonly record struct TrayId(string MonitorKey, TrayEdge Edge)
{
    internal const string PrimaryMonitorKey = "primary";
    internal static readonly TrayId PrimaryLeft = new(PrimaryMonitorKey, TrayEdge.Left);

    internal bool IsPrimaryAlias => MonitorKey == PrimaryMonitorKey;

    public override string ToString() => $"{MonitorKey}/{EdgeName(Edge)}";

    internal static string EdgeName(TrayEdge edge) => edge == TrayEdge.Right ? "right" : "left";

    // Unrecognized values fall back to Left so a config written by a newer
    // host (or a typo) degrades gracefully instead of dropping shells.
    internal static TrayEdge ParseEdge(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "left", StringComparison.OrdinalIgnoreCase))
            return TrayEdge.Left;
        if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase))
            return TrayEdge.Right;
        PagurianLog.Host($"config: unknown tray edge \"{value}\" in {context}; using left");
        return TrayEdge.Left;
    }
}

// Identity of a physical presentation surface: one live display's taskbar at
// one edge. DisplayKey is the display's EDID identity key (never the
// "primary" alias — alias resolution happens in the binding engine).
internal readonly record struct SurfaceKey(string DisplayKey, TrayEdge Edge)
{
    public override string ToString() => $"{DisplayKey}/{TrayId.EdgeName(Edge)}";
}
