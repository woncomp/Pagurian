using System.Text.Json;

namespace Pagurian.Modules.Metrics;

static class MetricsSettings
{
    public const int DefaultTopProcesses = 3;
    public const int MinTopProcesses = 1;
    public const int MaxTopProcesses = 20;

    public static int TopProcesses(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("topProcesses", out var count) &&
            count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out var configured))
        {
            return Math.Clamp(configured, MinTopProcesses, MaxTopProcesses);
        }
        return DefaultTopProcesses;
    }

    public static JsonElement Write(int topProcesses) =>
        JsonSerializer.SerializeToElement(new
        {
            topProcesses = Math.Clamp(
                topProcesses,
                MinTopProcesses,
                MaxTopProcesses),
        });
}
