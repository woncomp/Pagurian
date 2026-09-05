using Pagurian.Sdk;

namespace Pagurian.Modules.Metrics;

[Shell(DisplayName = "CPU Load", ConfigurationView = typeof(CpuConfiguration))]
public sealed class CpuShell : Shell
{
    public override void Startup()
    {
        MetricsModule.Instance.ShellStarted();
        AddCell<MetricsCell>(
            model: SystemMetricKind.Cpu,
            billboard: () => new CpuBillboard());
    }

    public override void OnSettingsChanged() => SystemMetricsTracker.NotifyChanged();

    public override void Shutdown() => MetricsModule.Instance.ShellStopped();
}
