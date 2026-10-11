using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

internal static class UsageHistoryPalette
{
    internal static Color ColorFor(double percent, bool dark)
    {
        var green = UsageTint.ColorFor(6, dark);
        var red = UsageTint.ColorFor(-6, dark);
        if (percent <= 5)
            return green;
        var weight = Math.Clamp((percent - 5) / 5, 0, 1);
        byte Blend(byte start, byte end) =>
            (byte)Math.Round(start + (end - start) * weight);
        return Color.FromArgb(255, Blend(green.R, red.R),
            Blend(green.G, red.G), Blend(green.B, red.B));
    }

    internal static Color Estimate(bool dark) => dark
        ? Color.FromArgb(255, 245, 190, 68)
        : Color.FromArgb(255, 176, 116, 0);

    internal static Color NoSample(bool dark) => dark
        ? Color.FromArgb(255, 130, 130, 130)
        : Color.FromArgb(255, 140, 140, 140);
}

internal static class UsageHistoryCard
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] Celestial = ["☀️", "🌙", "♂️", "☿️", "♃", "♀️", "♄"];
    private static readonly string[] CelestialNames =
        ["Sun", "Moon", "Mars", "Mercury", "Jupiter", "Venus", "Saturn"];
    private const double PlotHeight = 96;

    internal static Element Render(
        CopilotUsageHistoryProjection history, bool dark, bool highContrast,
        int page, Action<int> setPage)
    {
        var columns = new List<Element>();
        for (var column = 0; column < CopilotUsageHistoryCalculator.DaysPerPage; column++)
        {
            var columnIndex = column;
            var day = history.Days[page * CopilotUsageHistoryCalculator.DaysPerPage + column];
            var weekday = (int)day.Date.DayOfWeek;
            var tooltip = Describe(day, history.Today);
            columns.Add(Border((Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                    VStack(3,
                        Plot(day, dark, highContrast),
                        Caption(Celestial[weekday])
                            .TextAlignment(TextAlignment.Center)
                            .AutomationName($"{day.Date:dddd}, {CelestialNames[weekday]}")),
                    new UsageHistoryDateMarkerElement()
                        .Set(marker => marker.Update(
                            day.Date.Day is 1 or 11 or 21
                                ? day.Date.ToString("MMM d", English) : "",
                            columnIndex, dark, highContrast))
                        .Grid(row: 1),
                    Border(null)) with { RowSpacing = 3 })
                    .Set(content => content.IsHitTestVisible = false))
                    .Set(owner => UsageHistoryTooltip.Attach(owner, tooltip, dark, highContrast))
#pragma warning disable REACTOR_THEME_004 // Transparent full-height date hover target.
                    .Background(new SolidColorBrush(Microsoft.UI.Colors.Transparent))
#pragma warning restore REACTOR_THEME_004
                    .AutomationId($"UsageHistory-{day.Date:yyyy-MM-dd}")
                    .AutomationName(tooltip.Replace('\n', ' '))
                    .HelpText(tooltip)
                    .OnUnmount(UsageHistoryTooltip.Detach)
                .Grid(column: column)
                .WithKey(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        var chart = Grid(
            Enumerable.Repeat(GridSize.Star(), CopilotUsageHistoryCalculator.DaysPerPage).ToArray(),
            [GridSize.Auto], columns.ToArray());

        return VStack(8,
            BodyStrong("History").HeadingLevel(AutomationHeadingLevel.Level2),
            history.Error is null ? null
                : Body(history.Error)
                    .Foreground(ReactorTheme.SystemCritical)
                    .TextWrapping(TextWrapping.WrapWholeWords),
            history.Error is null && history.Days.All(day => day.Used is null)
                ? Caption("No usage history yet. Samples are recorded when Credits refresh.")
                    .TextWrapping(TextWrapping.WrapWholeWords)
                : null,
            ScrollViewer(chart)
                .AutomationId("UsageHistoryChartScroller")
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Set(scroll =>
                {
                    scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                }),
            PipsPager(CopilotUsageHistoryCalculator.PageCount, page, setPage)
                .MaxVisiblePips(CopilotUsageHistoryCalculator.PageCount)
                .PreviousButtonVisibility(PipsPagerButtonVisibility.Visible)
                .NextButtonVisibility(PipsPagerButtonVisibility.Visible)
                .AutomationId("UsageHistoryPager")
                .HorizontalAlignment(HorizontalAlignment.Center));
    }

    private static Element Plot(
        CopilotUsageHistoryCell day, bool dark, bool highContrast)
    {
        var percentage = Math.Clamp(day.PercentOfEntitlement ?? 0, 0, 10);
        var height = day.Used is null ? 2 : Math.Max(3, PlotHeight * percentage / 10);
        var bar = Border(Caption(""))
            .Width(12)
            .Height(height)
            .CornerRadius(2)
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Bottom);
        if (day.Used is null)
        {
            if (highContrast)
                bar = bar.Background(UsageTint.SystemBrush("SystemColorWindowTextColorBrush"));
            else
#pragma warning disable REACTOR_THEME_004 // Recomputed when the Usage theme changes.
                bar = bar.Background(new SolidColorBrush(UsageHistoryPalette.NoSample(dark)));
#pragma warning restore REACTOR_THEME_004
        }
        else if (highContrast)
            bar = bar.Background(UsageTint.SystemBrush("SystemColorWindowTextColorBrush"));
        else
#pragma warning disable REACTOR_THEME_004 // Recomputed when the Usage theme changes.
            bar = bar.Background(new SolidColorBrush(day.Estimated
                ? UsageHistoryPalette.Estimate(dark)
                : UsageHistoryPalette.ColorFor(percentage, dark)));
#pragma warning restore REACTOR_THEME_004

        Element? question = null;
        if (day.Estimated)
        {
#pragma warning disable REACTOR_THEME_004 // The estimate badge follows the current Usage theme.
            var badge = Border(Caption("?")
                    .TextAlignment(TextAlignment.Center)
                    .Foreground(highContrast
                        ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
                        : new SolidColorBrush(Microsoft.UI.Colors.Black)))
                .Width(12)
                .MinHeight(16)
                .CornerRadius(2)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .VerticalAlignment(VerticalAlignment.Top);
            question = badge.Background(highContrast
                ? UsageTint.SystemBrush("SystemColorWindowTextColorBrush")
                : new SolidColorBrush(UsageHistoryPalette.Estimate(dark)));
#pragma warning restore REACTOR_THEME_004
        }
        return Border(Grid([GridSize.Star()], [GridSize.Star()],
                bar, question))
            .Height(PlotHeight);
    }

    internal static string Describe(CopilotUsageHistoryCell day, DateOnly today)
    {
        var date = day.Date.ToString("MMMM d, yyyy", English);
        var weekday = day.Date.ToString("dddd", English);
        var usage = day.Date > today ? "Not sampled yet"
            : day.Used is not { } used ? "No usage data"
            : $"{(day.Estimated ? "Estimated " : "")}" +
                used.ToString(day.Estimated ? "N1" : "N0", CultureInfo.CurrentCulture) +
                $" Credits used{(day.Estimated ? " (?)" : "")}";
        var start = day.StartUsed is { } total
            ? total.ToString(day.StartEstimated ? "N1" : "N0", CultureInfo.CurrentCulture) +
                $" Credits{(day.StartEstimated ? " (estimated)" : "")}"
            : "Unavailable";
        return $"{date}\n{weekday}\n{usage}\nDay start: {start}";
    }
}
