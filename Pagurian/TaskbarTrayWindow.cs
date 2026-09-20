using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Borderless window anchored to one display's taskbar: a Tray surface, a
// horizontal container of the shell cells TrayManager assigns to it. All
// cell content comes from the bound trays; the window itself only supplies
// the per-cell chrome — the native rounded translucent hover overlay, the
// hover margins, and the width read-back Ref — while the session supplies
// the sampled taskbar-color background gradient that blends the tray into
// the taskbar. A surface with zero cells occupies no space.
class TaskbarTrayWindow : Component
{
    // Design sizes in DIPs; the session scales to physical pixels.
    // WindowHeightDip is the full taskbar thickness (the session derives
    // the DPI scale from it); the widget itself is inset WindowInsetYDip
    // from the taskbar's top and bottom edges so it sits slightly inside.
    // Cell widths are measured naturally by the session before committing
    // a shared viewport/window/hit-test layout snapshot.
    public const double WindowHeightDip = 48;
    public const double WindowInsetYDip = 2;
    public const double ContentHeightDip = WindowHeightDip - 2 * WindowInsetYDip;

    // Native cell hover visual: a SubtleFill overlay inset from the cell
    // edges with the control corner radius (like every Win11 taskbar button).
    public const double HoverMarginXDip = 3;
    public const double HoverMarginYDip = 4;
    public const double HoverCornerRadiusDip = 4;

    private readonly TaskbarTrayLayout _layout;
    private readonly SurfaceKey _surface;

    internal TaskbarTrayWindow(TaskbarTrayLayout layout)
        : this(layout, TrayManager.DefaultSurfaceKey) { }

    internal TaskbarTrayWindow(TaskbarTrayLayout layout, SurfaceKey surface)
    {
        _layout = layout;
        _surface = surface;
    }

    public static WindowSpec CreateSpec(WindowKey? key = null) => new()
    {
        Title = "Pagurian",
        Width = 1,
        Height = ContentHeightDip,
        ActivateOnOpen = false,
        SizeToContent = WindowSizeToContent.Manual,
        Style = WindowStyle.None,
        Backdrop = BackdropChoice.Of(BackdropKind.Transparent),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        IsMinimizable = false,
        IsMaximizable = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (0, 0),
        Key = key ?? WindowKey.Of("pagurian-icon"),
        Icon = WindowIcon.FromPath(AppAssets.ApplicationIconPath),
    };

    // Number of stops in the taskbar-color gradient: sampled across the
    // tray's width using the session's spatial background cache.
    public const int GradientStopCount = 5;

    // Per-cell hover overlays: one live brush per cell key, mutated in place
    // by the controller's poll loop (no re-render) — the native
    // SubtleFillColorSecondary (hover) / SubtleFillColorTertiary (pressed)
    // feedback, alpha-blended over the sampled base color. Cell keys are
    // globally unique across surfaces, so the table is shared; entries are
    // reference-counted because a cell briefly lives in two windows while a
    // rebinding moves it between surfaces.
    private static readonly Dictionary<string, (SolidColorBrush Brush, int Refs)> _hoverBrushes = new();

    public static SolidColorBrush HoverBrushFor(string cellKey)
    {
        if (!_hoverBrushes.TryGetValue(cellKey, out var entry))
        {
            entry = (new SolidColorBrush(HoverOverlayHidden), 0);
            _hoverBrushes[cellKey] = entry;
        }
        return entry.Brush;
    }

    internal static void BrushMounted(string cellKey)
    {
        var entry = _hoverBrushes.TryGetValue(cellKey, out var existing)
            ? existing
            : (Brush: new SolidColorBrush(HoverOverlayHidden), Refs: 0);
        _hoverBrushes[cellKey] = (entry.Brush, entry.Refs + 1);
    }

    internal static void BrushUnmounted(string cellKey)
    {
        if (!_hoverBrushes.TryGetValue(cellKey, out var entry))
            return;
        if (entry.Refs <= 1)
            _hoverBrushes.Remove(cellKey);
        else
            _hoverBrushes[cellKey] = (entry.Brush, entry.Refs - 1);
    }

    public static readonly Windows.UI.Color HoverOverlayHidden = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    // SubtleFillColorSecondary (hover) / SubtleFillColorTertiary (pressed):
    // ~6%/~4% white on dark taskbars, ~4%/~2.5% black on light ones.
    public static Windows.UI.Color HoverOverlayColorFor(bool isDark, bool pressed) =>
        isDark
            ? (pressed
                ? Windows.UI.Color.FromArgb(0x0A, 255, 255, 255)
                : Windows.UI.Color.FromArgb(0x0F, 255, 255, 255))
            : (pressed
                ? Windows.UI.Color.FromArgb(0x06, 0, 0, 0)
                : Windows.UI.Color.FromArgb(0x09, 0, 0, 0));

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            TrayManager.Changed += OnChanged;
            return () => TrayManager.Changed -= OnChanged;
        }, Array.Empty<object>());
        var cells = TrayManager.CellsForSurface(_surface).ToArray();
        _layout.SetCells(cells);
        return HStack(0, cells.Select(cell =>
            (Element)(Border(new ComponentElement(cell.ViewType, cell.Props))
                with { CornerRadius = HoverCornerRadiusDip })
                .Background(HoverBrushFor(cell.Key))
                .Margin(HoverMarginXDip, HoverMarginYDip, HoverMarginXDip, HoverMarginYDip)
                .Ref(_layout.RefFor(cell.Key))
                .OnMount(_ => _layout.Mounted(cell.Key))
                .OnUnmount(_ => _layout.Unmounted(cell.Key))
                .WithKey(cell.Key)).ToArray())
            .HorizontalAlignment(Microsoft.UI.Xaml.HorizontalAlignment.Left)
            .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Top);
    }
}
