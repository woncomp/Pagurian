using System.Text.Json;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotSdkPollingSettings(
    int DiscoverySeconds = 5,
    int StatusSeconds = 3)
{
    internal const int MinimumSeconds = 1;
    internal const int MaximumSeconds = 3600;

    public CopilotSdkPollingSettings Normalize() => new(
        Clamp(DiscoverySeconds),
        Clamp(StatusSeconds));

    public static CopilotSdkPollingSettings Read(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("polling", out var polling) ||
            polling.ValueKind != JsonValueKind.Object)
            return new();

        return new CopilotSdkPollingSettings(
            ReadInt(polling, "discoverySeconds", 5),
            ReadInt(polling, "statusSeconds", 3)).Normalize();
    }

    public JsonElement Write() =>
        JsonSerializer.SerializeToElement(new
        {
            polling = new
            {
                discoverySeconds = Normalize().DiscoverySeconds,
                statusSeconds = Normalize().StatusSeconds,
            },
        });

    private static int ReadInt(JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var result)
            ? result
            : fallback;

    private static int Clamp(int value) =>
        Math.Clamp(value, MinimumSeconds, MaximumSeconds);
}
