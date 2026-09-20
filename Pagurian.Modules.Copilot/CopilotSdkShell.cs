using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

[Shell(
    DisplayName = "Copilot SDK",
    PreviewIcon = "Assets/copilot-app.png",
    ConfigurationView = typeof(CopilotSdkConfiguration))]
public sealed class CopilotSdkShell : Shell
{
    private readonly Dictionary<string, ShellCellHandle> _cells = new();
    private CopilotSdkSessionService? _service;

    public override void Startup()
    {
        _service = CopilotModule.Instance.SdkSessions;
        _service.SessionStarted += OnSessionStarted;
        _service.SessionEnded += OnSessionEnded;
        _service.SessionChanged += OnSessionChanged;
        _service.Acquire(InstanceId, Log, CopilotSdkPollingSettings.Read(Settings));
        foreach (var session in _service.Sessions)
            OnSessionStarted(session);
    }

    public override void Shutdown()
    {
        var service = _service;
        if (service is null)
            return;
        service.SessionStarted -= OnSessionStarted;
        service.SessionEnded -= OnSessionEnded;
        service.SessionChanged -= OnSessionChanged;
        foreach (var session in service.Sessions)
        {
            CopilotStatusColors.Drop(session);
            if (_cells.Remove(session.SessionId, out var cell))
                RemoveCell(cell);
        }
        _service = null;
        service.Release(InstanceId);
    }

    public override void OnMessage(ShellMessage message) =>
        Log.Warn($"ignoring post message with command \"{message.Command}\"");

    public override void OnSettingsChanged() =>
        _service?.Configure(InstanceId, CopilotSdkPollingSettings.Read(Settings));

    private void OnSessionStarted(CopilotSession session)
    {
        if (_cells.ContainsKey(session.SessionId))
            return;
        _cells[session.SessionId] = AddCell<SessionCell>(
            model: session,
            tooltip: () => SdkTooltip(session),
            billboard: () => new SessionBillboard(session));
    }

    private void OnSessionChanged(CopilotSession session)
    {
        if (_cells.ContainsKey(session.SessionId))
            return;
        OnSessionStarted(session);
    }

    private void OnSessionEnded(CopilotSession session)
    {
        CopilotStatusColors.Drop(session);
        if (_cells.Remove(session.SessionId, out var cell))
            RemoveCell(cell);
    }

    private static string SdkTooltip(CopilotSession session)
    {
        var cwd = string.IsNullOrWhiteSpace(session.Cwd)
            ? "Working directory unavailable"
            : session.Cwd;
        var health = session.SdkReadHealth == CopilotSdkReadHealth.Healthy
            ? "SDK persisted-event inference"
            : $"SDK data {session.SdkReadHealth}";
        return $"{session.Name}\n{cwd}\n{health}";
    }
}
