namespace Pagurian.Modules.Hello;

// Curated world-clock city list: each entry pairs a friendly city label with
// its Windows time zone id (FindSystemTimeZoneById is native on Windows; no
// ICU dependency). The table is kept in UTC base-offset order, which is also
// the order the configuration picker shows. Several cities may share one zone
// id (e.g. Berlin and Vienna) — the picked city name is persisted in settings
// so the displayed label stays correct. TimeZoneInfo lookups are cached; an
// id this machine doesn't know (config copied from elsewhere) falls back to
// local time instead of throwing.
static class WorldClockCities
{
    public sealed record City(string Name, string TimeZoneId);

    public static readonly IReadOnlyList<City> All = new City[]
    {
        // UTC-10
        new("Honolulu", "Hawaiian Standard Time"),
        // UTC-9
        new("Anchorage", "Alaskan Standard Time"),
        // UTC-8
        new("Los Angeles", "Pacific Standard Time"),
        new("San Francisco", "Pacific Standard Time"),
        new("Las Vegas", "Pacific Standard Time"),
        new("Seattle", "Pacific Standard Time"),
        new("Vancouver", "Pacific Standard Time"),
        // UTC-7
        new("Phoenix", "US Mountain Standard Time"),
        new("Denver", "Mountain Standard Time"),
        new("Salt Lake City", "Mountain Standard Time"),
        new("Calgary", "Mountain Standard Time"),
        // UTC-6
        new("Chicago", "Central Standard Time"),
        new("Minneapolis", "Central Standard Time"),
        new("Dallas", "Central Standard Time"),
        new("Houston", "Central Standard Time"),
        new("Winnipeg", "Central Standard Time"),
        new("Mexico City", "Central Standard Time (Mexico)"),
        new("Guadalajara", "Central Standard Time (Mexico)"),
        new("Guatemala City", "Central America Standard Time"),
        new("San José", "Central America Standard Time"),
        // UTC-5
        new("New York", "Eastern Standard Time"),
        new("Boston", "Eastern Standard Time"),
        new("Washington, D.C.", "Eastern Standard Time"),
        new("Atlanta", "Eastern Standard Time"),
        new("Miami", "Eastern Standard Time"),
        new("Toronto", "Eastern Standard Time"),
        new("Montreal", "Eastern Standard Time"),
        new("Bogotá", "SA Pacific Standard Time"),
        new("Quito", "SA Pacific Standard Time"),
        new("Lima", "SA Pacific Standard Time"),
        new("Panama City", "SA Pacific Standard Time"),
        new("Havana", "Cuba Standard Time"),
        // UTC-4
        new("Halifax", "Atlantic Standard Time"),
        new("San Juan", "SA Western Standard Time"),
        new("La Paz", "SA Western Standard Time"),
        new("Caracas", "Venezuela Standard Time"),
        new("Santiago", "Pacific SA Standard Time"),
        // UTC-3:30
        new("St. John's", "Newfoundland Standard Time"),
        // UTC-3
        new("Asunción", "Paraguay Standard Time"),
        new("São Paulo", "E. South America Standard Time"),
        new("Rio de Janeiro", "E. South America Standard Time"),
        new("Buenos Aires", "Argentina Standard Time"),
        new("Montevideo", "Montevideo Standard Time"),
        // UTC-1
        new("Ponta Delgada", "Azores Standard Time"),
        // UTC+0
        new("London", "GMT Standard Time"),
        new("Dublin", "GMT Standard Time"),
        new("Lisbon", "GMT Standard Time"),
        new("Reykjavík", "Greenwich Standard Time"),
        new("Accra", "Greenwich Standard Time"),
        new("Dakar", "Greenwich Standard Time"),
        // UTC+1
        new("Casablanca", "Morocco Standard Time"),
        new("Lagos", "W. Central Africa Standard Time"),
        new("Kinshasa", "W. Central Africa Standard Time"),
        new("Amsterdam", "W. Europe Standard Time"),
        new("Berlin", "W. Europe Standard Time"),
        new("Munich", "W. Europe Standard Time"),
        new("Vienna", "W. Europe Standard Time"),
        new("Zurich", "W. Europe Standard Time"),
        new("Stockholm", "W. Europe Standard Time"),
        new("Oslo", "W. Europe Standard Time"),
        new("Rome", "W. Europe Standard Time"),
        new("Paris", "Romance Standard Time"),
        new("Madrid", "Romance Standard Time"),
        new("Brussels", "Romance Standard Time"),
        new("Copenhagen", "Romance Standard Time"),
        new("Prague", "Central European Standard Time"),
        new("Budapest", "Central European Standard Time"),
        new("Warsaw", "Central Europe Standard Time"),
        // UTC+2
        new("Helsinki", "FLE Standard Time"),
        new("Tallinn", "FLE Standard Time"),
        new("Kyiv", "FLE Standard Time"),
        new("Sofia", "FLE Standard Time"),
        new("Athens", "GTB Standard Time"),
        new("Bucharest", "GTB Standard Time"),
        new("Cairo", "Egypt Standard Time"),
        new("Jerusalem", "Israel Standard Time"),
        new("Johannesburg", "South Africa Standard Time"),
        new("Cape Town", "South Africa Standard Time"),
        new("Windhoek", "Namibia Standard Time"),
        new("Beirut", "Middle East Standard Time"),
        new("Amman", "Jordan Standard Time"),
        // UTC+3
        new("Istanbul", "Turkey Standard Time"),
        new("Moscow", "Russian Standard Time"),
        new("Saint Petersburg", "Russian Standard Time"),
        new("Minsk", "Belarus Standard Time"),
        new("Nairobi", "E. Africa Standard Time"),
        new("Addis Ababa", "E. Africa Standard Time"),
        new("Riyadh", "Arab Standard Time"),
        new("Kuwait City", "Arab Standard Time"),
        new("Doha", "Arab Standard Time"),
        new("Baghdad", "Arabic Standard Time"),
        // UTC+3:30
        new("Tehran", "Iran Standard Time"),
        // UTC+4
        new("Dubai", "Arabian Standard Time"),
        new("Muscat", "Arabian Standard Time"),
        new("Baku", "Azerbaijan Standard Time"),
        new("Tbilisi", "Georgian Standard Time"),
        new("Yerevan", "Caucasus Standard Time"),
        // UTC+4:30
        new("Kabul", "Afghanistan Standard Time"),
        // UTC+5
        new("Karachi", "Pakistan Standard Time"),
        new("Islamabad", "Pakistan Standard Time"),
        new("Tashkent", "West Asia Standard Time"),
        // UTC+5:30
        new("Mumbai", "India Standard Time"),
        new("New Delhi", "India Standard Time"),
        new("Bengaluru", "India Standard Time"),
        new("Kolkata", "India Standard Time"),
        new("Colombo", "Sri Lanka Standard Time"),
        // UTC+5:45
        new("Kathmandu", "Nepal Standard Time"),
        // UTC+6
        new("Dhaka", "Bangladesh Standard Time"),
        new("Almaty", "Central Asia Standard Time"),
        // UTC+6:30
        new("Yangon", "Myanmar Standard Time"),
        // UTC+7
        new("Bangkok", "SE Asia Standard Time"),
        new("Hanoi", "SE Asia Standard Time"),
        new("Jakarta", "SE Asia Standard Time"),
        // UTC+8
        new("Beijing", "China Standard Time"),
        new("Shanghai", "China Standard Time"),
        new("Hong Kong", "China Standard Time"),
        new("Taipei", "Taipei Standard Time"),
        new("Singapore", "Singapore Standard Time"),
        new("Kuala Lumpur", "Singapore Standard Time"),
        new("Manila", "Singapore Standard Time"),
        new("Ulaanbaatar", "Ulaanbaatar Standard Time"),
        new("Perth", "W. Australia Standard Time"),
        // UTC+9
        new("Tokyo", "Tokyo Standard Time"),
        new("Seoul", "Korea Standard Time"),
        // UTC+9:30
        new("Adelaide", "Cen. Australia Standard Time"),
        new("Darwin", "AUS Central Standard Time"),
        // UTC+10
        new("Brisbane", "E. Australia Standard Time"),
        new("Sydney", "AUS Eastern Standard Time"),
        new("Melbourne", "AUS Eastern Standard Time"),
        // UTC+12
        new("Auckland", "New Zealand Standard Time"),
        new("Suva", "Fiji Standard Time"),
    };

    private static readonly Dictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);

    public static City? Find(string timeZoneId) =>
        All.FirstOrDefault(city => city.TimeZoneId == timeZoneId);

    // Exact match for a persisted pick; several cities share one zone id, so
    // the name disambiguates.
    public static City? Find(string name, string timeZoneId) =>
        All.FirstOrDefault(city =>
            city.Name == name && city.TimeZoneId == timeZoneId);

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
