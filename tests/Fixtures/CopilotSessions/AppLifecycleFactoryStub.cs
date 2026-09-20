namespace Pagurian.Modules.Copilot;

// The source-only fixture never constructs production filesystem/SQLite/process readers.
internal static class CopilotAppLifecycleReader
{
    public static ICopilotAppLifecycleReader Create(string directory, Action<string>? diagnostic) =>
        throw new InvalidOperationException("Inject a fixture lifecycle reader.");
}
