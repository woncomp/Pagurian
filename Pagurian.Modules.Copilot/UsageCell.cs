using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

class UsageCell : ShellCell
{
    public override Element Render()
    {
        var model = ModelAs<CopilotUsageModel>();
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var scheme = UseColorScheme();
        UseEffect(() =>
        {
            void Changed() => setVersion(++tick.Current);
            model.Changed += Changed;
            Theme.Changed += Changed;
            return () => { model.Changed -= Changed; Theme.Changed -= Changed; };
        }, model, Theme);
        var state = model.State;
        var category = state.Usage.PremiumInteractions;
        var percent = state.Status == CopilotUsageStatus.Ready && !state.IsLoggingIn &&
            category is { IsUnlimited: false, UsedPercentage: { } value } ? Math.Clamp(value, 0, 100) : (double?)null;
        var highContrast = scheme == ColorScheme.HighContrast;
        var tint = UsageTint.BrushFor(model.Pace.RoundedBalance, Theme.IsDark, highContrast);
        var percentageText = percent is not null ? category!.PercentageText : "--";
        return Border(FlexColumn(
                Progress(percent ?? 0).Height(3).Margin(0, 0, 0, 2)
                    .Set(bar =>
                    {
                        // Top gauge continues to mean Credits used, never pace.
                        bar.Foreground = UsageTint.BrushFor(6, Theme.IsDark, highContrast);
                        bar.Background = highContrast ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
                            : new SolidColorBrush(Theme.IsDark
                                ? Windows.UI.Color.FromArgb(255, 72, 72, 72)
                                : Windows.UI.Color.FromArgb(255, 214, 214, 214));
                    }),
                new UsagePulseElement().Set(pulse => pulse.Tint = tint).AccessibilityHidden(),
                TextBlock(percentageText)
                    .FontSize(12).TextAlignment(TextAlignment.Center)
                    .Foreground(highContrast ? tint : Theme.TextBrush))
                .VerticalAlignment(VerticalAlignment.Center))
            .Padding(6, 0, 6, 0)
            .AutomationName($"Credits used {percentageText}. {model.Pace.BalanceText}");
    }
}

// Native-owned BitmapIcon, not a declarative panel children slot.
internal sealed record UsagePulseElement : Element
{
    internal Action<UsagePulse>[] Setters { get; init; } = [];
    static UsagePulseElement() => ControlRegistry.Register<UsagePulseElement, UsagePulse>(
        static () => new DescriptorHandler<UsagePulseElement, UsagePulse>(
            new ControlDescriptor<UsagePulseElement, UsagePulse> { GetSetters = static e => e.Setters }));
    internal UsagePulseElement Set(Action<UsagePulse> action) => this with { Setters = [action] };
}

// Original asset is 50x50, alpha bounds [5,45) x [12,36). The monochrome
// BitmapIcon retains every antialiased source pixel. Crop the transparent
// padding geometrically, not by replacing the silhouette or modifying the PNG.
internal sealed class UsagePulse : Panel
{
    private readonly BitmapIcon _icon = new()
    {
        UriSource = new Uri(CopilotModule.UsageIconPath),
        ShowAsMonochrome = true,
    };
    private readonly Microsoft.UI.Xaml.Controls.TextBlock _lineMeasure = new() { Text = "100%", FontSize = 12 };
    public UsagePulse() { Children.Add(_icon); }
    internal Brush Tint { set => _icon.Foreground = value; }
    internal bool Compact { get; set; }
    internal Rect VisibleBounds { get; private set; }
    protected override Size MeasureOverride(Size availableSize)
    {
        _lineMeasure.Style = Compact ? (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] : null;
        if (Compact) _lineMeasure.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.FontSizeProperty);
        else _lineMeasure.FontSize = 12;
        _lineMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Compact ? _lineMeasure.DesiredSize.Height : Math.Max(32, _lineMeasure.DesiredSize.Width);
        var height = _lineMeasure.DesiredSize.Height;
        _icon.Measure(new Size(width * 50 / 40, height * 50 / 24));
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var scale = Math.Min(finalSize.Width / 40, finalSize.Height / 24);
        var left = (finalSize.Width - 40 * scale) / 2;
        var top = (finalSize.Height - 24 * scale) / 2;
        _icon.Arrange(new Rect(left - 5 * scale, top - 12 * scale, 50 * scale, 50 * scale));
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) };
        VisibleBounds = new Rect(left, top, 40 * scale, 24 * scale);
        return finalSize;
    }
}
