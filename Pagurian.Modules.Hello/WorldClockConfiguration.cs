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

    public override Element Render()
    {
        var timeZoneId = WorldClockSettings.TimeZoneId(Settings);
        var label = WorldClockSettings.Label(Settings);
        var format = WorldClockSettings.Format(Settings);

        var cities = WorldClockCities.All;
        var items = cities
            .Select(city =>
                $"{city.Name} — {WorldClockCities.OffsetText(WorldClockCities.Resolve(city.TimeZoneId).BaseUtcOffset)}")
            .ToArray();
        var cityIndex = -1;
        for (var i = 0; i < cities.Count; i++)
        {
            if (cities[i].TimeZoneId == timeZoneId)
            {
                cityIndex = i;
                break;
            }
        }

        var formatIndex = format switch
        {
            WorldClockSettings.Format24 => 1,
            WorldClockSettings.Format12 => 2,
            _ => 0,
        };

        return FlexColumn(
            Body("Choose the city this clock follows. Drag more World Clock " +
                 "shells into the tray for additional cities.")
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText),
            ComboBox(
                    items,
                    cityIndex < 0 ? 0 : cityIndex,
                    index => SetSettings(WorldClockSettings.Write(
                        cities[index].TimeZoneId, label, format)))
                .Header("City")
                .AutomationName("City")
                .MinWidth(280)
                .HorizontalAlignment(HorizontalAlignment.Left)
                .Margin(0, 8, 0, 0),
            TextBox(
                    label,
                    value => SetSettings(WorldClockSettings.Write(
                        timeZoneId, value ?? "", format)),
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
                        timeZoneId, label,
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
