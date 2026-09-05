using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Metrics;

// CPU/memory tray cell: a thin gauge across the top, then two centered lines
// (percent over label) — about as tall as the clock's two lines. The model is
// the SystemMetricKind; the cell works standalone for either kind.
class MetricsCell : ShellCell
{
    private const double FontSizeDip = 12;
    private const double PaddingXDip = 6;

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

        // Re-render on every sampler snapshot and on taskbar theme flips (the
        // gauge colors are computed per render). A monotonic local counter —
        // not the tracker Version — because a theme flip doesn't bump it.
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

        var kind = ModelAs<SystemMetricKind>();
        var snapshot = kind == SystemMetricKind.Cpu
            ? (object?)SystemMetricsTracker.Cpu
            : SystemMetricsTracker.Memory;

        int? percent = null;
        if (snapshot is CpuSnapshot cpu)
            percent = (int)Math.Round(Math.Clamp(cpu.TotalPercent, 0, 100));
        else if (snapshot is MemorySnapshot mem)
            percent = (int)Math.Round(Math.Clamp(mem.UsedPercent, 0, 100));

        var percentText = percent.HasValue ? $"{percent.Value}%" : "--%";
        var isDark = Theme.IsDark;
        var accent = kind == SystemMetricKind.Cpu
            ? SystemMetricsColors.CpuAccent(isDark)
            : SystemMetricsColors.MemoryAccent(isDark);
        var track = SystemMetricsColors.GaugeTrack(isDark);
        // Anti-jitter floor: the percent line is exactly as wide as its
        // worst-case string (measured, so it's still font/locale-correct),
        // so the cell doesn't resize as the live percentage changes.
        var percentWidth = TextMeasurement.MeasureWidth("100%", FontSizeDip);

        return Border(
                FlexColumn(
                    // No explicit width: Yoga stretch spans the cell.
                    Progress(percent ?? 0)
                        .Height(3)
                        .Margin(0, 0, 0, 2)
                        .Set(pb =>
                        {
                            pb.Foreground = new SolidColorBrush(accent);
                            pb.Background = new SolidColorBrush(track);
                        }),
                    TextBlock(percentText)
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Width(percentWidth)
                        .Foreground(Theme.TextBrush),
                    TextBlock(kind == SystemMetricKind.Cpu ? "CPU" : "MEM")
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Foreground(Theme.TextBrush))
                    .VerticalAlignment(VerticalAlignment.Center))
            .Padding(PaddingXDip, 0, PaddingXDip, 0);
    }
}
