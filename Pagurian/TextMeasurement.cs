using Microsoft.UI.Xaml;

namespace Pagurian;

// Measures text width in DIPs with a scratch WinUI TextBlock (Reactor exposes
// no measure API — its FlexPanel measurement is internal). A detached
// TextBlock measures fine off the visual tree as long as XAML is up; all
// callers (Render, the controller's poll tick, tooltip creation) run on the
// UI thread, so the scratch instance and the cache need no locking.
static class TextMeasurement
{
    private static Microsoft.UI.Xaml.Controls.TextBlock? _scratch;
    private static readonly Dictionary<(string Text, double FontSize), double> _cache = new();

    // Width of `text` rendered at `fontSize` with the default font family, in
    // DIPs (DesiredSize is already DPI-independent, matching the app's
    // DIP-based design sizes).
    public static double MeasureWidth(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        if (_cache.TryGetValue((text, fontSize), out var cached))
            return cached;

        double width;
        try
        {
            // CreateSpec can run before the XAML application object exists;
            // fall back to a rough estimate rather than crash startup.
            if (Application.Current == null)
            {
                width = FallbackWidth(text, fontSize);
            }
            else
            {
                _scratch ??= new Microsoft.UI.Xaml.Controls.TextBlock();
                _scratch.Text = text;
                _scratch.FontSize = fontSize;
                _scratch.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                width = _scratch.DesiredSize.Width;
                if (width <= 0)
                    width = FallbackWidth(text, fontSize); // detached measure failed
            }
        }
        catch
        {
            width = FallbackWidth(text, fontSize);
        }

        _cache[(text, fontSize)] = width;
        return width;
    }

    // Rough estimate (~7 DIPs per char at 12pt, scaled): only used when real
    // measurement is unavailable, never cached as authoritative.
    private static double FallbackWidth(string text, double fontSize) =>
        text.Length * 7.0 * (fontSize / 12.0);
}
