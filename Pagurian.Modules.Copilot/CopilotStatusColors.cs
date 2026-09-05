using Microsoft.UI.Xaml.Media;

namespace Pagurian.Modules.Copilot;

// Session status text colors, theme-aware so they stay readable on the
// taskbar: Idle = TextFillColorSecondary-ish, Working/Blocked = Fluent
// green/orange. Brushes are shared live instances per session (stable across
// re-renders, mutated in place), refreshed on every render so status and
// theme changes are picked up.
static class CopilotStatusColors
{
    private static readonly Dictionary<string, SolidColorBrush> _brushes = new();

    public static SolidColorBrush StatusBrushFor(CopilotSession session, bool isDark)
    {
        var color = StatusColorFor(session.Status, isDark);
        if (!_brushes.TryGetValue(session.SessionId, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[session.SessionId] = brush;
        }
        else if (brush.Color != color)
        {
            brush.Color = color; // status or theme changed since the last render
        }
        return brush;
    }

    // Drops a session's cached brush (sessionEnd removes the session, so
    // without this the cache would grow forever).
    public static void Drop(string sessionId) => _brushes.Remove(sessionId);

    public static Windows.UI.Color StatusColorFor(CopilotSessionStatus status, bool isDark) =>
        status switch
        {
            CopilotSessionStatus.Working =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
                    : Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10),
            CopilotSessionStatus.Blocked =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0xF7, 0x63, 0x0C)
                    : Windows.UI.Color.FromArgb(255, 0xCA, 0x50, 0x10),
            _ =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0xC8, 0xC8, 0xC8)
                    : Windows.UI.Color.FromArgb(255, 0x5C, 0x5C, 0x5C),
        };
}
