using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;
using XamlText = Microsoft.UI.Xaml.Controls.TextBlock;

namespace Pagurian.Modules.Copilot;

internal sealed record UsagePaceCardProps(CopilotUsagePace Pace, bool Dark, bool HighContrast);

internal sealed class UsagePaceCard : Component<UsagePaceCardProps>
{
    public override Element Render()
    {
        var p = Props;
        return Border(VStack(8,
                BodyStrong("Used Percentage").HeadingLevel(AutomationHeadingLevel.Level2),
                UsageBalanceLabel.Render(p.Pace, p.Dark, p.HighContrast),
                new UsageAxisElement().Set(axis => axis.Update(p.Pace, p.Dark, p.HighContrast))))
            .Padding(12).CornerRadius(8)
            .Background(p.HighContrast ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(p.HighContrast ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                : ReactorTheme.SurfaceStroke, p.HighContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
    }
}

// Native measurement is intentional: both markers use the SAME arranged track.
// Text is measured at the actual width and system text scale in separate lanes.
// No guessed character widths, fixed text heights, or marker nudges at 0/100.
// The wrapper generator infers Panel.Children even with AutoDiscover=false and
// Exclude=Children. This leaf descriptor deliberately leaves native-owned
// children out of reconciliation; otherwise an update removes the axis shapes.
internal sealed record UsageAxisElement : Element
{
    internal Action<UsageAxis>[] Setters { get; init; } = [];
    static UsageAxisElement() => ControlRegistry.Register<UsageAxisElement, UsageAxis>(
        static () => new DescriptorHandler<UsageAxisElement, UsageAxis>(
            new ControlDescriptor<UsageAxisElement, UsageAxis> { GetSetters = static e => e.Setters }));
    internal UsageAxisElement Set(Action<UsageAxis> action) => this with { Setters = [action] };
}

internal sealed class UsageAxis : Panel
{
    internal const double Inset = 8;
    private readonly XamlText _used = Label();
    private readonly XamlText _days = Label();
    private readonly Border _track = new();
    private readonly Border _fill = new();
    private readonly Polygon _bookmark = new()
    {
        Points = new PointCollection { new(0, 0), new(12, 0), new(12, 8), new(6, 14), new(0, 8) },
    };
    private readonly Border _tick = new();
    private CopilotUsagePace? _pace;
    internal double TrackWidth { get; private set; }
    internal double UsedX { get; private set; }
    internal double WorkdayX { get; private set; }
    internal XamlText UsedLabel => _used;
    internal XamlText WorkdayLabel => _days;
    internal FrameworkElement Bookmark => _bookmark;
    internal FrameworkElement Tick => _tick;

    public UsageAxis()
    {
        Children.Add(_track); Children.Add(_fill); Children.Add(_bookmark);
        Children.Add(_tick); Children.Add(_used); Children.Add(_days);
        foreach (var decoration in new UIElement[] { _track, _fill, _bookmark, _tick })
            AutomationProperties.SetAccessibilityView(decoration, AccessibilityView.Raw);
    }

    private static XamlText Label() => new()
    {
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        TextWrapping = TextWrapping.WrapWholeWords,
    };

    internal void Update(CopilotUsagePace pace, bool dark, bool highContrast)
    {
        _pace = pace;
        _used.Text = pace.UsedPercentage is { } used
            ? $"Used {Math.Round(used, MidpointRounding.AwayFromZero):0}%"
            : "Used unavailable";
        _days.Text = pace.Cycle is null ? "Workdays unavailable" : pace.WorkdayLabel;
        var neutral = UsageTint.BrushFor(null, dark, highContrast);
        _used.Foreground = UsageTint.BrushFor(pace.RoundedBalance, dark, highContrast);
        _days.Foreground = neutral;
        _bookmark.Fill = UsageTint.BrushFor(pace.RoundedBalance, dark, highContrast);
        _tick.Background = neutral;
        _fill.Background = UsageTint.BrushFor(6, dark, highContrast);
        _track.Background = highContrast ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
            : UsageTint.SystemBrush("ControlStrokeColorDefaultBrush");
        _track.BorderBrush = neutral;
        _track.BorderThickness = new Thickness(highContrast ? 1 : 0);
        _bookmark.Visibility = pace.UsedPercentage is null ? Visibility.Collapsed : Visibility.Visible;
        _tick.Visibility = pace.WorkdayPercentage is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_used, $"Credits used: {_used.Text}");
        AutomationProperties.SetName(_days, $"Elapsed workdays: {_days.Text}");
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 320;
        _used.Measure(new Size(width, double.PositiveInfinity));
        _days.Measure(new Size(width, double.PositiveInfinity));
        foreach (var shape in new FrameworkElement[] { _track, _fill, _bookmark, _tick })
            shape.Measure(new Size(width, 16));
        return new Size(width, _used.DesiredSize.Height + _days.DesiredSize.Height + 40);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        TrackWidth = Math.Max(0, finalSize.Width - Inset * 2);
        UsedX = CopilotUsageCalendar.MarkerPosition(_pace?.UsedPercentage ?? 0, TrackWidth, Inset);
        WorkdayX = CopilotUsageCalendar.MarkerPosition(_pace?.WorkdayPercentage ?? 0, TrackWidth, Inset);
        double axisY = _used.DesiredSize.Height + 20;
        _track.Arrange(new Rect(Inset, axisY, TrackWidth, 4));
        _fill.Arrange(new Rect(Inset, axisY, Math.Max(0, WorkdayX - Inset), 4));
        _bookmark.Arrange(new Rect(UsedX - 6, axisY - 16, 12, 14));
        _tick.Arrange(new Rect(WorkdayX - 1, axisY + 6, 2, 8));
        ArrangeLabel(_used, UsedX, 0, finalSize.Width);
        ArrangeLabel(_days, WorkdayX, axisY + 20, finalSize.Width);
        return finalSize;
    }

    private static void ArrangeLabel(XamlText label, double marker, double y, double width)
    {
        var labelWidth = Math.Min(label.DesiredSize.Width, width);
        label.Arrange(new Rect(CopilotUsageCalendar.ClampLabel(marker, labelWidth, width),
            y, labelWidth, label.DesiredSize.Height));
    }
}
