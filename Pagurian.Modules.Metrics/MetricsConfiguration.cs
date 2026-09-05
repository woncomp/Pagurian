using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Metrics;

abstract class MetricsConfiguration : ShellConfiguration
{
    protected abstract string MetricName { get; }

    public override Element Render()
    {
        var count = MetricsSettings.TopProcesses(Settings);
        return FlexColumn(
            TextBlock($"Choose how many processes the {MetricName} billboard displays.")
                .FontSize(12)
                .Foreground(Theme.TextBrush),
            NumberBox(
                    count,
                    value =>
                    {
                        if (!double.IsFinite(value))
                            return;
                        SetSettings(MetricsSettings.Write(
                            (int)Math.Round(value, MidpointRounding.AwayFromZero)));
                    },
                    header: "Number of top processes")
                .AutomationName($"{MetricName} top process count")
                .Range(
                    MetricsSettings.MinTopProcesses,
                    MetricsSettings.MaxTopProcesses)
                .SpinButtons()
                .Width(220)
                .HorizontalAlignment(Microsoft.UI.Xaml.HorizontalAlignment.Left)
                .Margin(0, 8, 0, 0));
    }
}

class CpuConfiguration : MetricsConfiguration
{
    protected override string MetricName => "CPU";
}

class MemConfiguration : MetricsConfiguration
{
    protected override string MetricName => "memory";
}
