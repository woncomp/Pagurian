using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Reactor;
using Pagurian.Sdk;

namespace Pagurian;

// Named-pipe server receiving post envelopes from the short-lived bridge
// processes (PostBridge): one single-line JSON message per connection,
// marshaled to the UI thread and routed to the addressed shell.
static class ShellMessageServer
{
    private static CancellationTokenSource? _cts;

    public static void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => Loop(_cts.Token));
    }

    public static void Stop() => _cts?.Cancel();

    private static async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PostBridge.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    using var msg = JsonDocument.Parse(line);
                    var root = msg.RootElement.Clone();
                    ReactorApp.UIDispatcher?.TryEnqueue(() => Dispatch(root));
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // UI thread. Envelope: {"id","command","args":[],"payload":<raw|null>,"receivedAt"}.
    private static void Dispatch(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) &&
                 idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? ""
            : "";
        var command = root.TryGetProperty("command", out var cmdEl) &&
                      cmdEl.ValueKind == JsonValueKind.String
            ? cmdEl.GetString() ?? ""
            : "";
        if (id.Length == 0 || command.Length == 0)
        {
            PagurianLog.Host("post: envelope without id or command; dropped");
            return;
        }

        var args = new List<string>();
        if (root.TryGetProperty("args", out var argsEl) &&
            argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String)
                    args.Add(a.GetString() ?? "");
            }
        }

        string? payload = null;
        if (root.TryGetProperty("payload", out var payloadEl) &&
            payloadEl.ValueKind != JsonValueKind.Null)
            payload = payloadEl.GetRawText();

        var receivedAt = root.TryGetProperty("receivedAt", out var atEl) &&
                         atEl.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(atEl.GetString(), out var at)
            ? at
            : DateTimeOffset.Now;

        TrayShells.RouteMessage(id, new ShellMessage(command, args, payload, receivedAt));
    }
}
