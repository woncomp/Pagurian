using System.Text.Json;
using System.Text.Json.Serialization;
using Pagurian.Sdk;

namespace Pagurian;

// Loads config.json, which defines the logical trays — one per display/edge
// pair — and which shells sit in each, in what order, under which persistent
// 4-digit id:
//
//   { "trays": [
//     { "monitor": "primary",           // or a display identity key, e.g. "DEL40A6-UID4354"
//       "edge": "left",                 // optional; "right" is a planned extension
//       "shells": [
//         { "shell": "<Shell class FullName>", "id": "3842",
//           "settings": { ... } },      // optional, passed through verbatim
//         ...
//       ] },
//     ...
//   ] }
//
// The host never scans modules to auto-add shells: the file is the only
// source of tray membership. The file lives in the configured config folder
// (HostSettings.ConfigDir; default %LOCALAPPDATA%\Pagurian). A missing file
// is seeded with just the World Clock shell on the primary tray; an existing
// file is only ever modified through Save (the Shell editor), never on load.
//
// Backward compatibility: a legacy single "tray" array maps to the primary
// left tray in memory; the file is rewritten in v2 form on the next Save.
static class TrayConfig
{
    public sealed record Entry(string ShellType, string Id, JsonElement? Settings);

    // One configured tray: its identity and ordered entries. Group order in
    // the file is preserved and drives fallback composition order.
    public sealed record TrayGroup(TrayId Id, IReadOnlyList<Entry> Entries);

    public static string ConfigPath => Path.Combine(HostSettings.ConfigDir, "config.json");

    public static IReadOnlyList<TrayGroup> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                Seed();

            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var seenIds = new HashSet<string>();
            if (doc.RootElement.TryGetProperty("trays", out var traysElement) &&
                traysElement.ValueKind == JsonValueKind.Array)
                return LoadGroups(traysElement, seenIds);

            if (doc.RootElement.TryGetProperty("tray", out var legacyTray) &&
                legacyTray.ValueKind == JsonValueKind.Array)
            {
                // v1 file: the single flat tray was the primary taskbar's.
                // Remap in memory only; the next Save rewrites it as v2.
                PagurianLog.Host("config: migrating legacy \"tray\" array to the primary tray");
                return [new TrayGroup(TrayId.PrimaryLeft, LoadEntries(legacyTray, seenIds))];
            }

            PagurianLog.HostError($"config: missing or invalid \"trays\" array in {ConfigPath}");
            return [];
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"config: failed to load {ConfigPath}", ex);
            return [];
        }
    }

    private static IReadOnlyList<TrayGroup> LoadGroups(JsonElement traysElement, HashSet<string> seenIds)
    {
        var groups = new List<TrayGroup>();
        var seenTrays = new HashSet<TrayId>();
        foreach (var groupElement in traysElement.EnumerateArray())
        {
            var monitor = groupElement.TryGetProperty("monitor", out var m) &&
                          m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";
            if (monitor.Length == 0)
            {
                PagurianLog.HostError("config: tray group without a \"monitor\" key; skipped");
                continue;
            }
            var edge = TrayId.ParseEdge(
                groupElement.TryGetProperty("edge", out var e) ? e.GetString() : null,
                $"tray \"{monitor}\"");
            var id = new TrayId(monitor, edge);
            if (!seenTrays.Add(id))
            {
                PagurianLog.HostError($"config: duplicate tray \"{id}\"; skipped");
                continue;
            }
            var entries = groupElement.TryGetProperty("shells", out var shells) &&
                          shells.ValueKind == JsonValueKind.Array
                ? LoadEntries(shells, seenIds)
                : [];
            groups.Add(new TrayGroup(id, entries));
        }
        return groups;
    }

    private static IReadOnlyList<Entry> LoadEntries(JsonElement tray, HashSet<string> seenIds)
    {
        var entries = new List<Entry>();
        foreach (var item in tray.EnumerateArray())
        {
            var shellType = item.TryGetProperty("shell", out var s) &&
                            s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";
            // The Hello clock kind became the World Clock kind; remap
            // legacy entries in memory so existing trays keep their clock
            // (the file itself is only rewritten on the next Save).
            if (shellType == "Pagurian.Modules.Hello.HelloShell")
            {
                shellType = "Pagurian.Modules.Hello.WorldClockShell";
                PagurianLog.Host("config: remapped legacy Hello clock entry to World Clock");
            }
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
            // Ids are globally unique across all trays: `post` routes by bare
            // id with no monitor qualifier. Duplicates: keep the first.
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

    // Persists the tray groups (config folder created on demand). Writes to
    // a temp file first, then atomically replaces the target. Throws on
    // failure (after logging) so the Shell editor can surface the error.
    // Empty groups are omitted, except the primary tray is always written so
    // the file never degrades into a reseed.
    public static void Save(IReadOnlyList<TrayGroup> groups)
    {
        try
        {
            var doc = new
            {
                trays = groups
                    .Where(g => g.Entries.Count > 0 || g.Id == TrayId.PrimaryLeft)
                    .Select(g => new
                    {
                        monitor = g.Id.MonitorKey,
                        edge = g.Id.Edge == TrayEdge.Left ? null : TrayId.EdgeName(g.Id.Edge),
                        shells = g.Entries.Select(e => new
                        {
                            shell = e.ShellType,
                            id = e.Id,
                            settings = e.Settings,
                        }),
                    }),
            };
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
            PagurianLog.Host($"config: saved {groups.Sum(g => g.Entries.Count)} entr(ies) across {groups.Count} tray(s) to {ConfigPath}");
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

    // Seed config: just the World Clock shell (tracking local time) on the
    // primary tray with a fresh random id. Only written when no config exists.
    private static void Seed()
    {
        try
        {
            var seed = new
            {
                trays = new object[]
                {
                    new
                    {
                        monitor = TrayId.PrimaryMonitorKey,
                        shells = new object[]
                        {
                            new { shell = "Pagurian.Modules.Hello.WorldClockShell", id = NextId(Array.Empty<string>()) },
                        },
                    },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(seed,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }));
            PagurianLog.Host($"config: seeded {ConfigPath}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("config: failed to seed", ex);
        }
    }
}
