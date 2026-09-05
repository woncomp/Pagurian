using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pagurian.Modules.Copilot;

// Installs/uninstalls the Copilot CLI hook file that makes the CLI invoke
// "Pagurian.exe post {shellId} hook <event>" for every hook event (the events
// then arrive at CopilotShell.OnMessage through the host's post pipeline).
// The file lives at %USERPROFILE%\.copilot\hooks\pagurian-copilot-hook.json —
// one JSON file per hook provider, so it coexists with other tools' hooks —
// and exists only while the shell is in the tray (deleted on shell shutdown
// and, as a backstop, on process exit).
static class CopilotHookInstaller
{
    // GOTCHA (learned from the CopilotHookMonitor reference): Encoding.UTF8
    // emits a UTF-8 BOM, and the Copilot CLI's hook config parser is strict
    // JSON — it rejects the file with "Invalid JSON: expected value at line 1
    // column 1" and silently skips ALL hooks in it. Always write without a BOM.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // All 14 Copilot CLI hook events (camelCase).
    private static readonly string[] HookEvents =
    {
        "sessionStart", "sessionEnd", "userPromptSubmitted", "userPromptTransformed",
        "preToolUse", "postToolUse", "postToolUseFailure", "permissionRequest",
        "agentStop", "subagentStart", "subagentStop", "errorOccurred",
        "preCompact", "notification"
    };

    public static string HookFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "hooks", "pagurian-copilot-hook.json");

    public static void Install(string shellId)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) ||
                exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Dev runs via `dotnet run`: point the hook at the built exe
                // next to the dll, not at the dotnet host.
                exe = Path.Combine(AppContext.BaseDirectory, "Pagurian.exe");
            }
            var bashExe = exe.Replace('\\', '/');

            var sb = new StringBuilder();
            var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"hooks\": {");
            for (var i = 0; i < HookEvents.Length; i++)
            {
                var ev = HookEvents[i];
                var bash = JsonSerializer.Serialize($"\"{bashExe}\" post {shellId} hook {ev}", json);
                var powershell = JsonSerializer.Serialize($"& \"{exe}\" post {shellId} hook {ev}", json);
                sb.AppendLine($"    \"{ev}\": [");
                sb.AppendLine("      {");
                sb.AppendLine("        \"type\": \"command\",");
                sb.AppendLine($"        \"bash\": {bash},");
                sb.AppendLine($"        \"powershell\": {powershell},");
                sb.AppendLine("        \"timeoutSec\": 10");
                sb.AppendLine("      }");
                sb.AppendLine(i < HookEvents.Length - 1 ? "    ]," : "    ]");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");

            Directory.CreateDirectory(Path.GetDirectoryName(HookFilePath)!);
            File.WriteAllText(HookFilePath, sb.ToString(), Utf8NoBom);

            // Backstop for quit paths that skip the tray menu sequence.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Uninstall();
        }
        catch
        {
            // hook injection is best-effort; the app still works without it
        }
    }

    public static void Uninstall()
    {
        try
        {
            if (File.Exists(HookFilePath))
                File.Delete(HookFilePath);
        }
        catch
        {
            // best-effort
        }
    }
}
