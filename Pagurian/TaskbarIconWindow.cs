using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Borderless window anchored to the left-bottom corner of the taskbar: a
// horizontal container of widget cells. The first cell replicates the native
// Windows 11 datetime widget (two centered 12-DIP lines, time over date,
// current-culture short formats); it is followed by one cell per tracked
// Copilot session (GitHub icon + colored status text, see
// CopilotSessionTracker). The window blends into the taskbar via a sampled
// background gradient, shows the native rounded translucent hover/pressed
// highlight per cell, and adapts its text color to light/dark taskbars.
class TaskbarIconWindow : Component
{
    // Design sizes in DIPs; the controller scales to physical pixels.
    // WindowHeightDip is the full taskbar thickness (the controller derives
    // the DPI scale from it); the widget itself is inset WindowInsetYDip
    // from the taskbar's top and bottom edges so it sits slightly inside.
    // The window width is the sum of the cell widths (TotalWidthDip).
    public const double ClockCellWidthDip = 72;
    public const double SessionCellWidthDip = 76; // 16 icon + 6 gap + status text
    public const double WindowHeightDip = 48;
    public const double WindowInsetYDip = 2;

    // Widget id of the clock cell in the controller's hover/click bookkeeping
    // (session cells are identified by their session id).
    public const string ClockWidgetId = "clock";

    // Total window width: the clock cell plus one cell per tracked session.
    // Read by the controller on every tick, so adding/removing sessions
    // resizes the window through the normal anchor path.
    public static double TotalWidthDip() =>
        ClockCellWidthDip + CopilotSessionTracker.Sessions.Count * SessionCellWidthDip;

    // Native clock hover visual: a SubtleFill overlay inset from the cell
    // edges with the control corner radius (like every Win11 taskbar button).
    public const double HoverMarginXDip = 3;
    public const double HoverMarginYDip = 4;
    public const double HoverCornerRadiusDip = 4;
    public const double ClockFontSizeDip = 12;

    public static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian",
        Width = TotalWidthDip(),
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
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    // Number of stops in the taskbar-color gradient: sampled across the
    // widget's width (see TaskbarController.SyncTaskbarColor).
    public const int GradientStopCount = 5;

    public static readonly Windows.UI.Color DefaultTaskbarColor = Windows.UI.Color.FromArgb(255, 239, 239, 239);

    // Shared live brushes, mutated in place by TaskbarController on every poll
    // tick (no re-render needed — a brush is a live DependencyObject):
    //  - TaskbarColorBrush: the taskbar is translucent, so its apparent color
    //    can vary along its length (wallpaper showing through) and a single
    //    color can't blend the widget in. This is a gradient along the
    //    taskbar's long axis whose stops are the opaque colors sampled from
    //    the native taskbar sliver the widget's 2-DIP inset leaves uncovered.
    //    (Once injected into the taskbar the window is a child window where
    //    WinUI's Transparent SystemBackdrop no longer applies and the
    //    background turns opaque, so these sampled colors are what blend the
    //    widget into the taskbar — like the native clock's transparent
    //    background, minus the transparency.)
    //  - TextBrush: theme-aware TextFillColorPrimary for the clock lines.
    public static readonly Microsoft.UI.Xaml.Media.LinearGradientBrush TaskbarColorBrush =
        CreateTaskbarColorBrush();
    public static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TextBrush =
        new(TextColorFor(isDark: false));

    // Per-cell hover overlays: one live brush per widget (ClockWidgetId or a
    // session id), created lazily here and mutated in place by the
    // controller's poll loop (no re-render) — the native clock's
    // SubtleFillColorSecondary (hover) / SubtleFillColorTertiary (pressed)
    // feedback, alpha-blended over the sampled base color. Keeping the brush
    // instances here (rather than per render) keeps them stable across
    // re-renders, so the controller always mutates the brush on screen.
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> _hoverBrushes = new();

    public static Microsoft.UI.Xaml.Media.SolidColorBrush HoverBrushFor(string widgetId)
    {
        if (!_hoverBrushes.TryGetValue(widgetId, out var brush))
        {
            brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(HoverOverlayHidden);
            _hoverBrushes[widgetId] = brush;
        }
        return brush;
    }

    // Per-session status text brushes — the same live-brush pattern as the
    // hover overlays (and the analyzer-compliant alternative to inline
    // brushes, REACTOR_THEME_004): instances are stable per session so their
    // colors can be refreshed in place. Called from Render, which runs on
    // every tracker change and theme flip, so the color is always current.
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> _statusBrushes = new();

    public static Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrushFor(CopilotSession session, bool isDark)
    {
        var color = StatusColorFor(session.Status, isDark);
        if (!_statusBrushes.TryGetValue(session.SessionId, out var brush))
        {
            brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            _statusBrushes[session.SessionId] = brush;
        }
        else if (brush.Color != color)
        {
            brush.Color = color; // status or theme changed since the last render
        }
        return brush;
    }

    private static Microsoft.UI.Xaml.Media.LinearGradientBrush CreateTaskbarColorBrush()
    {
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };
        for (var i = 0; i < GradientStopCount; i++)
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            {
                Color = DefaultTaskbarColor,
                Offset = (double)i / (GradientStopCount - 1),
            });
        return brush;
    }

    public static readonly Windows.UI.Color HoverOverlayHidden = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    // TextFillColorPrimary: white on dark taskbars, near-black on light ones.
    public static Windows.UI.Color TextColorFor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 26, 26, 26);

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

    // Session status text colors, theme-aware so they stay readable on the
    // taskbar: Idle = TextFillColorSecondary-ish, Working/Blocked = Fluent
    // green/orange. (No "Ended" color: sessionEnd removes the cell outright.)
    public static Windows.UI.Color StatusColorFor(CopilotSessionStatus status, bool isDark) =>
        status switch
        {
            CopilotSessionStatus.Working =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
                    : Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10),
            CopilotSessionStatus.Blocked =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0xF7, 0x63, 0x0C)
                    : Windows.UI.Color.FromArgb(255, 0xCA, 0x50, 0x10),
            _ =>
                isDark
                    ? Windows.UI.Color.FromArgb(255, 0xC8, 0xC8, 0xC8)
                    : Windows.UI.Color.FromArgb(255, 0x5C, 0x5C, 0x5C),
        };

    // Drops cached brushes whose session is gone (sessionEnd removes
    // sessions now, so without this the caches would grow forever). Runs on
    // the UI thread from Render, like every other brush access.
    private static void PruneBrushCache(
        Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> brushes)
    {
        foreach (var id in brushes.Keys
                     .Where(k => k != ClockWidgetId && CopilotSessionTracker.Find(k) == null)
                     .ToList())
            brushes.Remove(id);
    }

    private static string NowTime() => DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
    private static string NowDate() => DateTime.Now.ToString("d", CultureInfo.CurrentCulture);

    public override Element Render()
    {
        var (time, setTime) = UseState(NowTime());
        var (date, setDate) = UseState(NowDate());
        var lastText = UseRef(NowTime() + "|" + NowDate());
        var (_, setTrackerVersion) = UseState(0);

        // 1s clock updates. Both formats change at most once per minute; the
        // guard keeps the timer from re-rendering when nothing changed.
        UseEffect(() =>
        {
            var timer = ReactorApp.UIDispatcher!.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                string t = NowTime(), d = NowDate();
                var key = t + "|" + d;
                if (key == lastText.Current)
                    return;
                lastText.Current = key;
                setTime(t);
                setDate(d);
            };
            timer.Start();
            return () => timer.Stop();
        }, Array.Empty<object>());

        // Re-render when Copilot sessions appear or change status (and on
        // taskbar theme flips, which TaskbarController forwards).
        UseEffect(() =>
        {
            void OnChanged() => setTrackerVersion(CopilotSessionTracker.Version);
            CopilotSessionTracker.UiChanged += OnChanged;
            return () => CopilotSessionTracker.UiChanged -= OnChanged;
        }, Array.Empty<object>());

        var isDark = TaskbarController.IsDarkTheme;

        // Sessions can vanish (sessionEnd): drop their cached brushes.
        PruneBrushCache(_hoverBrushes);
        PruneBrushCache(_statusBrushes);

        // Root: sampled taskbar color (the blend). Cells: rounded translucent
        // per-cell hover overlay (alpha 0 while idle) around each widget.
        // Explicit text widths make clock centering independent of panel
        // behavior; every cell is keyed so reconciliation preserves identity.
        return Border(
                HStack(0,
                [
                    // Cell 0: the native datetime replica.
                    (Border(
                        FlexColumn(
                            TextBlock(time)
                                .FontSize(ClockFontSizeDip)
                                .TextAlignment(Microsoft.UI.Xaml.TextAlignment.Center)
                                .Width(ClockCellWidthDip - 2 * HoverMarginXDip)
                                .Foreground(TextBrush),
                            TextBlock(date)
                                .FontSize(ClockFontSizeDip)
                                .TextAlignment(Microsoft.UI.Xaml.TextAlignment.Center)
                                .Width(ClockCellWidthDip - 2 * HoverMarginXDip)
                                .Foreground(TextBrush))
                            .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center))
                        with { CornerRadius = HoverCornerRadiusDip })
                        .Background(HoverBrushFor(ClockWidgetId))
                        // Fixed cell slot: the controller's hit-test rects assume
                        // exactly ClockCellWidthDip per cell (Width excludes Margin).
                        .Width(ClockCellWidthDip - 2 * HoverMarginXDip)
                        .Margin(HoverMarginXDip, HoverMarginYDip, HoverMarginXDip, HoverMarginYDip)
                        .WithKey(ClockWidgetId),
                    // One cell per tracked Copilot session: GitHub icon +
                    // colored status text, centered as a group.
                    .. CopilotSessionTracker.Sessions.Select(s =>
                        (Element)(Border(
                            HStack(6,
                            [
                                Image(AppAssets.GitHubIconPath)
                                    .Width(16)
                                    .Height(16)
                                    .AccessibilityHidden()
                                    .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center),
                                TextBlock(s.Status.ToString())
                                    .FontSize(ClockFontSizeDip)
                                    .Foreground(StatusBrushFor(s, isDark))
                                    .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center),
                            ])
                            .HorizontalAlignment(Microsoft.UI.Xaml.HorizontalAlignment.Center)
                            .VerticalAlignment(Microsoft.UI.Xaml.VerticalAlignment.Center))
                            with { CornerRadius = HoverCornerRadiusDip })
                            .Background(HoverBrushFor(s.SessionId))
                            // Fixed cell slot, same rationale as the clock cell:
                            // without an explicit width the Border would size to
                            // the status text and the controller's fixed-width
                            // hit-test rect would drift off the visual cell.
                            .Width(SessionCellWidthDip - 2 * HoverMarginXDip)
                            .Margin(HoverMarginXDip, HoverMarginYDip, HoverMarginXDip, HoverMarginYDip)
                            .WithKey(s.SessionId)),
                ]))
            .Background(TaskbarColorBrush);
    }
}
