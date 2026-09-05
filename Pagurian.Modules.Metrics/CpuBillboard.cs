using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Metrics;

// CPU details billboard: overall load, per-logical-processor gauges, and the
// top processes. Re-renders on every sampler snapshot, so it stays live
// while open. Opening it switches the sampler to its fast (1s) cadence.
class CpuBillboard : Billboard
{
    private const double ProcessorListHeightDip = 160;

    public override double WidthDip => 360;

    public override double HeightDip => 320;

    public override string Title => "CPU";

    public override void OnOpened() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Cpu, true);

    public override void OnClosed() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Cpu, false);

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            SystemMetricsTracker.UiChanged += OnChanged;
            Theme.Changed += OnChanged;
            return () =>
            {
                SystemMetricsTracker.UiChanged -= OnChanged;
                Theme.Changed -= OnChanged;
            };
        }, Array.Empty<object>());

        var cpu = SystemMetricsTracker.Cpu;
        if (cpu == null)
            return TextBlock("Data unavailable")
                .Padding(14);

        var isDark = Theme.IsDark;
        var accentBrush = new SolidColorBrush(SystemMetricsColors.CpuAccent(isDark));
        var trackBrush = new SolidColorBrush(SystemMetricsColors.GaugeTrack(isDark));

        var processorRows = new List<Element>();
        for (int i = 0; i < cpu.PerLogicalProcessorPercent.Count; i++)
        {
            var pct = cpu.PerLogicalProcessorPercent[i];
            processorRows.Add(Grid(
                [GridSize.Px(52), GridSize.Star(), GridSize.Px(44)],
                [GridSize.Auto],
                [
                    TextBlock($"CPU {i}")
                        .FontSize(12)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    Progress(pct)
                        .Height(4)
                        .Margin(0, 0, 8, 0)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Set(pb =>
                        {
                            pb.Foreground = accentBrush;
                            pb.Background = trackBrush;
                        })
                        .Grid(row: 0, column: 1),
                    TextBlock($"{pct:F0}%")
                        .FontSize(12)
                        .TextAlignment(TextAlignment.Right)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                ]));
        }

        var topRows = new List<Element>();
        foreach (var p in cpu.TopProcesses.Take(
                     MetricsSettings.TopProcesses(Shell.Settings)))
        {
            topRows.Add(Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Px(64)],
                [GridSize.Auto],
                [
                    TextBlock(p.Name)
                        .FontSize(12)
                        .MaxLines(1)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    TextBlock(p.ProcessId.ToString())
                        .FontSize(12)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1),
                    TextBlock($"{p.Percent:F1}%")
                        .FontSize(12)
                        .TextAlignment(TextAlignment.Right)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                ]));
        }
        if (topRows.Count == 0)
            topRows.Add(TextBlock("No process data available.").FontSize(12).FontStyle(Windows.UI.Text.FontStyle.Italic));

        return FlexColumn(
                TextBlock($"Overall CPU: {cpu.TotalPercent:F1}%")
                    .FontSize(14)
                    .SemiBold(),
                TextBlock("Logical processors")
                    .FontSize(12)
                    .SemiBold()
                    .Margin(0, 10, 0, 4),
                ScrollViewer(
                    VStack(2, processorRows.ToArray()))
                    .Height(ProcessorListHeightDip),
                TextBlock("Top processes")
                    .FontSize(12)
                    .SemiBold()
                    .Margin(0, 10, 0, 4),
                ScrollViewer(VStack(2, topRows.ToArray()))
                    .Flex(1))
            .Padding(14);
    }
}
