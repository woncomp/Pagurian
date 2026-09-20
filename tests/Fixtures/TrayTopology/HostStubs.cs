using Pagurian.Sdk;
namespace Pagurian;

// Only disk/config/module discovery are replaced; the registry, binding
// engine and config serialization are linked from production. HostSettings
// redirects every file into an isolated temp folder.
static class PagurianLog
{
    internal static void Tray(string sessionId, string s) => Console.WriteLine(s);
    internal static void Host(string s) => Console.WriteLine(s);
    internal static void HostError(string s, Exception? ex = null) => Console.WriteLine($"{s} {ex}");
}
static class ModuleLoader
{
    internal static readonly Dictionary<string, ShellAttribute> Catalog = new();
    internal static bool TryGetKind(string type, out ShellAttribute kind) => Catalog.TryGetValue(type, out kind!);
}
static class HostSettings
{
    internal static readonly string TestDir = Path.Combine(
        Path.GetTempPath(), "pagurian-topology-fixture-" + Guid.NewGuid().ToString("N")[..8]);
    public static string ConfigDir => TestDir;
}
