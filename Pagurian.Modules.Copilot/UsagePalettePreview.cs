using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pagurian.Modules.Copilot;

// The tooltip is noninteractive: shrink only the decorative palette when the
// viewport is narrow, rather than hiding colors behind an unusable scrollbar.
internal sealed class UsagePalettePreview : Panel
{
    private readonly TextBlock _text;
    private readonly Viewbox _viewbox;

    internal UsagePalettePreview(TextBlock text)
    {
        _text = text;
        _viewbox = new Viewbox { Child = text, Stretch = Stretch.Uniform };
        Children.Add(_viewbox);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = _text.DesiredSize;
        var scale = natural.Width > 0 ? Math.Min(1, availableSize.Width / natural.Width) : 1;
        var size = new Size(natural.Width * scale, natural.Height * scale);
        _viewbox.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewbox.Arrange(new Rect(0, 0,
            Math.Min(finalSize.Width, _viewbox.DesiredSize.Width),
            Math.Min(finalSize.Height, _viewbox.DesiredSize.Height)));
        return finalSize;
    }
}
