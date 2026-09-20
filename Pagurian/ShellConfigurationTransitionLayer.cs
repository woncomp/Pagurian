using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using XamlBorder = Microsoft.UI.Xaml.Controls.Border;

namespace Pagurian;

internal sealed record ShellConfigurationTransitionLayerProps(
    ShellConfigurationVisit Visit,
    ShellConfigurationTransitionState State,
    ShellNavigationDiagnostics Diagnostics,
    Element Content,
    bool HighContrast);

internal sealed class ShellConfigurationTransitionLayer :
    Component<ShellConfigurationTransitionLayerProps>
{
    public override Element Render()
    {
        var current = Props.State.IsCurrent(Props.Visit.VisitId);
        return Border(
                ScrollView(Border(Props.Content).Padding(24))
                    .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                    .IsEnabled(true))
            .Background(Props.HighContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.SolidBackground)
            .Set(element => element.IsHitTestVisible = current)
            .OnMountAdd(Mount)
            .OnUnmountAdd(Unmount);
    }

    private void Mount(FrameworkElement element)
    {
        ApplyBackground(element);
        element.ActualThemeChanged += OnActualThemeChanged;
        Props.State.Mount(Props.Visit.VisitId, element);
        Props.Diagnostics.MountPage(
            element,
            ShellNavigationDiagnostics.ConfigurationRoute(Props.Visit.Entry.Id));
    }

    private void Unmount(FrameworkElement element)
    {
        element.ActualThemeChanged -= OnActualThemeChanged;
        Props.State.Unmount(Props.Visit, element);
        Props.Diagnostics.Unmount(element);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyBackground(sender);

    private void ApplyBackground(FrameworkElement element)
    {
        if (element is XamlBorder border)
        {
            border.Background = ThemeResource.Brush(
                Props.HighContrast
                    ? "SystemColorWindowColorBrush"
                    : "SolidBackgroundFillColorBaseBrush");
        }
    }
}
