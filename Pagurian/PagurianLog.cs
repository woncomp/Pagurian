using Pagurian.Sdk;

namespace Pagurian;

// The app's single unified log: one file, timestamp + level + tag per line.
// Host components write through PagurianLog.Host directly; modules/shells/
// cells/billboards write through their Sdk Logger facade, which lands here
// via the sink installed in Initialize.
static class PagurianLog
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pagurian",
        "pagurian.log");

    public static void Initialize() => Logger.Sink = Write;

    public static void Host(string message) => Write("host", "INFO", message);

    public static void HostError(string message, Exception? ex = null) =>
        Write("host", "ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string tag, string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} [{level}] [{tag}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging is best-effort and never breaks the app
        }
    }
}
