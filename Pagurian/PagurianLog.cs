using Pagurian.Sdk;

namespace Pagurian;

// The app's single unified log: one file, timestamp + level + tag per line.
// Host components write through PagurianLog.Host directly; modules/shells/
// cells/billboards write through their Sdk Logger facade, which lands here
// via the sink installed in Initialize.
static class PagurianLog
{
    private static readonly object FileGate = new();
    private static readonly DiagnosticLogQueue NavigationQueue = new(AppendLine);

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pagurian",
        "pagurian.log");

    public static void Initialize() => Logger.Sink = Write;

    public static void Host(string message) => Write("host", "INFO", message);

    public static void HostError(string message, Exception? ex = null) =>
        Write("host", "ERROR", ex == null ? message : $"{message}: {ex}");

    internal static void Navigation(string sessionId, string message, string level = "INFO") =>
        NavigationQueue.Enqueue(sessionId, message, level);

    internal static Task FlushNavigationAsync() => NavigationQueue.FlushAsync();

    internal static void FlushNavigationOnExit()
    {
        try
        {
            // Regular Settings close uses the asynchronous flush. Explicit
            // process exit cannot wait forever if disk I/O is stalled.
            FlushNavigationAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Diagnostic failures must never prevent application exit.
        }
    }

    private static void Write(string tag, string level, string message)
        => AppendLine($"{DateTimeOffset.Now:O} [{level}] [{tag}] {message}");

    private static void AppendLine(string line)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // logging is best-effort and never breaks the app
        }
    }
}
