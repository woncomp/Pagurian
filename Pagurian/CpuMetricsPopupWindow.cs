using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

class CpuMetricsPopupWindow : Component
{
    public const double WindowWidthDip = 360;
    public const double WindowHeightDip = 320;

    private const double ProcessorListHeightDip = 160;
    private static int _instanceCount;

    private readonly string _key;

    public CpuMetricsPopupWindow()
    {
        _key = $"pagurian-cpu-popup-{++_instanceCount}";
    }

    public WindowSpec CreateSpec((double X, double Y) positionDip) => new()
    {
        Title = "CPU",
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

        var cpu = SystemMetricsTracker.Cpu;
        if (cpu == null)
            return TextBlock("Data unavailable")
                .Padding(14);

        var isDark = TaskbarController.IsDarkTheme;
        var accent = SystemMetricsColors.CpuAccent(isDark);
        var track = SystemMetricsColors.GaugeTrack(isDark);
        var accentBrush = new SolidColorBrush(accent);
        var trackBrush = new SolidColorBrush(track);

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
        foreach (var p in cpu.TopProcesses)
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
                VStack(2, topRows.ToArray()))
            .Padding(14);
    }
}
