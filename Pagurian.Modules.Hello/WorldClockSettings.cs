using System.Globalization;
using System.Text.Json;

namespace Pagurian.Modules.Hello;

static class WorldClockSettings
{
    public const string FormatSystem = "system";
    public const string Format24 = "h24";
    public const string Format12 = "h12";

    // A fresh shell (dragged in without settings) tracks the local time zone.
    public static string TimeZoneId(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("timeZone", out var timeZone) &&
            timeZone.ValueKind == JsonValueKind.String)
        {
            var id = timeZone.GetString();
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        return TimeZoneInfo.Local.Id;
    }

    public static string Label(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("label", out var label) &&
            label.ValueKind == JsonValueKind.String)
        {
            return label.GetString() ?? "";
        }
        return "";
    }

    // The picked city name, persisted alongside the zone id so cities sharing
    // one zone (Berlin/Vienna) keep their own label. Absent in legacy configs.
    public static string CityName(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("city", out var city) &&
            city.ValueKind == JsonValueKind.String)
        {
            return city.GetString() ?? "";
        }
        return "";
    }

    public static string Format(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String)
        {
            return format.GetString() switch
            {
                Format24 => Format24,
                Format12 => Format12,
                _ => FormatSystem,
            };
        }
        return FormatSystem;
    }

    // The cell/billboard second line: a custom label wins, then the persisted
    // city pick, then the curated city name for the stored zone (legacy
    // configs), then a safe fallback.
    public static string DisplayLabel(JsonElement? settings)
    {
        var label = Label(settings);
        if (label.Length > 0)
            return label;
        var city = CityName(settings);
        if (city.Length > 0)
            return city;
        var timeZoneId = TimeZoneId(settings);
        var match = WorldClockCities.Find(timeZoneId);
        if (match is not null)
            return match.Name;
        return timeZoneId == TimeZoneInfo.Local.Id ? "Local" : timeZoneId;
    }

    public static string FormatTime(DateTimeOffset time, string format) =>
        format switch
        {
            Format24 => time.ToString("HH:mm", CultureInfo.CurrentCulture),
            Format12 => time.ToString("h:mm tt", CultureInfo.CurrentCulture),
            _ => time.ToString("t", CultureInfo.CurrentCulture),
        };

    public static string FormatTimeSeconds(DateTimeOffset time, string format) =>
        format switch
        {
            Format24 => time.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            Format12 => time.ToString("h:mm:ss tt", CultureInfo.CurrentCulture),
            _ => time.ToString("T", CultureInfo.CurrentCulture),
        };

    public static JsonElement Write(string timeZoneId, string cityName, string label, string format) =>
        JsonSerializer.SerializeToElement(new { timeZone = timeZoneId, city = cityName, label, format });
}
