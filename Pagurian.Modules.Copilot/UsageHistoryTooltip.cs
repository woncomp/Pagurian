using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pagurian.Modules.Copilot;

internal sealed class UsageHistoryTooltip : IDisposable
{
    private static readonly ConditionalWeakTable<Border, UsageHistoryTooltip> Controllers = new();
    private readonly Border _owner;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.WrapWholeWords };
    private Border? _outline;
    private Brush? _hoverBrush;
    private bool _highContrast, _disposed;
    internal ToolTip Tip { get; }

    internal static UsageHistoryTooltip For(Border owner) => Controllers.GetValue(owner, b => new(b));
    internal static void Attach(Border owner, string text, bool dark, bool highContrast) =>
        For(owner).Update(text, dark, highContrast);

    internal static void Detach(FrameworkElement owner)
    {
        if (owner is Border border && Controllers.TryGetValue(border, out var controller))
        {
            controller.Dispose();
            Controllers.Remove(border);
        }
    }

    private UsageHistoryTooltip(Border owner)
    {
        _owner = owner;
        _text.Style = (Style)Application.Current.Resources["BodyTextBlockStyle"];
        Tip = new ToolTip
        {
            Content = _text,
            Placement = PlacementMode.Top,
            IsHitTestVisible = false,
        };
        owner.PointerEntered += PointerEntered;
        owner.PointerExited += PointerExited;
        owner.PointerCanceled += PointerCanceled;
        owner.PointerCaptureLost += PointerCanceled;
        owner.Unloaded += Unloaded;
    }

    private void Update(string text, bool dark, bool highContrast)
    {
        _text.Text = text;
        _text.Foreground = UsageTint.BrushFor(null, dark, highContrast);
        _highContrast = highContrast;
        _hoverBrush = highContrast ? UsageTint.SystemBrush("SystemColorWindowTextColorBrush")
            : UsageBalanceTooltip.ThemeBrush("TextFillColorSecondaryBrush", dark);
        Tip.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Tip.Background = highContrast ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
            : UsageBalanceTooltip.ThemeBrush("SolidBackgroundFillColorBaseBrush", dark);
        Tip.BorderBrush = highContrast ? _hoverBrush
            : UsageBalanceTooltip.ThemeBrush("SurfaceStrokeColorFlyoutBrush", dark);
        Tip.BorderThickness = new Thickness(highContrast ? 2 : 1);
        if (_outline is not null) ShowOutline();
    }

    private bool Contains(Point point) => point.X >= 0 && point.Y >= 0 &&
        point.X < _owner.ActualWidth && point.Y < _owner.ActualHeight;

    private void PointerEntered(object sender, PointerRoutedEventArgs e) => Enter(e.GetCurrentPoint(_owner).Position);
    private void PointerExited(object sender, PointerRoutedEventArgs e) => Exit(e.GetCurrentPoint(_owner).Position);
    private void PointerCanceled(object sender, PointerRoutedEventArgs e) => Cancel();
    private void Unloaded(object sender, RoutedEventArgs e) => Cancel();

    internal void Enter(Point point)
    {
        if (_disposed || !_owner.IsLoaded || !Contains(point)) return;
        // The outline overlays the content so hover never changes column measurement.
        _outline = ((Grid)_owner.Child).Children.OfType<Border>().Single();
        ShowOutline();
        if (Tip.IsOpen) return;
        Tip.XamlRoot = _owner.XamlRoot;
        Tip.PlacementTarget = _owner;
        // Like the balance popup, register only while hovered and open synchronously.
        ToolTipService.SetToolTip(_owner, Tip);
        Tip.IsOpen = true;
    }

    private void ShowOutline()
    {
        _outline!.BorderThickness = new Thickness(_highContrast ? 2 : 1);
        _outline.BorderBrush = _hoverBrush;
    }

    internal void Exit(Point point)
    {
        if (!Contains(point)) Cancel();
    }

    internal void Cancel()
    {
        if (_outline is not null) _outline.BorderBrush = null;
        _outline = null;
        Tip.IsOpen = false;
        ToolTipService.SetToolTip(_owner, null);
        Tip.PlacementTarget = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _owner.PointerEntered -= PointerEntered;
        _owner.PointerExited -= PointerExited;
        _owner.PointerCanceled -= PointerCanceled;
        _owner.PointerCaptureLost -= PointerCanceled;
        _owner.Unloaded -= Unloaded;
        Tip.Content = null;
    }
}
