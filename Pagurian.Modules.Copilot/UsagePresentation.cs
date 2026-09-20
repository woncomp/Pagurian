using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// One ramp for all Usage surfaces. The integer is also the displayed balance;
// values outside six days retain their meaning and saturate only the color.
internal static class UsageTint
{
    internal static Color ColorFor(int? roundedBalance, bool dark)
    {
        var neutral = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        if (roundedBalance is null) return neutral;
        if (roundedBalance is 0) return Color.FromArgb(255, 52, 120, 184);
        var signal = roundedBalance > 0
            ? dark ? Color.FromArgb(255, 108, 203, 95) : Color.FromArgb(255, 16, 124, 16)
            : dark ? Color.FromArgb(255, 255, 153, 164) : Color.FromArgb(255, 196, 43, 28);
        var weight = Math.Min(Math.Abs((double)roundedBalance.Value) / 6, 1);
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * weight);
        return Color.FromArgb(255, Blend(neutral.R, signal.R),
            Blend(neutral.G, signal.G), Blend(neutral.B, signal.B));
    }

    internal static Brush BrushFor(int? balance, bool dark, bool highContrast) =>
        highContrast ? SystemBrush("SystemColorWindowTextColorBrush")
            : new SolidColorBrush(ColorFor(balance, dark));

    internal static Brush SystemBrush(string key) =>
        (Brush)Application.Current.Resources[key];
}

internal static class UsageBalanceLabel
{
    internal static Element Render(CopilotUsagePace pace, bool dark, bool highContrast)
    {
        var tint = UsageTint.BrushFor(pace.RoundedBalance, dark, highContrast);
        return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            new UsagePulseElement().Set(pulse =>
                {
                    pulse.Compact = true;
                    pulse.Tint = tint;
                })
                .AccessibilityHidden().VerticalAlignment(VerticalAlignment.Top).Grid(column: 0),
            BodyStrong(pace.BalanceText).Foreground(tint)
                .TextWrapping(TextWrapping.WrapWholeWords).Grid(column: 1))
            with { ColumnSpacing = 8 };
    }
}
