using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// Compact taskbar summary for the premium interactions quota.
class UsageCell : ShellCell
{
    private const double FontSizeDip = 12;
    private const double PaddingXDip = 6;

    public override Element Render()
    {
        var service = ModelAs<CopilotUsageService>();
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            service.Changed += OnChanged;
            return () => service.Changed -= OnChanged;
        }, service);

        var state = service.State;
        var category = state.Usage.PremiumInteractions;
        var percent = PercentFor(category, state);
        var isDark = Theme.IsDark;
        return Border(
            FlexColumn(
                Progress(percent ?? 0)
                    .Height(3)
                    .Margin(0, 0, 0, 2)
                    .Set(pb =>
                    {
                        pb.Foreground = new SolidColorBrush(BarColor(isDark));
                        pb.Background = new SolidColorBrush(BarTrackColor(isDark));
                    }),
                Image(CopilotModule.UsageIconPath)
                    .Width(16)
                    .Height(16)
                    .AccessibilityHidden(),
                TextBlock(PercentageFor(category, state))
                    .FontSize(FontSizeDip)
                    .TextAlignment(TextAlignment.Center)
                    .Foreground(Theme.TextBrush)))
            .Padding(PaddingXDip, 0, PaddingXDip, 0);
    }

    private static double? PercentFor(
        CopilotUsageQuota? category,
        CopilotUsageState state) =>
        state.Status == CopilotUsageStatus.Ready &&
        !state.IsLoggingIn &&
        category is { IsUnlimited: false, UsedPercentage: { } percentage }
            ? Math.Clamp(percentage, 0, 100)
            : null;

    private static string PercentageFor(
        CopilotUsageQuota? category,
        CopilotUsageState state) =>
        PercentFor(category, state) is not null
            ? category!.PercentageText
            : "--";

    private static Windows.UI.Color BarColor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
            : Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10);

    private static Windows.UI.Color BarTrackColor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 0x48, 0x48, 0x48)
            : Windows.UI.Color.FromArgb(255, 0xD6, 0xD6, 0xD6);
}
