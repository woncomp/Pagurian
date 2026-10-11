namespace Pagurian.Modules.Copilot;

internal sealed class CopilotSessionCellVisibility
{
    private readonly Dictionary<string, (CopilotSession Session, long EventRevision)> _dismissed =
        new(StringComparer.Ordinal);

    public IReadOnlyList<CopilotSession> DismissedSessions =>
        _dismissed.Values.Select(entry => entry.Session).ToArray();

    public void Dismiss(CopilotSession session) =>
        _dismissed[session.SessionId] = (session, session.EventRevision);

    public bool ShouldShow(CopilotSession session)
    {
        if (!_dismissed.TryGetValue(session.SessionId, out var entry))
            return true;
        if (ReferenceEquals(entry.Session, session) && session.EventRevision <= entry.EventRevision)
            return false;
        _dismissed.Remove(session.SessionId);
        return true;
    }

    public void Forget(CopilotSession session)
    {
        if (_dismissed.TryGetValue(session.SessionId, out var entry) &&
            ReferenceEquals(entry.Session, session))
            _dismissed.Remove(session.SessionId);
    }

    public void Clear() => _dismissed.Clear();
}
