using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using XamlText = Microsoft.UI.Xaml.Controls.TextBlock;

namespace Pagurian.Modules.Copilot;

internal sealed record UsageHistoryDateMarkerElement : Element
{
    internal Action<UsageHistoryDateMarker>[] Setters { get; init; } = [];
    static UsageHistoryDateMarkerElement() =>
        ControlRegistry.Register<UsageHistoryDateMarkerElement, UsageHistoryDateMarker>(
            static () => new DescriptorHandler<UsageHistoryDateMarkerElement, UsageHistoryDateMarker>(
                new ControlDescriptor<UsageHistoryDateMarkerElement, UsageHistoryDateMarker>
                {
                    GetSetters = static element => element.Setters,
                }));
    internal UsageHistoryDateMarkerElement Set(Action<UsageHistoryDateMarker> action) =>
        this with { Setters = [action] };
}

internal sealed class UsageHistoryDateMarker : Panel
{
    private readonly Border _tick = new();
    private readonly XamlText _label = new()
    {
        TextAlignment = TextAlignment.Center,
        IsHitTestVisible = false,
    };
    private int _column;
    internal XamlText Label => _label;
    internal Border Tick => _tick;

    public UsageHistoryDateMarker()
    {
        _label.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_tick);
        Children.Add(_label);
    }

    internal void Update(string text, int column, bool dark, bool highContrast)
    {
        _column = column;
        _label.Text = text;
        _tick.Background = UsageTint.BrushFor(null, dark, highContrast);
        _tick.Visibility = _label.Visibility =
            text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _tick.Measure(new Size(2, 5));
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(18, Math.Max(26, _label.DesiredSize.Height + 6));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _tick.Arrange(new Rect((finalSize.Width - 2) / 2, 0, 2, 5));
        var width = _label.DesiredSize.Width;
        var left = _column switch
        {
            0 => 0,
            CopilotUsageHistoryCalculator.DaysPerPage - 1 => finalSize.Width - width,
            _ => (finalSize.Width - width) / 2,
        };
        _label.Arrange(new Rect(left, 6, width, _label.DesiredSize.Height));
        return finalSize;
    }
}
