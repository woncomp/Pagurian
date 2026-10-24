using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

// Presentation settings are shell-instance-owned. Session state stays shared
// between tracker consumers while this wrapper allows one shell to rerender
// without changing another shell's presentation. Simple mode is part of that
// presentation; the shared session only contributes status/inactivity evidence.
sealed class CopilotSessionCellModel : IShellCellSimpleMode, IShellCellSimpleModePolicy
{
    private bool _simple;

    public CopilotSessionCellModel(CopilotSession session, CopilotSessionIconSize iconSize)
    {
        Session = session;
        IconSize = iconSize;
    }

    public CopilotSession Session { get; }

    public CopilotSessionIconSize IconSize { get; private set; }

    public bool IsSimpleMode => _simple;

    // Only inactive aggregates compact: compact presentation has no status
    // surface, so Working/Blocked/Unknown must stay in their full cell.
    public bool CanUseSimpleMode => Session.Status is CopilotSessionStatus.Idle or CopilotSessionStatus.Done;

    public long SimpleModePriority => Session.InactiveSince?.UtcTicks ?? long.MaxValue;

    public event Action? Changed;

    public void SetIconSize(CopilotSessionIconSize iconSize)
    {
        if (IconSize == iconSize)
            return;
        IconSize = iconSize;
        Changed?.Invoke();
    }

    public void SetSimpleMode(bool simple)
    {
        if (simple && !CanUseSimpleMode)
            return;
        if (_simple == simple)
            return;
        _simple = simple;
        Changed?.Invoke();
    }

    // Called from the shell's tracker change handler before cells observe the
    // same notification, so a session that resumed work never renders compact.
    internal void EnforceSimpleModeEligibility()
    {
        if (!CanUseSimpleMode)
            SetSimpleMode(false);
    }
}
