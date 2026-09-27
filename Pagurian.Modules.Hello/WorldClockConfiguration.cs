using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;
using static Microsoft.UI.Reactor.Controls.AutoSuggestDsl;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Hello;

class WorldClockConfiguration : ShellConfiguration
{
    private static readonly string[] FormatOptions = ["System default", "24-hour", "12-hour"];

    // Keeps the suggestion popup short; the list is small enough that a
    // substring filter is instant, so no async work is needed.
    private const int MaxSuggestions = 25;

    public override Element Render()
    {
        var timeZoneId = WorldClockSettings.TimeZoneId(Settings);
        var cityName = WorldClockSettings.CityName(Settings);
        var label = WorldClockSettings.Label(Settings);
        var format = WorldClockSettings.Format(Settings);

        var cities = WorldClockCities.All;
        var selected = cityName.Length > 0
            ? WorldClockCities.Find(cityName, timeZoneId)
            : WorldClockCities.Find(timeZoneId);

        var formatIndex = format switch
        {
            WorldClockSettings.Format24 => 1,
            WorldClockSettings.Format12 => 2,
            _ => 0,
        };

        var cityPicker = AutoSuggest<WorldClockCities.City?>(
                selected,
                city =>
                {
                    if (city is null)
                        return;
                    SetSettings(WorldClockSettings.Write(
                        city.TimeZoneId, city.Name, label, format));
                },
                (query, _) =>
                {
                    var text = (query ?? "").Trim();
                    var matches = text.Length == 0
                        ? cities.Take(MaxSuggestions)
                        : cities
                            .Where(city => city.Name.Contains(
                                text, StringComparison.OrdinalIgnoreCase))
                            .Take(MaxSuggestions);
                    return Task.FromResult(
                        (IReadOnlyList<WorldClockCities.City?>)matches
                            .Cast<WorldClockCities.City?>()
                            .ToArray());
                },
                city => city is null ? "" : $"{city.Name} — {WorldClockCities.OffsetText(
                    WorldClockCities.Resolve(city.TimeZoneId).BaseUtcOffset)}",
                "Search cities…")
            with { EmptyMessage = "No cities match" };

        return FlexColumn(
            Body("Search for the city this clock follows. Drag more World Clock " +
                 "shells into the tray for additional cities.")
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText),
            Caption("City")
                .Margin(0, 8, 0, 0),
            cityPicker
                .AutomationName("City")
                .MinWidth(280)
                .HorizontalAlignment(HorizontalAlignment.Left)
                .Margin(0, 4, 0, 0),
            TextBox(
                    label,
                    value => SetSettings(WorldClockSettings.Write(
                        timeZoneId, cityName, value ?? "", format)),
                    placeholderText: WorldClockSettings.DisplayLabel(Settings),
                    header: "Label (optional)")
                .AutomationName("Label")
                .MinWidth(280)
                .HorizontalAlignment(HorizontalAlignment.Left)
                .Margin(0, 8, 0, 0),
            RadioButtons(
                    FormatOptions,
                    formatIndex,
                    index => SetSettings(WorldClockSettings.Write(
                        timeZoneId, cityName, label,
                        index switch
                        {
                            1 => WorldClockSettings.Format24,
                            2 => WorldClockSettings.Format12,
                            _ => WorldClockSettings.FormatSystem,
                        })))
                .Set(buttons => buttons.Header = "Clock format")
                .AutomationName("Clock format")
                .HorizontalAlignment(HorizontalAlignment.Left)
                .Margin(0, 8, 0, 0));
    }
}
