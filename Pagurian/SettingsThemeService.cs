using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;

namespace Pagurian;

// Theme service for module configuration views hosted inside Settings. Unlike
// ThemeService (which deliberately follows sampled taskbar pixels), this one
// follows the effective Windows theme of the settings surface, including High
// Contrast. This keeps third-party configuration views on the same visual
// surface as the host without changing the public SDK shape.
sealed class SettingsThemeService : IThemeService
{
    private ColorScheme _scheme;
    private string? _highContrastScheme;

    public SettingsThemeService(ColorScheme scheme, string? highContrastScheme)
    {
        _scheme = scheme;
        _highContrastScheme = highContrastScheme;
        IsDark = ResolveIsDark(scheme);
        TextBrush = ThemeResource.Brush("TextFillColorPrimaryBrush");
    }

    public bool IsDark { get; private set; }

    public Brush TextBrush { get; private set; }

    public event Action? Changed;

    public void Apply(ColorScheme scheme, string? highContrastScheme)
    {
        if (scheme == _scheme &&
            string.Equals(highContrastScheme, _highContrastScheme, StringComparison.Ordinal))
        {
            return;
        }

        _scheme = scheme;
        _highContrastScheme = highContrastScheme;
        IsDark = ResolveIsDark(scheme);
        TextBrush = ThemeResource.Brush("TextFillColorPrimaryBrush");
        Changed?.Invoke();
    }

    private static bool ResolveIsDark(ColorScheme scheme)
    {
        if (scheme != ColorScheme.HighContrast)
            return scheme == ColorScheme.Dark;

        if (ThemeResource.Brush("SystemColorWindowColorBrush") is SolidColorBrush brush)
        {
            var color = brush.Color;
            var luminance = (299 * color.R + 587 * color.G + 114 * color.B) / 1000;
            return luminance < 140;
        }

        return false;
    }
}
