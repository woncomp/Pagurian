using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Tiny tooltip window showing a Copilot session's name while hovering its
// taskbar cell. Implemented as a real window (not a XAML ToolTip) because the
// taskbar cells live in a NoActivate window injected into the taskbar, where
// pointer events — and thus XAML tooltips — are unreliable; the controller
// shows/hides this from its cursor-polling loop instead.
class TooltipWindow : Component
{
    public const double WindowHeightDip = 26;

    // Rough width estimate from the text (no text measurement available):
    // ~7 DIPs per Latin char, 12 per wide (CJK) char, plus padding.
    public static double WidthFor(string text)
    {
        double units = 0;
        foreach (var c in text)
            units += c > 0x2E7F ? 12 : 7;
        return Math.Clamp(units + 20, 48, 320);
    }

    private static int _instanceCount; // unique WindowKey per shown tooltip

    private readonly string _text;
    private readonly string _key;

    public TooltipWindow(string text)
    {
        _text = text;
        _key = $"pagurian-tooltip-{++_instanceCount}";
    }

    public WindowSpec CreateSpec((double X, double Y) positionDip) => new()
    {
        Title = "Pagurian Tooltip",
        Width = WidthFor(_text),
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

    public override Element Render() =>
        Border(
            TextBlock(_text)
                .FontSize(12)
                .MaxLines(1)
                .TextTrimming(Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis)
                .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center))
            .Padding(8, 0, 8, 0);
}
