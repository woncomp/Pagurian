namespace Pagurian.Modules.Hello;

// Curated world-clock city list: each entry pairs a friendly city label with
// its Windows time zone id (FindSystemTimeZoneById is native on Windows; no
// ICU dependency). The table is kept in UTC base-offset order, which is also
// the order the configuration picker shows. TimeZoneInfo lookups are cached;
// an id this machine doesn't know (config copied from elsewhere) falls back
// to local time instead of throwing.
static class WorldClockCities
{
    public sealed record City(string Name, string TimeZoneId);

    public static readonly IReadOnlyList<City> All = new City[]
    {
        new("Honolulu", "Hawaiian Standard Time"),
        new("Los Angeles", "Pacific Standard Time"),
        new("Denver", "Mountain Standard Time"),
        new("Phoenix", "US Mountain Standard Time"),
        new("Chicago", "Central Standard Time"),
        new("Mexico City", "Central Standard Time (Mexico)"),
        new("New York", "Eastern Standard Time"),
        new("Bogotá", "SA Pacific Standard Time"),
        new("Lima", "SA Pacific Standard Time"),
        new("Santiago", "Pacific SA Standard Time"),
        new("São Paulo", "E. South America Standard Time"),
        new("Buenos Aires", "Argentina Standard Time"),
        new("London", "GMT Standard Time"),
        new("Reykjavík", "Greenwich Standard Time"),
        new("Berlin", "W. Europe Standard Time"),
        new("Paris", "Romance Standard Time"),
        new("Lagos", "W. Central Africa Standard Time"),
        new("Cairo", "Egypt Standard Time"),
        new("Johannesburg", "South Africa Standard Time"),
        new("Jerusalem", "Israel Standard Time"),
        new("Helsinki", "FLE Standard Time"),
        new("Moscow", "Russian Standard Time"),
        new("Istanbul", "Turkey Standard Time"),
        new("Nairobi", "E. Africa Standard Time"),
        new("Riyadh", "Arab Standard Time"),
        new("Dubai", "Arabian Standard Time"),
        new("Kabul", "Afghanistan Standard Time"),
        new("Karachi", "Pakistan Standard Time"),
        new("Mumbai", "India Standard Time"),
        new("Colombo", "Sri Lanka Standard Time"),
        new("Kathmandu", "Nepal Standard Time"),
        new("Dhaka", "Bangladesh Standard Time"),
        new("Bangkok", "SE Asia Standard Time"),
        new("Jakarta", "SE Asia Standard Time"),
        new("Shanghai", "China Standard Time"),
        new("Singapore", "Singapore Standard Time"),
        new("Perth", "W. Australia Standard Time"),
        new("Tokyo", "Tokyo Standard Time"),
        new("Seoul", "Korea Standard Time"),
        new("Adelaide", "Cen. Australia Standard Time"),
        new("Sydney", "AUS Eastern Standard Time"),
        new("Auckland", "New Zealand Standard Time"),
    };

    private static readonly Dictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);

    public static City? Find(string timeZoneId) =>
        All.FirstOrDefault(city => city.TimeZoneId == timeZoneId);

    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (string.IsNullOrEmpty(timeZoneId) ||
            timeZoneId == TimeZoneInfo.Local.Id)
        {
            return TimeZoneInfo.Local;
        }
        if (_zones.TryGetValue(timeZoneId, out var cached))
            return cached;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            _zones[timeZoneId] = zone;
            return zone;
        }
        catch (TimeZoneNotFoundException)
        {
            _zones[timeZoneId] = TimeZoneInfo.Local;
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            _zones[timeZoneId] = TimeZoneInfo.Local;
            return TimeZoneInfo.Local;
        }
    }

    public static string OffsetText(TimeSpan offset)
    {
        var value = offset < TimeSpan.Zero ? -offset : offset;
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{value.Hours:00}:{value.Minutes:00}";
    }
}
