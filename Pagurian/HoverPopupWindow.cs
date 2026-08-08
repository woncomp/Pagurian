using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Fluent-styled popup shown when hovering the taskbar icon:
// the icon, a "Hello World" line and a button that shows a message box.
class HoverPopupWindow : Component
{
    // Design size in DIPs; Reactor window APIs (WindowSpec.Width/Height,
    // ManualPosition, SetPosition/SetSize) take DIPs and scale them to physical
    // pixels by the window DPI themselves.
    public const double WindowWidthDip = 240;
    public const double WindowHeightDip = 108;

    public static WindowSpec CreateSpec((double X, double Y) positionDip) => new()
    {
        Title = "Pagurian Popup",
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
        Key = WindowKey.Of("pagurian-popup"),
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    public override Element Render() =>
        FlexColumn(
            FlexRow(
                Image(AppAssets.GitHubIconPath)
                    .Width(40)
                    .Height(40)
                    .AccessibilityHidden(),
                TextBlock("Hello World")
                    .FontSize(16)
                    .Margin(12, 12, 0, 0)),
            Button("Say Hello Again",
                    () => TaskbarInterop.ShowMessage("Hello World Again!", "Pagurian"))
                .Margin(0, 12, 0, 0))
            .Padding(14);
}
