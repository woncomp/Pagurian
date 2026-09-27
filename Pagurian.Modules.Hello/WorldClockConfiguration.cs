using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;
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

        string DisplayText(WorldClockCities.City city) =>
            $"{city.Name} — {WorldClockCities.OffsetText(
                WorldClockCities.Resolve(city.TimeZoneId).BaseUtcOffset)}";

        // The box Text tracks the configured city; an unset Text (no city yet)
        // leaves the placeholder visible. Optional<string>.Unset means "the
        // control owns its text", which also lets the user type freely.
        var selected = cityName.Length > 0
            ? WorldClockCities.Find(cityName, timeZoneId)
            : WorldClockCities.Find(timeZoneId);
        var selectedText = selected is null ? null : DisplayText(selected);

        var formatIndex = format switch
        {
            WorldClockSettings.Format24 => 1,
            WorldClockSettings.Format12 => 2,
            _ => 0,
        };

        // Search state lives in the component so the suggestion list is a
        // plain reactive projection of the query. AutoSuggestBox has no
        // built-in filter, so we drive Suggestions from the typed text.
        var (query, setQuery) = UseState("");
        var trimmed = query.Trim();

        // Once a city is picked the box text equals its display text; treat
        // that as "no query" so the list shows all cities again instead of
        // filtering on the "Name — UTC±HH:MM" string.
        var isShowingSelection = selectedText is not null &&
                                 trimmed.Equals(selectedText, StringComparison.Ordinal);
        var effectiveQuery = isShowingSelection ? "" : trimmed;

        var suggestions = (effectiveQuery.Length == 0
                ? cities
                : cities.Where(c => c.Name.Contains(
                    effectiveQuery, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxSuggestions)
            .Select(DisplayText)
            .ToArray();
        // Keep the list open while typing a fresh query, but not once a city
        // is selected (its display text then fills the box).
        var listOpen = !isShowingSelection && effectiveQuery.Length > 0;

        // REACTOR_A11Y_003: the label is set via the fluent .Header("City")
        // below; the analyzer only recognizes a `header:` factory argument,
        // which this factory does not accept.
#pragma warning disable REACTOR_A11Y_003
        // While the user is typing (query diverged from the selection) the
        // box must reflect the typed text, not the selected city — otherwise
        // every keystroke is overwritten by the controlled selectedText value.
        var boxText = isShowingSelection
            ? selectedText ?? Optional<string>.Unset
            : trimmed;
        var cityPicker = AutoSuggestBox(
                text: boxText,
                onTextChanged: text => setQuery(text ?? ""))
            with
            {
                PlaceholderText = "Search cities…",
                Suggestions = suggestions,
                IsSuggestionListOpen = listOpen,
                OnSuggestionChosen = chosen =>
                {
                    var city = cities.FirstOrDefault(
                        c => DisplayText(c) == chosen);
                    if (city is not null)
                        SetSettings(WorldClockSettings.Write(
                            city.TimeZoneId, city.Name, label, format));
                },
            };
#pragma warning restore REACTOR_A11Y_003

        return FlexColumn(
            Body("Search for the city this clock follows. Drag more World Clock " +
                 "shells into the tray for additional cities.")
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText),
            cityPicker
                .Header("City")
                .AutomationName("City")
                .MinWidth(280)
                .HorizontalAlignment(HorizontalAlignment.Left)
                .Margin(0, 8, 0, 0),
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
