using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XamlText = Microsoft.UI.Xaml.Controls.TextBlock;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// Module-local, tree-scoped test seam. No global service replacement and no SDK
// start: the production default is resolved only when no fixture is provided.
internal sealed record UsageConfigurationSource(
    Func<JsonElement?, Action<string>, CopilotUsageModel> CreateModel,
    Action Login);

internal static class UsageConfigurationContext
{
    internal static readonly Context<UsageConfigurationSource?> Source = new(null);
}

internal static class UsageCalendarView
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly ConditionalWeakTable<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton, UsageDateContent> Content = new();

    internal static Element Render(CopilotUsagePace pace, Action<DateOnly> toggle, bool dark, bool highContrast)
        => Component<UsageCalendarSurface, UsageCalendarSurfaceProps>(new(pace, toggle, dark, highContrast));

    internal static Element RenderSurface(CopilotUsagePace pace, Action<DateOnly> toggle, bool dark,
        bool highContrast, UsageCalendarWidth layout)
    {
        if (pace.Cycle is not { } cycle)
            return Caption("Workday calendar unavailable until a Credits reset cycle is available.")
                .TextWrapping(TextWrapping.WrapWholeWords);

        var cells = new List<Element>();
        for (int column = 0; column < 7; column++)
            cells.Add(Caption(Weekdays[column]).TextAlignment(TextAlignment.Center)
                .Grid(row: 0, column: column).WithKey(Weekdays[column]));
        for (int index = 0; index < cycle.Days.Count; index++)
        {
            var day = cycle.Days[index];
            var state = day.IsWorkday ? "Workday" : "Not a workday";
            var name = $"{day.Date.ToString("dddd, MMMM d, yyyy", English)}, {state}" +
                (!day.IsEligible ? ", Outside this period, cannot be changed" : "") +
                (day.IsToday ? ", Today" : "");
            var dateText = day.Date.ToString("MMM d", English);
            var temporal = day.Date < pace.Today ? "✓" : day.Date == pace.Today ? "Today" : "";
            cells.Add(Border(ToggleButton(
                    dateText,
                    day.IsEligible && day.IsWorkday,
                    _ => { if (day.IsEligible) toggle(day.Date); })
                .IsEnabled(day.IsEligible)
                .HorizontalAlignment(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Center)
                .Padding(8, 4)
                // ToggleButton's string-only factory needs a native content bridge.
                // Keep the native panel across draft reconciliation, owned by the
                // button; it has no subscriptions or independent renderer lifetime.
                .Set(button =>
                {
                    var content = Content.GetValue(button, _ => new UsageDateContent());
                    content.Update(dateText, temporal);
                    button.Content = content;
                })
                .AutomationName(name)
                .HelpText(day.IsEligible
                    ? "Space toggles this date between working and rest. Changes remain in the Settings draft until Save."
                    : "This date is outside the current Credits cycle and cannot be changed."))
                // The wrapper stays enabled and hit-testable even when its date
                // button is disabled. No unavailable WinUI ShowOnDisabled API.
#pragma warning disable REACTOR_THEME_004 // Transparent hit target, not a themed painted surface.
                .Background(new SolidColorBrush(Microsoft.UI.Colors.Transparent))
#pragma warning restore REACTOR_THEME_004
                .ToolTip(name)
                .OnUnmount(CloseToolTip)
                .Grid(row: index / 7 + 1, column: index % 7)
                .WithKey(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return VStack(8,
            BodyStrong("Workday calendar").HeadingLevel(AutomationHeadingLevel.Level2),
            Body(PeriodText(cycle)).TextWrapping(TextWrapping.WrapWholeWords),
            BalanceRow(pace, dark, highContrast)
                .OnMount(root => layout.AttachBalance((Microsoft.UI.Xaml.Controls.Border)root)),
            ScrollViewer((Grid(Enumerable.Repeat(GridSize.Auto, 7).ToArray(),
                Enumerable.Repeat(GridSize.Auto, cycle.Days.Count / 7 + 1).ToArray(),
                cells.ToArray()) with { ColumnSpacing = 4, RowSpacing = 4 })
                .HorizontalAlignment(HorizontalAlignment.Left)
                .AutomationId("UsageCalendarGrid")
                .OnMount(layout.AttachGrid))
                .HorizontalContentAlignment(HorizontalAlignment.Left)
                .HorizontalAlignment(HorizontalAlignment.Left)
                // Preserve measured columns at large text sizes/narrow windows;
                // only overflow scrolls, never squeeze or clip date labels.
                .Set(scroll =>
                {
                    scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                })
                .AutomationId("UsageCalendarScroller")
                .OnMount(root => layout.AttachScroller((Microsoft.UI.Xaml.Controls.ScrollViewer)root)),
            Caption("Click a date to toggle whether it is a workday. Changes apply when you Save.")
                .TextWrapping(TextWrapping.WrapWholeWords))
            .AutomationId("UsageCalendarSection")
            .OnUnmount(_ => layout.Dispose());
    }

    private static string PeriodText(CopilotUsageCycle cycle)
    {
        var eligible = cycle.EligibleDays.ToArray();
        var dates = eligible.Length == 0 ? "No eligible dates" :
            $"{eligible[0].Date.ToString("MMM d, yyyy", English)} - {eligible[^1].Date.ToString("MMM d, yyyy", English)}";
        return $"This period: {dates}";
    }

    internal const string SurplusHelp = "Surplus: Credits used are below the elapsed working-day allocation, shown as green.";
    internal const string BudgetHelp = "Over budget: Credits used exceed the elapsed working-day allocation, shown as red.";
    internal const string NeutralHelp = "On track: The rounded credit balance is zero, shown as blue.";
    internal const string RoundingHelp = "(Days are rounded to the nearest whole workday, with halves rounded away from zero.)";
    internal const string BalanceHelp = SurplusHelp + " " + NeutralHelp + " " + BudgetHelp + " " +
        RoundingHelp;

    internal static Element BalanceRow(CopilotUsagePace pace, bool dark, bool highContrast)
    {
        var balance = UsageBalanceLabel.Render(pace, dark, highContrast);
        var workdayStats = BodyStrong(
                $"{pace.TotalWorkdays} total workdays · {pace.ElapsedWorkdays} elapsed through today")
            .TextWrapping(TextWrapping.WrapWholeWords)
            .LiveRegion(AutomationLiveSetting.Polite)
            .Padding(20, 0, 0, 0);
        return Border(VStack(4, balance, workdayStats)
                .Set(content => content.IsHitTestVisible = false))
#pragma warning disable REACTOR_THEME_004 // Include the unpainted icon/text gap in the tooltip hit area.
            .Background(new SolidColorBrush(Microsoft.UI.Colors.Transparent))
#pragma warning restore REACTOR_THEME_004
            .Padding(8)
            .HorizontalAlignment(HorizontalAlignment.Left)
            .Set(owner => UsageBalanceTooltip.Attach(owner, dark, highContrast))
            .AutomationId("UsageCalendarBalance")
            .AutomationName(pace.BalanceText)
            .HelpText(BalanceHelp)
            .OnUnmount(UsageBalanceTooltip.Detach);
    }

    private static void CloseToolTip(FrameworkElement owner)
    {
        // Covers explicit/automation-opened ToolTips as well as WinUI's private
        // wrapper for Element/string content. Drop logical content ownership
        // before Reactor disposes the rich tooltip's renderer.
        if (ToolTipService.GetToolTip(owner) is ToolTip tip) tip.IsOpen = false;
        ToolTipService.SetToolTip(owner, null);
    }
}

// Native measurement uses a stable sample of every English month/day label,
// Today and weekday headers, never today's value or the work/rest state.
// Auto grid columns therefore have equal intrinsic widths without stretching.
// The two real text children inherit the ToggleButton's font/disabled/checked
// foreground. No explicit foreground can defeat its native visual states.
internal sealed class UsageDateContent : Panel
{
    private static readonly string Samples = string.Join("\n",
        Enumerable.Range(1, 12).SelectMany(month => Enumerable.Range(1, 31)
            .Select(day => $"{CultureInfo.GetCultureInfo("en-US").DateTimeFormat.GetAbbreviatedMonthName(month)} {day}"))
            .Concat(["Today", "✓", "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]));
    internal XamlText DateLabel { get; } = new() { TextAlignment = TextAlignment.Center };
    internal XamlText TemporalLabel { get; } = new() { TextAlignment = TextAlignment.Center };
    private readonly XamlText _measure = new() { Text = Samples };
    private readonly XamlText _line = new() { Text = "Today" };
    private double _lineHeight;

    internal UsageDateContent()
    {
        Children.Add(DateLabel);
        Children.Add(TemporalLabel);
    }

    internal void Update(string date, string temporal)
    {
        DateLabel.Text = date;
        TemporalLabel.Text = temporal;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Copy the effective inherited typography at measure time. WinUI handles
        // OS text scale, font metrics and rasterization; no character estimates.
        foreach (var probe in new[] { _measure, _line })
        {
            probe.FontFamily = DateLabel.FontFamily;
            probe.FontSize = DateLabel.FontSize;
            probe.FontWeight = DateLabel.FontWeight;
            probe.FontStyle = DateLabel.FontStyle;
            probe.FontStretch = DateLabel.FontStretch;
            probe.CharacterSpacing = DateLabel.CharacterSpacing;
            probe.IsTextScaleFactorEnabled = DateLabel.IsTextScaleFactorEnabled;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var width = Math.Ceiling(_measure.DesiredSize.Width / 4) * 4;
        DateLabel.Measure(new Size(width, double.PositiveInfinity));
        TemporalLabel.Measure(new Size(width, double.PositiveInfinity));
        _lineHeight = Math.Max(_line.DesiredSize.Height,
            Math.Max(DateLabel.DesiredSize.Height, TemporalLabel.DesiredSize.Height));
        // Horizontal button padding exceeds vertical padding by 8 DIP. Account
        // for that in the minimum height, but never constrain scaled text height.
        return new Size(width, Math.Max(width + 8, _lineHeight * 2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = Math.Max(0, (finalSize.Height - _lineHeight * 2) / 2);
        DateLabel.Arrange(new Rect(0, y, finalSize.Width, _lineHeight));
        TemporalLabel.Arrange(new Rect(0, y + _lineHeight, finalSize.Width, _lineHeight));
        return finalSize;
    }
}
