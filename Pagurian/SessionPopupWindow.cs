using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Popup for one Copilot session, opened by clicking its taskbar cell: the
// session name, its colored status, and the last received hook event dumped
// as pretty-printed JSON. Re-renders on every tracker change, so the status
// and dump stay live while the popup is open.
class SessionPopupWindow : Component
{
    // Design size in DIPs; Reactor window APIs scale to physical pixels by
    // the window DPI themselves.
    public const double WindowWidthDip = 360;
    public const double WindowHeightDip = 260;

    // Fixed dump area height: window minus padding, name row, status row and
    // margins (see Render).
    private const double DumpHeightDip = 164;

    private static int _instanceCount; // unique WindowKey per opened popup

    private readonly string _sessionId;
    private readonly string _key;

    public SessionPopupWindow(string sessionId)
    {
        _sessionId = sessionId;
        _key = $"pagurian-session-{sessionId}-{++_instanceCount}";
    }

    public WindowSpec CreateSpec((double X, double Y) positionDip) => new()
    {
        Title = "Copilot Session",
        Width = WindowWidthDip,
        Height = WindowHeightDip,
        Style = WindowStyle.None,
        CornerStyle = WindowCornerStyle.Rounded,
        Backdrop = BackdropChoice.Of(BackdropKind.AcrylicThin),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        IsMinimizable = false,
        IsMaximizable = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = positionDip,
        Key = WindowKey.Of(_key),
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    public override Element Render()
    {
        var (_, setTrackerVersion) = UseState(0);

        // Live updates: re-render whenever any session changes state.
        UseEffect(() =>
        {
            void OnChanged() => setTrackerVersion(CopilotSessionTracker.Version);
            CopilotSessionTracker.UiChanged += OnChanged;
            return () => CopilotSessionTracker.UiChanged -= OnChanged;
        }, Array.Empty<object>());

        var session = CopilotSessionTracker.Find(_sessionId);
        if (session == null)
            // The session ended and the tracker dropped it; the controller
            // closes this popup on its next tick.
            return TextBlock("Session ended.").Padding(14);

        // Shared per-session live brush: the popup and the taskbar cell show
        // the same status color, refreshed in place on every render.
        var statusBrush = TaskbarIconWindow.StatusBrushFor(session, TaskbarController.IsDarkTheme);

        return FlexColumn(
                FlexRow(
                    Image(AppAssets.GitHubIconPath)
                        .Width(24)
                        .Height(24)
                        .AccessibilityHidden()
                        .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center),
                    TextBlock(session.Name)
                        .FontSize(14)
                        .SemiBold()
                        .MaxLines(1)
                        .TextTrimming(Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis)
                        .Margin(10, 0, 0, 0)
                        .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center)),
                FlexRow(
                    TextBlock("Status:")
                        .FontSize(12),
                    TextBlock(session.Status.ToString())
                        .FontSize(12)
                        .SemiBold()
                        .Foreground(statusBrush)
                        .Margin(6, 0, 0, 0))
                    .Margin(0, 8, 0, 0),
                ScrollViewer(
                    TextBlock(session.LastEventDump)
                        .FontFamily("Consolas")
                        .FontSize(11)
                        .TextWrapping(Microsoft.UI.Xaml.TextWrapping.Wrap))
                    .Height(DumpHeightDip)
                    .Margin(0, 10, 0, 0))
            .Padding(14);
    }
}
