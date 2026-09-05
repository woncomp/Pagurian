using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Metrics;

// Memory details billboard: used/total summary and the top processes by
// working set. Re-renders on every sampler snapshot; opening it switches the
// sampler to its fast (1s) cadence.
class MemBillboard : Billboard
{
    private const double ProcessListHeightDip = 140;

    public override double WidthDip => 360;

    public override double HeightDip => 300;

    public override string Title => "Memory";

    public override void OnOpened() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Memory, true);

    public override void OnClosed() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Memory, false);

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

        var mem = SystemMetricsTracker.Memory;
        if (mem == null)
            return TextBlock("Data unavailable")
                .Padding(14);

        double usedGb = mem.UsedBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = mem.TotalBytes / (1024.0 * 1024.0 * 1024.0);
        var summary = $"Used {usedGb:F1} GB / {totalGb:F1} GB ({mem.UsedPercent:F0}%)";

        var topRows = new List<Element>();
        foreach (var p in mem.TopProcesses.Take(
                     MetricsSettings.TopProcesses(Shell.Settings)))
        {
            double wsMb = p.WorkingSetBytes / (1024.0 * 1024.0);
            topRows.Add(Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Px(80), GridSize.Px(56)],
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
                    TextBlock($"{wsMb:F0} MB")
                        .FontSize(12)
                        .TextAlignment(TextAlignment.Right)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                    TextBlock($"{p.SystemPercent:F1}%")
                        .FontSize(12)
                        .TextAlignment(TextAlignment.Right)
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Grid(row: 0, column: 3),
                ]));
        }
        if (topRows.Count == 0)
            topRows.Add(TextBlock("No process data available.").FontSize(12).FontStyle(Windows.UI.Text.FontStyle.Italic));

        return FlexColumn(
                TextBlock("Memory")
                    .FontSize(14)
                    .SemiBold(),
                TextBlock(summary)
                    .FontSize(12)
                    .Margin(0, 4, 0, 0),
                TextBlock("Top processes by working set")
                    .FontSize(12)
                    .SemiBold()
                    .Margin(0, 10, 0, 4),
                ScrollViewer(
                    VStack(2, topRows.ToArray()))
                    .Height(ProcessListHeightDip))
            .Padding(14);
    }
}
