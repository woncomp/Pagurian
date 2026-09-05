using Microsoft.UI.Xaml.Media;

namespace Pagurian.Sdk;

// The host-derived taskbar theme, shared with modules. The theme is derived
// from sampled taskbar pixels (never from registry/UISettings), so it always
// matches the actual taskbar including translucency and accent tints.
public interface IThemeService
{
    // True when the taskbar is dark.
    bool IsDark { get; }

    // Shared live TextFillColorPrimary brush (white on dark, near-black on
    // light). The host mutates its Color in place on theme flips, so any
    // TextBlock referencing it follows without a re-render.
    Brush TextBrush { get; }

    // Raised on the UI thread when the theme flips. Cells/billboards whose
    // colors are computed per render subscribe in UseEffect and re-render;
    // forgetting to subscribe only leaves that component miscolored.
    event Action? Changed;
}
