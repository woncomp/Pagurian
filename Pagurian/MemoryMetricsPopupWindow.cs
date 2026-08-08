using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

class MemoryMetricsPopupWindow : Component
{
    public const double WindowWidthDip = 360;
    public const double WindowHeightDip = 300;

    private const double ProcessListHeightDip = 140;
    private static int _instanceCount;

    private readonly string _key;

    public MemoryMetricsPopupWindow()
    {
        _key = $"pagurian-memory-popup-{++_instanceCount}";
    }

    public WindowSpec CreateSpec((double X, double Y) positionDip) => new()
    {
        Title = "Memory",
        Width = WindowWidthDip,
        Height = WindowHeightDip,
        Style = WindowStyle.None,
        CornerStyle = WindowCornerStyle.Rounded,
        Backdrop = BackdropChoice.Of(BackdropKind.AcrylicThin),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        IsMinimizable = false,
        IsMaximizable = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = positionDip,
        Key = WindowKey.Of(_key),
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);

        UseEffect(() =>
        {
            void OnChanged() => setVersion(SystemMetricsTracker.Version);
            SystemMetricsTracker.UiChanged += OnChanged;
            return () => SystemMetricsTracker.UiChanged -= OnChanged;
        }, Array.Empty<object>());

        var mem = SystemMetricsTracker.Memory;
        if (mem == null)
            return TextBlock("Data unavailable")
                .Padding(14);

        double usedGb = mem.UsedBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = mem.TotalBytes / (1024.0 * 1024.0 * 1024.0);
        var summary = $"Used {usedGb:F1} GB / {totalGb:F1} GB ({mem.UsedPercent:F0}%)";

        var topRows = new List<Element>();
        foreach (var p in mem.TopProcesses)
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
