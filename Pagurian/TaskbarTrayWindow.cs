using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Borderless window anchored to the taskbar: the Tray, a horizontal container
// of shell cells. All cell content comes from the configured shells
// (TrayShells); the window itself only supplies the per-cell chrome — the
// native rounded translucent hover overlay, the hover margins, and the width
// read-back Ref — plus the sampled taskbar-color background gradient that
// blends the whole tray into the taskbar. A shell with zero cells occupies no
// space.
class TaskbarTrayWindow : Component
{
    // Design sizes in DIPs; the controller scales to physical pixels.
    // WindowHeightDip is the full taskbar thickness (the controller derives
    // the DPI scale from it); the widget itself is inset WindowInsetYDip
    // from the taskbar's top and bottom edges so it sits slightly inside.
    // Cell widths are content-driven: cells size to their content, and the
    // controller reads the rendered widths back through TaskbarTrayLayout to
    // size the window and its hit-test rects.
    public const double WindowHeightDip = 48;
    public const double WindowInsetYDip = 2;
    public const double ContentHeightDip = WindowHeightDip - 2 * WindowInsetYDip;

    // Native cell hover visual: a SubtleFill overlay inset from the cell
    // edges with the control corner radius (like every Win11 taskbar button).
    public const double HoverMarginXDip = 3;
    public const double HoverMarginYDip = 4;
    public const double HoverCornerRadiusDip = 4;

    public static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian",
        // First frame: the cells haven't laid out yet, so the layout reads
        // 0 — start at a 1-DIP floor (a literal 0-wide window risks a
        // skipped layout pass) and let the controller grow the window to the
        // real content-driven width within a tick or two of the first render.
        Width = Math.Max(TaskbarTrayLayout.TotalWidthDip, 1),
        Height = WindowHeightDip,
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
        Key = WindowKey.Of("pagurian-icon"),
        Icon = WindowIcon.FromPath(AppAssets.ApplicationIconPath),
    };

    // Number of stops in the taskbar-color gradient: sampled across the
    // tray's width (see TaskbarController.SyncTaskbarColor).
    public const int GradientStopCount = 5;

    public static readonly Windows.UI.Color DefaultTaskbarColor = Windows.UI.Color.FromArgb(255, 239, 239, 239);

    // Shared live brushes, mutated in place by TaskbarController on every poll
    // tick (no re-render needed — a brush is a live DependencyObject):
    //  - TaskbarColorBrush: the taskbar is translucent, so its apparent color
    //    can vary along its length (wallpaper showing through) and a single
    //    color can't blend the tray in. This is a gradient along the taskbar's
    //    long axis whose stops are the opaque colors sampled from the native
    //    taskbar sliver the tray's 2-DIP inset leaves uncovered. (Once
    //    injected into the taskbar the window is a child window where WinUI's
    //    Transparent SystemBackdrop no longer applies and the background turns
    //    opaque, so these sampled colors are what blend the tray into the
    //    taskbar — like the native clock's transparent background, minus the
    //    transparency.)
    public static readonly LinearGradientBrush TaskbarColorBrush = CreateTaskbarColorBrush();
    public static readonly ScaleTransform ContentScaleTransform = new();
    public static double ContentScale { get; private set; } = 1;

    // SetParent can make the child window's XAML scale differ from the taskbar
    // monitor scale. Scale the entire fixed-size content root to bridge that
    // gap, while the raw child HWND remains positioned in physical pixels.
    public static bool SetContentScale(double scale)
    {
        scale = Math.Max(scale, 0.01);
        if (Math.Abs(ContentScale - scale) < 0.001)
            return false;

        ContentScale = scale;
        ContentScaleTransform.ScaleX = scale;
        ContentScaleTransform.ScaleY = scale;
        return true;
    }

    // Per-cell hover overlays: one live brush per cell key, created lazily and
    // mutated in place by the controller's poll loop (no re-render) — the
    // native SubtleFillColorSecondary (hover) / SubtleFillColorTertiary
    // (pressed) feedback, alpha-blended over the sampled base color. Keeping
    // the brush instances here keeps them stable across re-renders, so the
    // controller always mutates the brush on screen.
    private static readonly Dictionary<string, SolidColorBrush> _hoverBrushes = new();

    public static SolidColorBrush HoverBrushFor(string cellKey)
    {
        if (!_hoverBrushes.TryGetValue(cellKey, out var brush))
        {
            brush = new SolidColorBrush(HoverOverlayHidden);
            _hoverBrushes[cellKey] = brush;
        }
        return brush;
    }

    private static LinearGradientBrush CreateTaskbarColorBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };
        for (var i = 0; i < GradientStopCount; i++)
            brush.GradientStops.Add(new GradientStop
            {
                Color = DefaultTaskbarColor,
                Offset = (double)i / (GradientStopCount - 1),
            });
        return brush;
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

        // Cells come and go as shells AddCell/RemoveCell: re-render the whole
        // tray (no fine-grained diff — cell counts are tiny). Cell content
        // changes don't reach here; each cell re-renders itself.
        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            TrayShells.Changed += OnChanged;
            TaskbarTrayLayout.MeasuredWidthChanged += OnChanged;
            return () =>
            {
                TrayShells.Changed -= OnChanged;
                TaskbarTrayLayout.MeasuredWidthChanged -= OnChanged;
            };
        }, Array.Empty<object>());

        var cells = TrayShells.Cells;
        TaskbarTrayLayout.PruneRefs();

        // Root: sampled taskbar color (the blend). Cells: rounded translucent
        // per-cell hover overlay (alpha 0 while idle) around each cell.
        // Cells size to their content; every cell is keyed so reconciliation
        // preserves identity and carries a Ref the controller reads the
        // rendered width back through (TaskbarTrayLayout). Reactor embeds
        // child components by type, so a cell renders as a ComponentElement
        // carrying its props record.
        var contentWidth = Math.Max(TaskbarTrayLayout.TotalWidthDip, 1);
        var content = Border(
                HStack(0,
                    cells.Select(cell =>
                        (Element)(Border(new ComponentElement(cell.ViewType, cell.Props))
                            with { CornerRadius = HoverCornerRadiusDip })
                            .Background(HoverBrushFor(cell.Key))
                            .Margin(HoverMarginXDip, HoverMarginYDip, HoverMarginXDip, HoverMarginYDip)
                            .Ref(TaskbarTrayLayout.RefFor(cell.Key))
                            .WithKey(cell.Key))
                        .ToArray()))
            .Width(contentWidth)
            .Height(ContentHeightDip)
            .Set(border =>
            {
                border.RenderTransform = ContentScaleTransform;
                border.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            });

        // The content keeps its explicit unscaled width for DPI compensation,
        // while this unconstrained outer layer fills the HWND. If the native
        // window grows before newly mounted cells finish measuring, the new
        // space therefore shows the sampled taskbar gradient instead of the
        // window's default white background.
        return Border(content)
            .Background(TaskbarColorBrush);
    }
}
