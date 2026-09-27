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
        var key = Key(session);
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[key] = brush;
        }
        else if (brush.Color != color)
        {
            brush.Color = color; // status or theme changed since the last render
        }
        return brush;
    }

    // Drops a session's cached brush (sessionEnd removes the session, so
    // without this the cache would grow forever).
    public static void Drop(CopilotSession session) => _brushes.Remove(Key(session));

    public static void Drop(string sessionId) =>
        _brushes.Remove(sessionId);

    private static string Key(CopilotSession session) =>
        (session.IsSdk ? "sdk:" : "hook:") + session.SessionId;

    // Single, unified palette for both tray cells and billboards. Token
    // values (SystemSuccess/SystemCaution) are inlined so dark/light pairs
    // match exactly on the sampled taskbar; Cyan has no SystemFillColor
    // counterpart and is hand-picked.
    public static Windows.UI.Color StatusColorFor(CopilotSessionStatus status, bool isDark) =>
        status switch
        {
            CopilotSessionStatus.Working =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0x4C, 0xC1, 0xD4)
                    : Windows.UI.Color.FromArgb(255, 0x03, 0x83, 0x87),
            CopilotSessionStatus.Blocked =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0xFC, 0xE1, 0x00)
                    : Windows.UI.Color.FromArgb(255, 0x9D, 0x5D, 0x00),
            CopilotSessionStatus.Done =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
                    : Windows.UI.Color.FromArgb(255, 0x0F, 0x7B, 0x0F),
            _ =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A)
                    : Windows.UI.Color.FromArgb(255, 0x86, 0x86, 0x86),
        };
}
