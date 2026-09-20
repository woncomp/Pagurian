using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pagurian.Modules.Copilot;

// Only exceptions to Monday-Friday are persisted; dates never recur.
internal sealed class CopilotUsageSettings
{
    public const string PropertyName = "workdayOverrides";
    private readonly Dictionary<DateOnly, bool> _overrides;
    public static CopilotUsageSettings Default { get; } = new([]);

    private CopilotUsageSettings(Dictionary<DateOnly, bool> overrides) =>
        _overrides = overrides;

    public static bool DefaultWorkday(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public bool IsWorkday(DateOnly date) =>
        _overrides.TryGetValue(date, out var work) ? work : DefaultWorkday(date);

    public CopilotUsageSettings Toggle(DateOnly date)
    {
        var copy = new Dictionary<DateOnly, bool>(_overrides);
        var next = !IsWorkday(date);
        if (next == DefaultWorkday(date))
            copy.Remove(date);
        else
            copy[date] = next;
        return new(copy);
    }

    public static CopilotUsageSettings Read(JsonElement? settings, Action<string> warn)
    {
        if (settings is null || settings.Value.ValueKind == JsonValueKind.Null)
            return Default;
        if (settings.Value.ValueKind != JsonValueKind.Object)
        {
            warn("Copilot Usage settings must be an object; using weekday defaults.");
            return Default;
        }
        if (!settings.Value.TryGetProperty(PropertyName, out var values))
            return Default;
        if (values.ValueKind != JsonValueKind.Object)
        {
            warn("Copilot Usage workdayOverrides must be an object; using weekday defaults.");
            return Default;
        }
        var result = new Dictionary<DateOnly, bool>();
        foreach (var entry in values.EnumerateObject())
        {
            if (!DateOnly.TryParseExact(entry.Name, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                entry.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                // Do not log arbitrary configuration content.
                warn("Ignoring malformed Copilot Usage date override; expected ISO date and boolean.");
                continue;
            }
            var work = entry.Value.GetBoolean();
            result.Remove(date);
            if (work != DefaultWorkday(date))
                result[date] = work;
        }
        return new(result);
    }

    public JsonElement Write(JsonElement? original)
    {
        var root = original is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject()
            : new JsonObject();
        var overrides = new JsonObject();
        foreach (var (date, work) in _overrides.OrderBy(pair => pair.Key))
            overrides[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = work;
        root[PropertyName] = overrides;
        return JsonSerializer.SerializeToElement(root);
    }
}
