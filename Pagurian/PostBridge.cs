using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pagurian;

// Bridge mode: invoked as "Pagurian.exe post {shell_id} <cmd> [args...]" to
// deliver a message to a shell of the running app (the Copilot CLI hooks are
// the primary caller). Reads the payload from stdin when piped, wraps
// everything in a single-line JSON envelope, forwards it over a named pipe,
// and exits. NEVER throws and always exits 0 — callers (preToolUse hooks) are
// fail-closed, so a non-zero exit would block their work.
static class PostBridge
{
    public const string PipeName = "Pagurian.ShellMessages";

    public static void Run(string[] args)
    {
        try
        {
            // Need at least the shell id and a command.
            if (args.Length < 2)
                return;

            var id = args[0];
            var command = args[1];
            var rest = args.Skip(2).ToArray();

            string? payload = null;
            if (Console.IsInputRedirected)
            {
                using var stdin = Console.OpenStandardInput();
                using var reader = new StreamReader(stdin, Encoding.UTF8);
                var raw = reader.ReadToEnd();
                payload = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            }

            // The payload is embedded verbatim as raw JSON (or null) so the
            // receiver can parse it losslessly.
            var receivedAt = DateTime.Now.ToString("o");
            var message =
                $"{{\"id\":{JsonSerializer.Serialize(id)}," +
                $"\"command\":{JsonSerializer.Serialize(command)}," +
                $"\"args\":{JsonSerializer.Serialize(rest)}," +
                $"\"payload\":{payload ?? "null"}," +
                $"\"receivedAt\":{JsonSerializer.Serialize(receivedAt)}}}";

            // Forward to the running app, retrying briefly if it is not
            // listening yet.
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
            // never fail the caller
        }
    }
}
