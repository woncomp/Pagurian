using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

// The Copilot activator shell: zero cells while no Copilot CLI session is
// tracked (occupying no tray space), one SessionCell per resolved display owner.
// App task children share their root; independently persisted sessions do not.
// Startup registers the CLI hook file pointing at this shell's persistent id
// and starts the session tracker; hook events arrive via OnMessage through
// the host's post pipeline ("Pagurian.exe post {id} hook <event>").
[Shell(
    DisplayName = "Copilot Sessions",
    PreviewIcon = "Assets/copilot-app.png",
    ConfigurationView = typeof(CopilotConfiguration))]
public sealed class CopilotShell : Shell
{
    private readonly Dictionary<string, ShellCellHandle> _cells = new();

    public override void Startup()
    {
        CopilotSessionTracker.SessionStarted += OnSessionStarted;
        CopilotSessionTracker.SessionEnded += OnSessionEnded;
        CopilotSessionTracker.Start(Log);
        foreach (var session in CopilotSessionTracker.Sessions)
            OnSessionStarted(session);
        CopilotHookInstaller.Install(InstanceId);
    }

    public override void Shutdown()
    {
        CopilotSessionTracker.SessionStarted -= OnSessionStarted;
        CopilotSessionTracker.SessionEnded -= OnSessionEnded;
        CopilotSessionTracker.Stop();
        foreach (var (id, cell) in _cells)
        {
            CopilotStatusColors.Drop(id);
            RemoveCell(cell);
        }
        _cells.Clear();
        CopilotHookInstaller.Uninstall();
    }

    public override void OnMessage(ShellMessage message)
    {
        if (message.Command != "hook" || message.Args.Count == 0)
        {
            Log.Warn($"ignoring post message with command \"{message.Command}\"");
            return;
        }

        HookEventLog.Write(message.Args[0], message.Payload);
        CopilotSessionTracker.HandleHookEvent(message.Args[0], message.Payload, message.ReceivedAt);
    }

    private void OnSessionStarted(CopilotSession session)
    {
        if (_cells.ContainsKey(session.SessionId))
            return;
        _cells[session.SessionId] = AddCell<SessionCell>(
            model: session,
            tooltip: () => $"{session.Name}\n{(string.IsNullOrWhiteSpace(session.Cwd) ? "Working directory unavailable" : session.Cwd)}",
            billboard: () => new SessionBillboard(session));
    }

    private void OnSessionEnded(CopilotSession session)
    {
        CopilotStatusColors.Drop(session.SessionId);
        if (_cells.Remove(session.SessionId, out var cell))
            RemoveCell(cell);
    }
}
