using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Hello;

// World Clock tray cell, same small footprint as the old clock: two centered
// 12-DIP lines (city time over city label) updated every second. The date the
// clock used to show survives in the hover tooltip and the billboard. The time
// line carries the measured worst-case width (anti-jitter, metrics-style) and
// the label line is capped so long labels can't grow the cell.
class WorldClockCell : ShellCell
{
    private const double FontSizeDip = 12;
    private const double PaddingXDip = 10;
    private const double MinLabelWidthDip = 64;

    internal static string Tooltip(Shell shell)
    {
        var settings = shell.Settings;
        var zone = WorldClockCities.Resolve(WorldClockSettings.TimeZoneId(settings));
        var there = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var label = WorldClockSettings.DisplayLabel(settings);
        return $"{label} — {there.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture)}" +
               $" · {WorldClockCities.OffsetText(there.Offset)}";
    }

    public override Element Render()
    {
        var shell = (WorldClockShell)Owner;
        var settings = shell.Settings;
        var zone = WorldClockCities.Resolve(WorldClockSettings.TimeZoneId(settings));
        var format = WorldClockSettings.Format(settings);
        var label = WorldClockSettings.DisplayLabel(settings);

        var (time, setTime) = UseState(Now(zone, format));
        var lastText = UseRef(Now(zone, format));
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

        // 1s clock updates, reading the shell's live settings each tick (a
        // saved config change re-targets the clock without restarting). The
        // guard keeps the timer from re-rendering when nothing changed.
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

        // Saved-config and taskbar theme changes re-render immediately (the
        // label row comes from Render, not the timer state).
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

        // Anti-jitter floor (measured, so it stays font/locale-correct): the
        // time line is exactly as wide as its worst-case rendering across all
        // formats; the label line is capped so long labels stay compact.
        var timeWidth = WorstTimeWidth();
        var labelWidth = Math.Min(
            TextMeasurement.MeasureWidth(label, FontSizeDip),
            Math.Max(timeWidth, MinLabelWidthDip));

        return Border(
                FlexColumn(
                    TextBlock(time)
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Width(timeWidth)
                        .Foreground(Theme.TextBrush),
                    TextBlock(label)
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Width(labelWidth)
                        .MaxLines(1)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .Foreground(Theme.TextBrush))
                    .VerticalAlignment(VerticalAlignment.Center))
            .Padding(PaddingXDip, 0, PaddingXDip, 0);
    }

    private static string Now(TimeZoneInfo zone, string format)
    {
        var there = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return WorldClockSettings.FormatTime(there, format);
    }

    // Midnight probe rendered through every format: covers wide digits,
    // 12-hour designators and both digit counts regardless of locale.
    private static double WorstTimeWidth()
    {
        var probe = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var width = 0.0;
        foreach (var format in new[]
                 {
                     WorldClockSettings.FormatSystem,
                     WorldClockSettings.Format24,
                     WorldClockSettings.Format12,
                 })
        {
            width = Math.Max(
                width,
                TextMeasurement.MeasureWidth(
                    WorldClockSettings.FormatTime(probe, format), FontSizeDip));
        }
        return width;
    }
}
