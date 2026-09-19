using Microsoft.UI.Xaml.Media;

namespace Pagurian.Sdk;

// The host-derived theme for the surface currently rendering module UI.
// Shells, cells, and billboards receive the taskbar-derived theme; shell
// configuration views receive their editor's effective Windows theme.
public interface IThemeService
{
    // True when the current host surface is dark.
    bool IsDark { get; }

    // Primary text brush for the current host surface.
    Brush TextBrush { get; }

    // Raised on the UI thread when the host surface's effective theme changes.
    event Action? Changed;
}
