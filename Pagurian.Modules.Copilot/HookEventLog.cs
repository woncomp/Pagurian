using System.Text;

namespace Pagurian.Modules.Copilot;

// Best-effort audit log of every hook event, one single-line JSON envelope
// per line, next to the exe. Kept from the pre-plugin bridge behavior: the
// event is in the log file even when nothing consumes it.
static class HookEventLog
{
    private static string LogFile =>
        Path.Combine(AppContext.BaseDirectory, "hook-events.log");

    public static void Write(string eventName, string? payloadJson)
    {
        try
        {
            var loggedAt = DateTime.Now.ToString("o");
            var line =
                $"{{\"loggedAt\":{System.Text.Json.JsonSerializer.Serialize(loggedAt)}," +
                $"\"event\":{System.Text.Json.JsonSerializer.Serialize(eventName)}," +
                $"\"payload\":{payloadJson ?? "null"}}}";
            File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // logging is best-effort
        }
    }
}
