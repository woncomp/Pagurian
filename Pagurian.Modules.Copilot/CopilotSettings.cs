using System.Text.Json;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotClients(
    bool CopilotCli,
    bool CopilotApp,
    bool VsCode);

static class CopilotSettings
{
    public static CopilotClients Clients(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("clients", out var clients) ||
            clients.ValueKind != JsonValueKind.Object)
        {
            return new CopilotClients(true, true, true);
        }

        return new CopilotClients(
            ReadBool(clients, "copilotCli"),
            ReadBool(clients, "copilotApp"),
            ReadBool(clients, "vsCode"));
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
        });

    private static bool ReadBool(JsonElement clients, string name) =>
        !clients.TryGetProperty(name, out var value) ||
        value.ValueKind != JsonValueKind.False;
}
