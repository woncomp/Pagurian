using Pagurian.Sdk;
namespace Pagurian;
// Only disk/config/module discovery are replaced. All rendering, registry,
// measurement, native injection and sampler code are linked from production.
static class AppAssets { internal static string ApplicationIconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Pagurian-256.ico"); }
static class PagurianLog
{
    internal static void Tray(string sessionId, string s) => Console.WriteLine(s);
    internal static void Host(string s) => Console.WriteLine(s);
    internal static void HostError(string s, Exception? ex = null) => Console.WriteLine($"{s} {ex}");
}
static class TrayConfig { public sealed record Entry(string ShellType, string Id, System.Text.Json.JsonElement? Settings); }
static class ModuleLoader
{
    internal static readonly Dictionary<string, ShellAttribute> Catalog = new();
    internal static bool TryGetKind(string type, out ShellAttribute kind) => Catalog.TryGetValue(type, out kind!);
}
