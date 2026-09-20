using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Hello;

// World Clock details for the configured city: a large live clock with
// seconds, the full date, the time zone, and a comparison against local time.
// Re-renders every second while open and follows saved-config changes through
// the shell's Changed event. Card chrome follows the metrics billboards'
// MaterialCard pattern; the natural content height stays below the
// BillboardLifecycle fixture's 288-DIP cap.
class WorldClockBillboard : Billboard
{
    public override double WidthDip => 360;

    public override double HeightDip => 240;

    public override string Title => WorldClockSettings.DisplayLabel(Shell.Settings);

    public override Element Render()
    {
        var shell = (WorldClockShell)Shell;
        var settings = shell.Settings;
        var zone = WorldClockCities.Resolve(WorldClockSettings.TimeZoneId(settings));
        var format = WorldClockSettings.Format(settings);
        var label = WorldClockSettings.DisplayLabel(settings);

        var (time, setTime) = UseState(Now(zone, format));
        var lastText = UseRef(Now(zone, format));
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();

        UseEffect(() =>
        {
            var timer = ReactorApp.UIDispatcher!.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                var currentZone = WorldClockCities.Resolve(
                    WorldClockSettings.TimeZoneId(shell.Settings));
                var currentFormat = WorldClockSettings.Format(shell.Settings);
                string t = Now(currentZone, currentFormat);
                if (t == lastText.Current)
                    return;
                lastText.Current = t;
                setTime(t);
            };
            timer.Start();
            return () => timer.Stop();
        }, Array.Empty<object>());

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            shell.Changed += OnChanged;
            Theme.Changed += OnChanged;
            return () =>
            {
                shell.Changed -= OnChanged;
                Theme.Changed -= OnChanged;
            };
        }, Array.Empty<object>());

        var highContrast = colorScheme == ColorScheme.HighContrast;
        var requestedTheme = highContrast
            ? ElementTheme.Default
            : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var nowThere = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var thereHour = nowThere.Hour;
        var isDay = thereHour >= 6 && thereHour < 18;

        var summaryCard = MaterialCard(
            VStack(8,
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        BodyStrong(label)
                            .HeadingLevel(AutomationHeadingLevel.Level1)
                            .Grid(row: 0, column: 0),
                        Caption(
                                $"{WorldClockCities.OffsetText(nowThere.Offset)} · " +
                                (isDay ? "Daytime" : "Night"))
                            .Foreground(ReactorTheme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 1),
                    ]),
                TextBlock(time)
                    .FontSize(34)
                    .Foreground(Theme.TextBrush)
                    .Set(text => Typography.SetNumeralAlignment(
                        text,
                        FontNumeralAlignment.Tabular)),
                Caption(nowThere.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture))
                    .Foreground(ReactorTheme.SecondaryText)),
            highContrast);

        var detailsCard = MaterialCard(
            VStack(8,
                ValueRow("Date", nowThere.ToString("D", CultureInfo.CurrentCulture)),
                ValueRow("Time zone", zone.Id),
                ValueRow("Compared to local", ComparedToLocal(nowLocal, nowThere, zone))),
            highContrast);

        var content = (ScrollViewer(VStack(8, summaryCard, detailsCard)) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch);

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private static string Now(TimeZoneInfo zone, string format)
    {
        var there = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return WorldClockSettings.FormatTimeSeconds(there, format);
    }

    private static string ComparedToLocal(
        DateTimeOffset nowLocal,
        DateTimeOffset nowThere,
        TimeZoneInfo zone)
    {
        if (zone.Id == TimeZoneInfo.Local.Id)
            return "Same time";

        var diff = nowThere.Offset - nowLocal.Offset;
        var sign = diff < TimeSpan.Zero ? "-" : "+";
        var value = diff < TimeSpan.Zero ? -diff : diff;
        var hours = value.TotalHours;
        var hoursText = hours % 1 == 0 ? $"{hours:0}" : $"{hours:0.#}";
        var dayDelta = (nowThere.Date - nowLocal.Date).Days;
        var dayText = dayDelta switch
        {
            0 => "same day",
            1 => "tomorrow",
            -1 => "yesterday",
            _ => $"{(dayDelta > 0 ? "+" : "")}{dayDelta} days",
        };
        return $"{sign}{hoursText} h · {dayText}";
    }

    private static Element ValueRow(string label, string value) =>
        (Grid(
            [GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                Caption(label)
                    .Foreground(ReactorTheme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Grid(row: 0, column: 0),
                Body(value)
                    .TextAlignment(TextAlignment.Right)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Grid(row: 0, column: 1),
            ]) with
        {
            ColumnSpacing = 12,
        });

    private static BorderElement Page(Element content, bool highContrast)
    {
        var page = Border(content)
            .Padding(8)
            .CornerRadius(8);

        return highContrast
            ? page
                .Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
                .WithBorder(ReactorTheme.Ref("SystemColorWindowTextColorBrush"), 2)
                .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge)
            : page;
    }

    private static BorderElement MaterialCard(Element content, bool highContrast) =>
        Border(content)
            .Padding(12)
            .CornerRadius(8)
            .Background(highContrast
                ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.SurfaceStroke,
                highContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
}
