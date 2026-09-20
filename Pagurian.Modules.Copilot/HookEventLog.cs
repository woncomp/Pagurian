using System.Text;

namespace Pagurian.Modules.Copilot;

// Best-effort audit log of every hook event, one single-line JSON envelope
// per line. A process owns one exclusive file for its run.
static class HookEventLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
#if HOOK_EVENT_LOG_TESTS
    internal static string? TestLocalAppDataRoot { get; set; }
    internal static void CloseForTests()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
#endif

    public static void Write(string eventName, string? payloadJson)
    {
        try
        {
            lock (Gate)
            {
                _writer ??= CreateRunWriter();
                if (_writer is null)
                    return;

                var loggedAt = DateTime.Now.ToString("o");
                _writer.WriteLine(
                    $"{{\"loggedAt\":{System.Text.Json.JsonSerializer.Serialize(loggedAt)}," +
                    $"\"event\":{System.Text.Json.JsonSerializer.Serialize(eventName)}," +
                    $"\"payload\":{payloadJson ?? "null"}}}");
                _writer.Flush();
            }
        }
        catch
        {
            // logging is best-effort
        }
    }

    private static StreamWriter? CreateRunWriter()
    {
        try
        {
            var directory = Path.Combine(
#if HOOK_EVENT_LOG_TESTS
                TestLocalAppDataRoot ??
#endif
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pagurian", "Copilot");
            Directory.CreateDirectory(directory);

            string path;
            FileStream stream;
            for (var attempt = 0; ; attempt++)
            {
                var timestamp = DateTime.Now.AddMilliseconds(attempt)
                    .ToString("yyyyMMdd-HHmmss-fff");
                path = Path.Combine(directory, $"hook-events-{timestamp}.log");
                try
                {
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                    break;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another bridge process may have claimed this millisecond.
                }
            }

            CleanupPreviousRuns(directory, DateTime.Now.Date);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            return null;
        }
    }

    private static void CleanupPreviousRuns(string directory, DateTime today)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "hook-events-*.log"))
            {
                try
                {
                    if (File.GetCreationTime(path).Date < today)
                        File.Delete(path);
                }
                catch
                {
                    // A stale file can be in use by another bridge process.
                }
            }
        }
        catch
        {
            // Cleanup must never prevent the current run from logging.
        }
    }
}
