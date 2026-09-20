using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

[Shell(
    DisplayName = "Copilot Usage",
    PreviewIcon = "Assets/icons8-pulse-50.png",
    ConfigurationView = typeof(UsageConfiguration))]
public sealed class UsageShell : Shell
{
    private ShellCellHandle? _cell;
    private CopilotUsageModel? _model;

    public override void Startup()
    {
        var usage = CopilotModule.Instance.Usage;
        var model = _model = new CopilotUsageModel(usage, Settings, Log.Warn,
            callback => Microsoft.UI.Reactor.ReactorApp.UIDispatcher?.TryEnqueue(() => callback()));
        _cell = AddCell<UsageCell>(
            model: model,
            tooltip: () => "GitHub Copilot Usage",
            billboard: () => new UsageBillboard(model));
        usage.Refresh();
    }

    public override void Shutdown()
    {
        _model?.Dispose();
        _model = null;
        if (_cell is not null)
        {
            RemoveCell(_cell);
            _cell = null;
        }
    }

    public override void OnSettingsChanged() => _model?.ApplySettings(Settings);
}
