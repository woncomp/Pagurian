using Pagurian.Sdk;

namespace Pagurian.Modules.Metrics;

[Shell(
    DisplayName = "Memory Load",
    PreviewIcon = "Assets/mem.png",
    ConfigurationView = typeof(MemConfiguration))]
public sealed class MemShell : Shell
{
    public override void Startup()
    {
        MetricsModule.Instance.ShellStarted();
        AddCell<MetricsCell>(
            model: SystemMetricKind.Memory,
            billboard: () => new MemBillboard());
    }

    public override void OnSettingsChanged() => SystemMetricsTracker.NotifyChanged();

    public override void Shutdown() => MetricsModule.Instance.ShellStopped();
}
