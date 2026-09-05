using System.Text.Json;

namespace Pagurian.Modules.Hello;

static class HelloSettings
{
    public const string DefaultMessage = "Hello World Again!";

    public static string Message(JsonElement? settings)
    {
        if (settings is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "";
        }
        return DefaultMessage;
    }

    public static JsonElement Write(string message) =>
        JsonSerializer.SerializeToElement(new { message });
}
