using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

[Shell(
    DisplayName = "Copilot Usage",
    PreviewIcon = "Assets/icons8-pulse-50.png",
    ConfigurationView = typeof(UsageConfiguration))]
public sealed class UsageShell : Shell
{
    private ShellCellHandle? _cell;

    public override void Startup()
    {
        var usage = CopilotModule.Instance.Usage;
        _cell = AddCell<UsageCell>(
            model: usage,
            tooltip: () => "GitHub Copilot Usage",
            billboard: () => new UsageBillboard(usage));
        usage.Refresh();
    }

    public override void Shutdown()
    {
        if (_cell is not null)
        {
            RemoveCell(_cell);
            _cell = null;
        }
    }
}
