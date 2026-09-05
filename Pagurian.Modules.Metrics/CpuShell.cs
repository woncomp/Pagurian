using Pagurian.Sdk;

namespace Pagurian.Modules.Metrics;

[Shell(DisplayName = "CPU Load")]
public sealed class CpuShell : Shell
{
    public override void Startup()
    {
        MetricsModule.Instance.ShellStarted();
        AddCell<MetricsCell>(
            model: SystemMetricKind.Cpu,
            billboard: () => new CpuBillboard());
    }

    public override void Shutdown() => MetricsModule.Instance.ShellStopped();
}
