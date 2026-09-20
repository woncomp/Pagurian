namespace Pagurian.Modules.Copilot;

// Presentation settings are shell-instance-owned. Session state stays shared
// between tracker consumers while this wrapper allows one shell to rerender
// without changing another shell's presentation.
sealed class CopilotSessionCellModel
{
    public CopilotSessionCellModel(CopilotSession session, CopilotSessionIconSize iconSize)
    {
        Session = session;
        IconSize = iconSize;
    }

    public CopilotSession Session { get; }

    public CopilotSessionIconSize IconSize { get; private set; }

    public event Action? Changed;

    public void SetIconSize(CopilotSessionIconSize iconSize)
    {
        if (IconSize == iconSize)
            return;
        IconSize = iconSize;
        Changed?.Invoke();
    }
}
