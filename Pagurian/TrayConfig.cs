using System.Text.Json;
using Pagurian.Sdk;

namespace Pagurian;

// Loads config.json, which defines which shells sit in the tray, in what
// order, and under which persistent 4-digit id:
//
//   { "tray": [
//     { "shell": "<Shell class FullName>", "id": "3842",
//       "settings": { ... } },   // optional, passed through verbatim
//     ...
//   ] }
//
// The host never scans modules to auto-add shells: the file is the only
// source of tray membership. A missing file is seeded with the three
// first-party modules' shells (the development stand-in until a
// configuration UI exists); an existing file is never modified.
static class TrayConfig
{
    public sealed record Entry(string ShellType, string Id, JsonElement? Settings);

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pagurian",
        "config.json");

    public static IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                Seed();

            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!doc.RootElement.TryGetProperty("tray", out var tray) ||
                tray.ValueKind != JsonValueKind.Array)
            {
                PagurianLog.HostError($"config: missing or invalid \"tray\" array in {ConfigPath}");
                return Array.Empty<Entry>();
            }

            var entries = new List<Entry>();
            var seenIds = new HashSet<string>();
            foreach (var item in tray.EnumerateArray())
            {
                var shellType = item.TryGetProperty("shell", out var s) &&
                                s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? ""
                    : "";
                var id = item.TryGetProperty("id", out var i) &&
                         i.ValueKind == JsonValueKind.String
                    ? i.GetString() ?? ""
                    : "";

                if (shellType.Length == 0)
                {
                    PagurianLog.HostError("config: entry without a \"shell\" type; skipped");
                    continue;
                }
                if (id.Length != 4 || !id.All(char.IsDigit))
                {
                    PagurianLog.HostError($"config: id \"{id}\" for {shellType} is not a 4-digit number; skipped");
                    continue;
                }
                // Duplicate ids are a config error: log and ignore the
                // duplicates, keep the first occurrence.
                if (!seenIds.Add(id))
                {
                    PagurianLog.HostError($"config: duplicate id \"{id}\" ({shellType}); skipped");
                    continue;
                }

                JsonElement? settings = item.TryGetProperty("settings", out var st) &&
                                        st.ValueKind == JsonValueKind.Object
                    ? st.Clone()
                    : null;
                entries.Add(new Entry(shellType, id, settings));
            }
            return entries;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"config: failed to load {ConfigPath}", ex);
            return Array.Empty<Entry>();
        }
    }

    // Development stand-in config: the three first-party modules' shells with
    // fresh random ids. Only written when no config exists at all.
    private static void Seed()
    {
        try
        {
            var ids = new HashSet<string>();
            string NextId()
            {
                string id;
                do { id = Random.Shared.Next(1000, 10000).ToString(); } while (!ids.Add(id));
                return id;
            }

            var seed = new
            {
                tray = new object[]
                {
                    new { shell = "Pagurian.Modules.Hello.HelloShell", id = NextId() },
                    new { shell = "Pagurian.Modules.Metrics.CpuShell", id = NextId() },
                    new { shell = "Pagurian.Modules.Metrics.MemShell", id = NextId() },
                    new { shell = "Pagurian.Modules.Copilot.CopilotShell", id = NextId() },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(seed,
                new JsonSerializerOptions { WriteIndented = true }));
            PagurianLog.Host($"config: seeded {ConfigPath}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("config: failed to seed", ex);
        }
    }
}
