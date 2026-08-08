using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pagurian;

// Bridge mode: invoked by a Copilot CLI hook as "Pagurian.exe --hook <event>"
// (the hook file installed by CopilotHookInstaller makes the CLI do this for
// every hook event). The Copilot CLI pipes the event payload JSON to this
// process's stdin; the bridge wraps it in a single-line envelope, appends it
// to hook-events.log next to the exe, forwards it to the running Pagurian app
// over a named pipe, and exits. NEVER throws and always exits 0: preToolUse
// command hooks are fail-closed (a non-zero exit code would block the Copilot
// tool call that triggered the hook).
static class CopilotHookBridge
{
    // Named pipe the bridge forwards events to (CopilotSessionTracker listens).
    public const string PipeName = "PagurianCopilotHook";

    private static string LogFile =>
        Path.Combine(AppContext.BaseDirectory, "hook-events.log");

    public static void Run(string eventName)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, Encoding.UTF8);
            var rawInput = reader.ReadToEnd();

            // Build a single-line message. The payload is embedded verbatim as
            // raw JSON (or null) so the receiver can parse it losslessly.
            var payload = string.IsNullOrWhiteSpace(rawInput) ? "null" : rawInput.Trim();
            var loggedAt = DateTime.Now.ToString("o");
            var message = $"{{\"loggedAt\":{JsonSerializer.Serialize(loggedAt)},\"event\":{JsonSerializer.Serialize(eventName)},\"payload\":{payload}}}";

            try
            {
                File.AppendAllText(LogFile, message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // logging is best-effort
            }

            // Forward to the running app. If the app is not listening yet,
            // retry briefly; the event is already in the log file regardless.
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    pipe.Connect(500);
                    var bytes = Encoding.UTF8.GetBytes(message + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    break;
                }
                catch (TimeoutException) { /* try again */ }
                catch (IOException) { /* try again */ }
                catch { break; }
                Thread.Sleep(150);
            }
        }
        catch
        {
            // never fail the hook
        }
    }
}
