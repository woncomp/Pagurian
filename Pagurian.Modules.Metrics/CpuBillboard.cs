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
    public override double WidthDip => 400;

    public override double HeightDip => 440;

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
            var processorRows = cpu.PerLogicalProcessorPercent
                .Select((percent, index) =>
                    ProcessorRow(
                        index,
                        percent,
                        cpu.PerLogicalProcessorPercent.Count))
                .ToArray();

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
                            VStack(4,
                                    Caption("Overall CPU")
                                        .Foreground(ReactorTheme.SecondaryText),
                                    TextBlock($"{cpu.TotalPercent:F1}%")
                                        .ApplyStyle("TitleTextBlockStyle")
                                        .Set(text => Typography.SetNumeralAlignment(
                                            text,
                                            FontNumeralAlignment.Tabular)))
                                .Grid(row: 0, column: 0),
                            Caption($"{cpu.PerLogicalProcessorPercent.Count} logical processors")
                                .Foreground(ReactorTheme.SecondaryText)
                                .VAlign(VerticalAlignment.Bottom)
                                .Grid(row: 0, column: 1),
                        ]),
                    Progress(cpu.TotalPercent)
                        .Height(4)),
                highContrast);

            var processorsCard = MaterialCard(
                VStack(8,
                    BodyStrong("Logical processors")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    VStack(8, processorRows)),
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

        var content = FlexColumn(
            VStack(4,
                Subtitle("CPU")
                    .HeadingLevel(AutomationHeadingLevel.Level1),
                Caption("Live system load")
                    .Foreground(ReactorTheme.SecondaryText)),
            (ScrollViewer(scrollContent) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
                .Margin(0, 8, 0, 0)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Flex(1));

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private static Element ProcessorRow(int index, double percent, int count) =>
        Border(
                Grid(
                    [GridSize.Px(56), GridSize.Star(), GridSize.Px(48)],
                    [GridSize.Auto],
                    [
                        Caption($"CPU {index}")
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        Progress(percent)
                            .Height(4)
                            .Margin(0, 0, 8, 0)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 1),
                        Caption($"{percent:F0}%")
                            .TextAlignment(TextAlignment.Right)
                            .VAlign(VerticalAlignment.Center)
                            .Set(text => Typography.SetNumeralAlignment(
                                text,
                                FontNumeralAlignment.Tabular))
                            .Grid(row: 0, column: 2),
                    ]))
            .Padding(0, 4)
            .PositionInSet(index + 1, count)
            .WithKey($"processor:{index}");

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
