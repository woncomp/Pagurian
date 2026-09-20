using System.Text.Json;

namespace Pagurian.Modules.Copilot;

internal enum CopilotSessionIconSize { Medium, Large }

internal sealed record CopilotClients(
    bool CopilotCli,
    bool CopilotApp,
    bool VsCode,
    CopilotSessionIconSize IconSize);

static class CopilotSettings
{
    public static CopilotClients Clients(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } value)
        {
            return DefaultClients;
        }

        bool copilotCli = true;
        bool copilotApp = true;
        bool vsCode = true;
        if (value.TryGetProperty("clients", out var clients) &&
            clients.ValueKind == JsonValueKind.Object)
        {
            copilotCli = ReadBool(clients, "copilotCli");
            copilotApp = ReadBool(clients, "copilotApp");
            vsCode = ReadBool(clients, "vsCode");
        }
        return new CopilotClients(
            copilotCli,
            copilotApp,
            vsCode,
            ReadIconSize(value));
    }

    public static JsonElement Write(CopilotClients clients) =>
        JsonSerializer.SerializeToElement(new
        {
            clients = new
            {
                copilotCli = clients.CopilotCli,
                copilotApp = clients.CopilotApp,
                vsCode = clients.VsCode,
            },
            iconSize = clients.IconSize == CopilotSessionIconSize.Large ? "large" : "medium",
        });

    private static CopilotClients DefaultClients =>
        new(true, true, true, CopilotSessionIconSize.Medium);

    private static CopilotSessionIconSize ReadIconSize(JsonElement settings) =>
        settings.TryGetProperty("iconSize", out var size) &&
        size.ValueKind == JsonValueKind.String &&
        string.Equals(size.GetString(), "large", StringComparison.OrdinalIgnoreCase)
            ? CopilotSessionIconSize.Large
            : CopilotSessionIconSize.Medium;

    private static bool ReadBool(JsonElement clients, string name) =>
        !clients.TryGetProperty(name, out var value) ||
        value.ValueKind != JsonValueKind.False;
}
