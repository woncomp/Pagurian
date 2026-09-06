using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Metrics;

// Memory details billboard: used/total summary and the top processes by
// working set. Re-renders on every sampler snapshot; opening it switches the
// sampler to its fast (1s) cadence.
class MemBillboard : Billboard
{
    private const double BaseHeightDip = 132;
    private const double ProcessRowHeightDip = 32;
    private const double MinHeightDip = 164;
    private const double MaxHeightDip = 560;

    public override double WidthDip => 420;

    public override double HeightDip
    {
        get
        {
            var processCount = Math.Min(
                MetricsSettings.TopProcesses(Shell.Settings),
                SystemMetricsTracker.Memory?.TopProcesses.Count ?? 0);

            return Math.Clamp(
                BaseHeightDip + ProcessRowHeightDip * processCount,
                MinHeightDip,
                MaxHeightDip);
        }
    }

    public override string Title => "Memory";

    public override void OnOpened() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Memory, true);

    public override void OnClosed() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Memory, false);

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();

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

        var highContrast = colorScheme == ColorScheme.HighContrast;
        var requestedTheme = highContrast
            ? ElementTheme.Default
            : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var memory = SystemMetricsTracker.Memory;

        Element scrollContent;
        if (memory == null)
        {
            scrollContent = MaterialCard(
                Body("Data unavailable")
                    .Foreground(ReactorTheme.SecondaryText),
                highContrast);
        }
        else
        {
            var usedGb = memory.UsedBytes / (1024.0 * 1024.0 * 1024.0);
            var totalGb = memory.TotalBytes / (1024.0 * 1024.0 * 1024.0);
            var visibleProcesses = memory.TopProcesses
                .Take(MetricsSettings.TopProcesses(Shell.Settings))
                .ToList();
            var processRows = visibleProcesses
                .Select((process, index) =>
                    ProcessRow(process, index, visibleProcesses.Count))
                .ToArray();

            Element processList = processRows.Length > 0
                ? VStack(8, processRows)
                : Caption("No process data available.")
                    .Foreground(ReactorTheme.SecondaryText);

            var summaryCard = MaterialCard(
                VStack(8,
                    Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        [
                            BodyStrong("Memory Used")
                                .HeadingLevel(AutomationHeadingLevel.Level1)
                                .Grid(row: 0, column: 0),
                            Caption($"{usedGb:F1} GB / {totalGb:F1} GB")
                                .Foreground(ReactorTheme.SecondaryText)
                                .VAlign(VerticalAlignment.Center)
                                .Set(text => Typography.SetNumeralAlignment(
                                    text,
                                    FontNumeralAlignment.Tabular))
                                .Grid(row: 0, column: 1),
                        ]),
                    Progress(memory.UsedPercent)
                        .Height(4)),
                highContrast);

            var processesCard = MaterialCard(
                VStack(8,
                    BodyStrong("Top processes by working set")
                        .HeadingLevel(AutomationHeadingLevel.Level1),
                    processList),
                highContrast);

            scrollContent = VStack(8,
                summaryCard,
                processesCard);
        }

        var content = (ScrollViewer(scrollContent) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch);

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private static Element ProcessRow(
        MemoryProcessUsage process,
        int index,
        int count)
    {
        var workingSetMb = process.WorkingSetBytes / (1024.0 * 1024.0);

        return Border(
                (Grid(
                    [GridSize.Star(), GridSize.Px(48), GridSize.Px(72), GridSize.Px(48)],
                    [GridSize.Auto],
                    [
                        Caption(process.Name)
                            .MaxLines(1)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .ToolTip(process.Name)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        Caption(process.ProcessId.ToString())
                            .Foreground(ReactorTheme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)
                            .Set(text => Typography.SetNumeralAlignment(
                                text,
                                FontNumeralAlignment.Tabular))
                            .Grid(row: 0, column: 1),
                        Caption($"{workingSetMb:F0} MB")
                            .TextAlignment(TextAlignment.Right)
                            .VAlign(VerticalAlignment.Center)
                            .Set(text => Typography.SetNumeralAlignment(
                                text,
                                FontNumeralAlignment.Tabular))
                            .Grid(row: 0, column: 2),
                        Caption($"{process.SystemPercent:F1}%")
                            .TextAlignment(TextAlignment.Right)
                            .VAlign(VerticalAlignment.Center)
                            .Set(text => Typography.SetNumeralAlignment(
                                text,
                                FontNumeralAlignment.Tabular))
                            .Grid(row: 0, column: 3),
                    ]) with
                {
                    ColumnSpacing = 8,
                }))
            .Padding(0, 4)
            .PositionInSet(index + 1, count)
            .WithKey($"process:{process.ProcessId}:{process.Name}");
    }

    private static BorderElement Page(Element content, bool highContrast)
    {
        var page = Border(content)
            .Padding(8)
            .CornerRadius(8);

        return highContrast
            ? page
                .Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
                .WithBorder(ReactorTheme.Ref("SystemColorWindowTextColorBrush"), 2)
                .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge)
            : page;
    }

    private static BorderElement MaterialCard(Element content, bool highContrast) =>
        Border(content)
            .Padding(12)
            .CornerRadius(8)
            .Background(highContrast
                ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.SurfaceStroke,
                highContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
}
