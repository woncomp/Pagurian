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
    private readonly Dictionary<string, CopilotSessionCellModel> _cellModels = new();
    private CopilotSessionIconSize _iconSize;

    public override void Startup()
    {
        _iconSize = CopilotSettings.Clients(Settings).IconSize;
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
        _cellModels.Clear();
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
        var model = new CopilotSessionCellModel(session, _iconSize);
        _cellModels[session.SessionId] = model;
        _cells[session.SessionId] = AddCell<SessionCell>(
            model: model,
            tooltip: () => $"{session.Name}\n{(string.IsNullOrWhiteSpace(session.Cwd) ? "Working directory unavailable" : session.Cwd)}",
            billboard: () => new SessionBillboard(session));
    }

    public override void OnSettingsChanged()
    {
        var next = CopilotSettings.Clients(Settings).IconSize;
        if (next == _iconSize)
            return;
        _iconSize = next;
        foreach (var model in _cellModels.Values)
            model.SetIconSize(next);
    }

    private void OnSessionEnded(CopilotSession session)
    {
        CopilotStatusColors.Drop(session.SessionId);
        _cellModels.Remove(session.SessionId);
        if (_cells.Remove(session.SessionId, out var cell))
            RemoveCell(cell);
    }
}
