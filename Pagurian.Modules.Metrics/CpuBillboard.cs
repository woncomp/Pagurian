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

// CPU details billboard: overall load, per-logical-processor gauges, and the
// top processes. Re-renders on every sampler snapshot, so it stays live
// while open. Opening it switches the sampler to its fast (1s) cadence.
class CpuBillboard : Billboard
{
    private const int ProcessorColumns = 8;
    private const double BaseHeightDip = 184;
    private const double ProcessorGridRowHeightDip = 40;
    private const double ProcessRowHeightDip = 32;
    private const double MinHeightDip = 176;
    private const double MaxHeightDip = 640;

    public override double WidthDip => 400;

    public override double HeightDip
    {
        get
        {
            var cpu = SystemMetricsTracker.Cpu;
            var processorCount = cpu?.PerLogicalProcessorPercent.Count ?? 0;
            var processorRowCount =
                (processorCount + ProcessorColumns - 1) / ProcessorColumns;
            var processCount = Math.Min(
                MetricsSettings.TopProcesses(Shell.Settings),
                cpu?.TopProcesses.Count ?? 0);

            return Math.Clamp(
                BaseHeightDip +
                    ProcessorGridRowHeightDip * processorRowCount +
                    ProcessRowHeightDip * processCount,
                MinHeightDip,
                MaxHeightDip);
        }
    }

    public override string Title => "CPU";

    public override void OnOpened() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Cpu, true);

    public override void OnClosed() => SystemMetricsTracker.SetPopupVisible(SystemMetricKind.Cpu, false);

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
        var cpu = SystemMetricsTracker.Cpu;

        Element scrollContent;
        if (cpu == null)
        {
            scrollContent = MaterialCard(
                Body("Data unavailable")
                    .Foreground(ReactorTheme.SecondaryText),
                highContrast);
        }
        else
        {
            var processorGauges = cpu.PerLogicalProcessorPercent
                .Select((percent, index) =>
                    ProcessorGauge(
                        index,
                        percent,
                        cpu.PerLogicalProcessorPercent.Count))
                .ToArray();
            var processorColumnCount = Math.Min(
                ProcessorColumns,
                Math.Max(1, processorGauges.Length));
            var processorRowCount =
                (processorGauges.Length + ProcessorColumns - 1) /
                ProcessorColumns;

            Element processorGrid = processorGauges.Length > 0
                ? Grid(
                    Enumerable.Repeat(
                            GridSize.Star(),
                            processorColumnCount)
                        .ToArray(),
                    Enumerable.Repeat(
                            GridSize.Auto,
                            processorRowCount)
                        .ToArray(),
                    processorGauges) with
                    {
                        ColumnSpacing = 8,
                        RowSpacing = 8,
                    }
                : Caption("No processor data available.")
                    .Foreground(ReactorTheme.SecondaryText);

            var visibleProcesses = cpu.TopProcesses
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
                            BodyStrong("Overall CPU")
                                .HeadingLevel(AutomationHeadingLevel.Level1)
                                .Grid(row: 0, column: 0),
                            Caption(
                                    $"{cpu.TotalPercent:F1}% · " +
                                    $"{cpu.PerLogicalProcessorPercent.Count} logical processors")
                                .Foreground(ReactorTheme.SecondaryText)
                                .VAlign(VerticalAlignment.Center)
                                .Set(text => Typography.SetNumeralAlignment(
                                    text,
                                    FontNumeralAlignment.Tabular))
                                .Grid(row: 0, column: 1),
                        ]),
                    Progress(cpu.TotalPercent)
                        .Height(4)),
                highContrast);

            var processorsCard = MaterialCard(
                VStack(8,
                    BodyStrong("Logical processors")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    processorGrid),
                highContrast);

            var processesCard = MaterialCard(
                VStack(8,
                    BodyStrong("Top processes")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    processList),
                highContrast);

            scrollContent = VStack(8,
                summaryCard,
                processorsCard,
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

    private static Element ProcessorGauge(int index, double percent, int count) =>
        Border(
                VStack(4,
                    Progress(percent)
                        .Height(4),
                    Caption($"{percent:F0}%")
                        .TextAlignment(TextAlignment.Center)
                        .Set(text => Typography.SetNumeralAlignment(
                            text,
                            FontNumeralAlignment.Tabular))))
            .Padding(0, 4)
            .ToolTip($"CPU {index}")
            .PositionInSet(index + 1, count)
            .WithKey($"processor:{index}")
            .Grid(
                row: index / ProcessorColumns,
                column: index % ProcessorColumns);

    private static Element ProcessRow(
        CpuProcessUsage process,
        int index,
        int count) =>
        Border(
                Grid(
                    [GridSize.Star(), GridSize.Px(56), GridSize.Px(72)],
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
                        Caption($"{process.Percent:F1}%")
                            .TextAlignment(TextAlignment.Right)
                            .VAlign(VerticalAlignment.Center)
                            .Set(text => Typography.SetNumeralAlignment(
                                text,
                                FontNumeralAlignment.Tabular))
                            .Grid(row: 0, column: 2),
                    ]))
            .Padding(0, 4)
            .PositionInSet(index + 1, count)
            .WithKey($"process:{process.ProcessId}:{process.Name}");

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
