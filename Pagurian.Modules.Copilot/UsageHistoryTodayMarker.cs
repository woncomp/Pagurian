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

namespace Pagurian.Modules.Copilot;

internal sealed record UsageHistoryTodayMarkerElement : Element
{
    internal Action<UsageHistoryTodayMarker>[] Setters { get; init; } = [];
    static UsageHistoryTodayMarkerElement() =>
        ControlRegistry.Register<UsageHistoryTodayMarkerElement, UsageHistoryTodayMarker>(
            static () => new DescriptorHandler<UsageHistoryTodayMarkerElement, UsageHistoryTodayMarker>(
                new ControlDescriptor<UsageHistoryTodayMarkerElement, UsageHistoryTodayMarker>
                {
                    GetSetters = static element => element.Setters,
                }));
    internal UsageHistoryTodayMarkerElement Set(Action<UsageHistoryTodayMarker> action) =>
        this with { Setters = [action] };
}

internal sealed class UsageHistoryTodayMarker : Panel
{
    internal const double VerticalOffset = 16;

    private readonly TextBlock _label = new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
    };

    internal TextBlock Label => _label;

    public UsageHistoryTodayMarker()
    {
        _label.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_label);
    }

    internal void Update(string date, bool dark, bool highContrast)
    {
        _label.Text = $"Today\n{date}";
        _label.Foreground = UsageTint.BrushFor(null, dark, highContrast);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _label.Arrange(new Rect(finalSize.Width - _label.DesiredSize.Width,
            finalSize.Height - _label.DesiredSize.Height - 8 + VerticalOffset,
            _label.DesiredSize.Width, _label.DesiredSize.Height));
        return finalSize;
    }
}

internal sealed record UsageHistoryTodayArrowElement : Element
{
    internal Action<UsageHistoryTodayArrow>[] Setters { get; init; } = [];
    static UsageHistoryTodayArrowElement() =>
        ControlRegistry.Register<UsageHistoryTodayArrowElement, UsageHistoryTodayArrow>(
            static () => new DescriptorHandler<UsageHistoryTodayArrowElement, UsageHistoryTodayArrow>(
                new ControlDescriptor<UsageHistoryTodayArrowElement, UsageHistoryTodayArrow>
                {
                    GetSetters = static element => element.Setters,
                }));
    internal UsageHistoryTodayArrowElement Set(Action<UsageHistoryTodayArrow> action) =>
        this with { Setters = [action] };
}

internal sealed class UsageHistoryTodayArrow : Panel
{
    internal const double StemHeight = 9;
    internal const double TipHeight = 5;
    private readonly Border _stem = new();
    private readonly Polygon _tip = new()
    {
        Points = new PointCollection { new(0, 0), new(8, 0), new(4, TipHeight) },
    };
    private int _columnCount;

    internal Border Stem => _stem;
    internal Polygon Tip => _tip;

    public UsageHistoryTodayArrow()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_stem);
        Children.Add(_tip);
    }

    internal void Update(int columnCount, bool dark, bool highContrast)
    {
        var brush = UsageTint.BrushFor(null, dark, highContrast);
        _stem.Background = _tip.Fill = brush;
        _columnCount = columnCount;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _stem.Measure(new Size(1, StemHeight));
        _tip.Measure(new Size(8, TipHeight));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var center = finalSize.Width - finalSize.Width / _columnCount / 2;
        _stem.Arrange(new Rect(center - .5, -StemHeight - TipHeight, 1, StemHeight));
        _tip.Arrange(new Rect(center - 4, -TipHeight, 8, TipHeight));
        return finalSize;
    }
}
