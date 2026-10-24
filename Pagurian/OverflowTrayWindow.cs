using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Top-level overflow presentation. The cells are the same handles/models as the
// main tray, but Reactor creates a second view in this root; interaction rects
// are published only from this window while it is visible.
class OverflowTrayWindow : Component
{
    private readonly SurfaceKey _surface;
    private readonly Func<IReadOnlyList<Pagurian.Sdk.ShellCellHandle>> _getCells;
    private readonly OverflowTrayLayout _layout;
    private readonly ElementRef _scroll = new();

    internal OverflowTrayWindow(OverflowTrayLayout layout, SurfaceKey surface,
        Func<IReadOnlyList<Pagurian.Sdk.ShellCellHandle>> getCells)
    {
        _layout = layout;
        _surface = surface;
        _getCells = getCells;
    }

    internal OverflowTrayLayout Layout => _layout;
    internal event Action? LayoutChanged;

    internal OverflowLayoutSnapshot? Measure(long version, TaskbarInterop.RECT windowBounds, double windowScale)
    {
        var scroll = _scroll.Current as ScrollViewer;
        if (scroll == null) return null;
        return _layout.Measure(version, windowBounds, scroll.ViewportWidth,
            scroll.HorizontalOffset, windowScale);
    }

    internal void ScrollToAnchor()
    {
        if (_scroll.Current is not ScrollViewer scroll) return;
        scroll.UpdateLayout();
        var target = _surface.Edge == TrayEdge.Right ? scroll.ScrollableWidth : 0;
        scroll.ChangeView(target, null, null, true);
    }

    public override Element Render()
    {
        var cells = _getCells().ToArray();
        if (_surface.Edge == TrayEdge.Right)
            Array.Reverse(cells);
        _layout.SetCells(cells);
        var strip = HStack(0, cells.Select(cell =>
            (Element)(Border(new ComponentElement(cell.ViewType, cell.Props))
                with { CornerRadius = TaskbarTrayWindow.HoverCornerRadiusDip })
                .Background(TaskbarTrayWindow.HoverBrushFor(cell.Key))
                .Margin(TaskbarTrayWindow.HoverMarginXDip, TaskbarTrayWindow.HoverMarginYDip,
                    TaskbarTrayWindow.HoverMarginXDip, TaskbarTrayWindow.HoverMarginYDip)
                .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Stretch)
                .Ref(_layout.RefFor(cell.Key))
                .OnMount(_ => _layout.Mounted(cell.Key))
                .OnUnmount(_ => _layout.Unmounted(cell.Key))
                .WithKey(cell.Key)).ToArray())
            .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center);
        return ScrollViewer(strip)
            .Ref(_scroll)
            .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Stretch)
            .Set(scroll =>
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.HorizontalScrollMode = ScrollMode.Auto;
                scroll.VerticalScrollMode = ScrollMode.Disabled;
            })
            .ViewChanged(_ => LayoutChanged?.Invoke());
    }
}
