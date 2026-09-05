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
// source of tray membership. The file lives in the configured config folder
// (HostSettings.ConfigDir; default %LOCALAPPDATA%\Pagurian). A missing file
// is seeded with just the Hello shell; an existing file is only ever
// modified through Save (the settings UI), never on load.
static class TrayConfig
{
    public sealed record Entry(string ShellType, string Id, JsonElement? Settings);

    public static string ConfigPath => Path.Combine(HostSettings.ConfigDir, "config.json");

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

    // Persists the tray entries (config folder created on demand). Writes to
    // a temp file first, then atomically replaces the target. Throws on
    // failure (after logging) so the settings UI can surface the error.
    public static void Save(IReadOnlyList<Entry> entries)
    {
        try
        {
            var doc = new
            {
                tray = entries.Select(e => new
                {
                    shell = e.ShellType,
                    id = e.Id,
                    settings = e.Settings,
                }),
            };
            var json = JsonSerializer.Serialize(doc,
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
            PagurianLog.Host($"config: saved {entries.Count} entr(ies) to {ConfigPath}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"config: failed to save {ConfigPath}", ex);
            throw;
        }
    }

    // A fresh random 4-digit id not colliding with any of `taken`.
    public static string NextId(IEnumerable<string> taken)
    {
        var ids = new HashSet<string>(taken);
        string id;
        do { id = Random.Shared.Next(1000, 10000).ToString(); } while (!ids.Add(id));
        return id;
    }

    // Seed config: just the Hello shell with a fresh random id. Only written
    // when no config exists at all.
    private static void Seed()
    {
        try
        {
            var seed = new
            {
                tray = new object[]
                {
                    new { shell = "Pagurian.Modules.Hello.HelloShell", id = NextId(Array.Empty<string>()) },
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
