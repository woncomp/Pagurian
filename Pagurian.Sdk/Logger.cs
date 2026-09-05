namespace Pagurian.Sdk;

// Thin logging facade shared by modules, shells, cells and billboards, all
// flowing into the host's single unified log file. The host installs the sink
// at startup; before that (or in a detached context) logging is a no-op.
public sealed class Logger
{
    private readonly string _tag;

    private Logger(string tag) => _tag = tag;

    internal static Logger For(string tag) => new(tag);

    // (tag, level, message) -> host log writer. Installed by the host.
    internal static Action<string, string, string>? Sink;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            Sink?.Invoke(_tag, level, ex == null ? message : $"{message}: {ex}");
        }
        catch
        {
            // logging never throws into module code
        }
    }
}
