using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Pagurian;

// Owns the ongoing behaviors of the app:
//  1. Keeps the tray window — a horizontal container of widget cells (clock,
//     CPU, memory, plus one cell per tracked Copilot session) — anchored to
//     the left-bottom corner of the taskbar, resizing it as cells come and go.
//  2. Per-cell interaction by polling the cursor position and left mouse
//     button: native hover/pressed highlights, a tooltip with the session
//     name after a short hover dwell, and popups — a click on the clock cell
//     toggles the Hello popup, a click on the CPU or memory cell toggles its
//     detail popup, a click on a session cell toggles that session's popup,
//     and an outside click dismisses whichever is open.
// Runs on a DispatcherQueueTimer on the UI thread.
//
// The tray window is injected into the taskbar via SetParent (same approach as
// AwqatSalaat.WinUI's TaskBarWidget): it becomes a child of Shell_TrayWnd and is
// positioned in taskbar client coordinates with raw SetWindowPos (AppWindow.Move
// semantics are unreliable for child windows owned by WinUI). If injection fails
// the controller falls back to the original floating topmost window.
//
// Note: Reactor window APIs (WindowSpec.Width/Height, ManualPosition,
// ReactorWindow.SetPosition/SetSize) take DIPs and scale them by the window DPI
// themselves. Raw Win32 calls (SetWindowPos for the injected icon, GetCursorPos
// hit-testing) operate in physical pixels. The controller computes anchor
// positions in physical pixels (taskbar rects are physical) and converts to
// DIPs only at the Reactor API boundary.
static class TaskbarController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int InjectRetryIntervalTicks = 25; // ~5s at 200ms per tick

    private static ReactorWindow? _trayWindow;
    private static ReactorWindow? _popupWindow;
    private static ReactorWindow? _sessionPopupWindow;
    private static string? _sessionPopupId;
    private static ReactorWindow? _metricsPopupWindow;
    private static SystemMetricKind? _metricsPopupKind;
    private static ReactorWindow? _tooltipWindow;

    private static TaskbarInterop.RECT _trayRectPx;
    private static TaskbarInterop.RECT _popupRectPx;
    private static TaskbarInterop.RECT _sessionPopupRectPx;
    private static TaskbarInterop.RECT _metricsPopupRectPx;

    // Per-cell hit-test rects in physical pixels (clock, CPU, memory, then
    // session ids in tracker order), rebuilt on every anchor pass.
    private static readonly List<(string Id, TaskbarInterop.RECT Rect)> _cellRectsPx = new();

    private static (double X, double Y) _lastTrayPos = (double.MinValue, double.MinValue);
    private static (double W, double H) _lastTraySize = (0, 0);
    private static bool _injected;
    private static int _ticksSinceInjectAttempt = int.MaxValue; // inject on first tick
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private static Windows.UI.Color _taskbarColor = TaskbarTrayWindow.DefaultTaskbarColor; // average of the gradient stops, for theme derivation
    private static string? _hoverWidgetId; // cell under the cursor (null = outside)
    private static bool _pressed;
    private static bool _wasLeftButtonDown; // previous tick's button state, for click-edge detection
    private static bool _isDarkTheme;

    // Tooltip dwell: the session-name tooltip appears only after the cursor
    // rests on a session cell for ~400 ms (native tooltip timing).
    private const int TooltipDwellTicks = 8; // at 50 ms per tick
    private static string? _tooltipHoverId; // cell the dwell timer is running for
    private static int _tooltipHoverTicks;

    // The theme (derived from the sampled taskbar luminance) that the window
    // and popups use for status colors.
    public static bool IsDarkTheme => _isDarkTheme;

    // Screen-color sampling cadence. Every read from the screen DC synchronizes
    // with DWM composition and stalls for ~one display frame (measured: 17-33
    // ms), so sampling must NEVER run on the UI thread: a stalled UI thread
    // stops pumping, and with the widget parented into the taskbar the attached
    // input queues then wedge the whole taskbar (the reported bug). Sampling
    // runs throttled on a background thread and applies via the dispatcher.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static DateTime _lastSampleUtc = DateTime.MinValue;
    private static int _sampleInFlight; // 0/1, Interlocked guarded

    public static void Start(ReactorWindow trayWindow)
    {
        _trayWindow = trayWindow;
        TaskbarDiagnostics.Log($"start hwnd={TrayWindowHwnd()} dipScale={ScaleOf(trayWindow):F3}");

        _timer = ReactorApp.UIDispatcher!.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop() => _timer?.Stop();

    // Keeps the widget's theme brushes in sync with the sampled taskbar
    // colors, mimicking the native Windows 11 clock (mutated in place, no
    // re-render). The root background gradient itself is maintained by
    // SyncTaskbarColor; here:
    //  - text = theme-aware TextFillColorPrimary, where the theme is derived
    //    from the sampled taskbar luminance so it always matches the taskbar
    //    itself (light/dark/translucent/accent-tinted alike);
    //  - hover overlay = per-cell rounded translucent SubtleFill rect, fully
    //    transparent while idle, SubtleFillColorSecondary on hover and
    //    SubtleFillColorTertiary while pressed (only the hovered cell gets
    //    the overlay; clearing the previous one happens in the hover
    //    transition in UpdateInteractions).
    private static void ApplyEffectiveBrushColor()
    {
        var isDark = Luminance(_taskbarColor) <= 140;
        if (isDark != _isDarkTheme)
        {
            _isDarkTheme = isDark;
            TaskbarTrayWindow.TextBrush.Color = TaskbarTrayWindow.TextColorFor(isDark);
            // Session status and metric gauge colors are per-render brushes, so the
            // container and any open popup must re-render to pick the new theme.
            CopilotSessionTracker.NotifyChanged();
            SystemMetricsTracker.NotifyChanged();
        }

        if (_hoverWidgetId != null)
        {
            var overlay = TaskbarTrayWindow.HoverOverlayColorFor(isDark, _pressed);
            var brush = TaskbarTrayWindow.HoverBrushFor(_hoverWidgetId);
            if (brush.Color != overlay)
                brush.Color = overlay;
        }
    }

    private static double Luminance(Windows.UI.Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    // Keeps the tray window's background in sync with the taskbar. The taskbar
    // is translucent, so its apparent color can vary along its length
    // (wallpaper showing through — measured deltas over 25 levels across the
    // widget's own width): no single color blends the widget in. Instead the
    // widget paints a gradient whose stops are sampled from the native
    // taskbar sliver the widget's 2-DIP inset leaves uncovered — below the
    // widget for horizontal taskbars, above and below for vertical ones —
    // so each stop matches the real taskbar color at that spot. Every sample
    // is a trimmed mean over a small patch, robust against acrylic noise and
    // text/icon glyph pixels.
    //
    // Runs on the UI thread every tick but only maintains the gradient axis;
    // the screen capture itself is throttled and async (see SampleInterval).
    private static void SyncTaskbarColor(in TaskbarInterop.RECT taskbar, double scale)
    {
        var horizontal = taskbar.Width >= taskbar.Height;
        var brush = TaskbarTrayWindow.TaskbarColorBrush;

        // The gradient follows the taskbar's long axis.
        var start = horizontal ? new Windows.Foundation.Point(0, 0.5) : new Windows.Foundation.Point(0.5, 0);
        var end = horizontal ? new Windows.Foundation.Point(1, 0.5) : new Windows.Foundation.Point(0.5, 1);
        if (brush.StartPoint != start) brush.StartPoint = start;
        if (brush.EndPoint != end) brush.EndPoint = end;

        if (DateTime.UtcNow - _lastSampleUtc < SampleInterval)
            return;
        if (Interlocked.CompareExchange(ref _sampleInFlight, 1, 0) != 0)
            return;
        _lastSampleUtc = DateTime.UtcNow;

        if (horizontal)
        {
            // One strip captures all stops: the native sliver below the widget
            // (uncovered thanks to WindowInsetYDip), spanning the widget's
            // width; each stop is a column slice of it (ApplyHorizontalSample).
            var sx = _trayRectPx.Left;
            var sy = _trayRectPx.Bottom;
            var sw = Math.Max(1, _trayRectPx.Right - _trayRectPx.Left);
            var sh = Math.Max(1, taskbar.Bottom - sy); // rows [trayBottom, taskbarBottom - 1]
            Task.Run(() =>
            {
                byte[]? px;
                try
                {
                    px = TaskbarInterop.CaptureScreenRegionPixels(sx, sy, sw, sh);
                }
                finally
                {
                    Interlocked.Exchange(ref _sampleInFlight, 0);
                }
                if (px != null)
                    ReactorApp.UIDispatcher?.TryEnqueue(() => ApplyHorizontalSample(px, sw, sh));
            });
        }
        else
        {
            // Vertical taskbar: only the widget's top and bottom edges have an
            // uncovered native sliver, so capture those two strips across the
            // taskbar's thickness and interpolate between them.
            var x = taskbar.Left + 4;
            var w = Math.Max(1, taskbar.Width - 8);
            var yTop = Math.Clamp(_trayRectPx.Top - 3, taskbar.Top, taskbar.Bottom - 3);
            var yBottom = Math.Clamp(_trayRectPx.Bottom + 1, taskbar.Top, taskbar.Bottom - 3);
            Task.Run(() =>
            {
                byte[]? top, bottom;
                try
                {
                    top = TaskbarInterop.CaptureScreenRegionPixels(x, yTop, w, 3);
                    bottom = TaskbarInterop.CaptureScreenRegionPixels(x, yBottom, w, 3);
                }
                finally
                {
                    Interlocked.Exchange(ref _sampleInFlight, 0);
                }
                if (top != null && bottom != null)
                    ReactorApp.UIDispatcher?.TryEnqueue(() => ApplyVerticalSample(top, bottom, w));
            });
        }
    }

    // UI thread. One captured strip -> per-stop colors: stop i is a trimmed
    // mean over a ~5-px-wide column slice centered on its fraction along the
    // widget's width (the same spots the old per-stop captures sampled).
    private static void ApplyHorizontalSample(byte[] rgba, int width, int height)
    {
        var count = TaskbarTrayWindow.GradientStopCount;
        var colors = new Windows.UI.Color[count];
        for (var i = 0; i < count; i++)
        {
            var center = (int)Math.Round(StopFraction(i, count) * (width - 1));
            var color = TaskbarInterop.TrimmedMeanColor(rgba, width, height, center - 2, center + 3);
            if (color is not { } c)
                return; // unreadable: keep the last good colors
            colors[i] = c;
        }
        ApplyStopColors(colors);
    }

    // UI thread. Two captured strips (above/below the widget) -> per-stop
    // colors interpolated along the taskbar's long axis.
    private static void ApplyVerticalSample(byte[] top, byte[] bottom, int width)
    {
        var t = TaskbarInterop.TrimmedMeanColor(top, width, 3, 0, width);
        var b = TaskbarInterop.TrimmedMeanColor(bottom, width, 3, 0, width);
        if (t is not { } tc || b is not { } bc)
            return;

        var count = TaskbarTrayWindow.GradientStopCount;
        var colors = new Windows.UI.Color[count];
        for (var i = 0; i < count; i++)
            colors[i] = LerpColor(tc, bc, StopFraction(i, count));
        ApplyStopColors(colors);
    }

    // UI thread. Writes the new stop colors into the live gradient brush (no
    // re-render needed) and re-derives the theme from their average.
    private static void ApplyStopColors(Windows.UI.Color[] colors)
    {
        var brush = TaskbarTrayWindow.TaskbarColorBrush;
        var count = colors.Length;

        var changed = false;
        for (var i = 0; i < count && !changed; i++)
        {
            var cur = brush.GradientStops[i].Color;
            changed = Math.Abs(cur.R - colors[i].R) > 2
                   || Math.Abs(cur.G - colors[i].G) > 2
                   || Math.Abs(cur.B - colors[i].B) > 2;
        }
        if (!changed)
            return;

        for (var i = 0; i < count; i++)
            brush.GradientStops[i].Color = colors[i];

        // The theme (light/dark text and hover overlay) follows the average.
        long rSum = 0, gSum = 0, bSum = 0;
        foreach (var c in colors) { rSum += c.R; gSum += c.G; bSum += c.B; }
        _taskbarColor = Windows.UI.Color.FromArgb(255,
            (byte)(rSum / count), (byte)(gSum / count), (byte)(bSum / count));
        ApplyEffectiveBrushColor();
    }

    private static double StopFraction(int i, int count) =>
        count == 1 ? 0.5 : (double)i / (count - 1);

    private static Windows.UI.Color LerpColor(Windows.UI.Color a, Windows.UI.Color b, double f) =>
        Windows.UI.Color.FromArgb(255,
            (byte)(a.R + (b.R - a.R) * f),
            (byte)(a.G + (b.G - a.G) * f),
            (byte)(a.B + (b.B - a.B) * f));

    private static void Tick()
    {
        if (_trayWindow == null)
            return;

        EnsureInjected();
        AnchorTrayWindow();
        UpdateInteractions();
    }

    private static void EnsureInjected()
    {
        if (_injected)
        {
            var hwnd = TrayWindowHwnd();
            var taskbar = TaskbarInterop.FindTaskbar();

            if (hwnd != IntPtr.Zero &&
                TaskbarInterop.IsWindow(hwnd) &&
                taskbar != IntPtr.Zero &&
                TaskbarInterop.GetAncestor(hwnd, TaskbarInterop.GA_PARENT) == taskbar)
            {
                return; // still injected, nothing to do
            }

            // Explorer restart destroys Shell_TrayWnd and every child window with
            // it. Recreate the tray window and re-inject on this tick.
            _injected = false;
            try { _trayWindow!.Close(); } catch { /* native window may already be gone */ }
            _trayWindow = ReactorApp.OpenWindow(
                TaskbarTrayWindow.CreateSpec(), () => new TaskbarTrayWindow());
            _lastTrayPos = (double.MinValue, double.MinValue);
            _lastTraySize = (0, 0);
            _ticksSinceInjectAttempt = int.MaxValue;
        }

        if (_ticksSinceInjectAttempt >= InjectRetryIntervalTicks)
        {
            _ticksSinceInjectAttempt = 0;
            _injected = TryInject();
            TaskbarDiagnostics.Log($"inject result={_injected} trayHwnd={TrayWindowHwnd()} taskbarHwnd={TaskbarInterop.FindTaskbar()}");
        }
        else
        {
            _ticksSinceInjectAttempt++;
        }
    }

    private static bool TryInject()
    {
        var taskbar = TaskbarInterop.FindTaskbar();
        var hwnd = TrayWindowHwnd();
        if (taskbar == IntPtr.Zero || hwnd == IntPtr.Zero)
            return false;

        if (TaskbarInterop.GetAncestor(hwnd, TaskbarInterop.GA_PARENT) == taskbar)
            return true;

        // Convert the top-level style to a child style BEFORE SetParent,
        // otherwise the coordinate space and clipping misbehave.
        var style = (int)TaskbarInterop.GetWindowStyle(hwnd);
        style = (style & ~TaskbarInterop.WS_POPUP) | TaskbarInterop.WS_CHILD;
        TaskbarInterop.SetWindowStyle(hwnd, (IntPtr)style);

        // One attempt per tick; the poll loop provides the retries so the UI
        // thread never blocks (AwqatSalaat sleeps between attempts instead).
        var previousParent = TaskbarInterop.SetParent(hwnd, taskbar);
        TaskbarDiagnostics.Log($"set-parent trayHwnd={hwnd} taskbarHwnd={taskbar} previousParent={previousParent} style=0x{style:X8}");
        return previousParent != IntPtr.Zero;
    }

    // HWND of the tray window, or IntPtr.Zero when it is gone (e.g. right after
    // an Explorer restart destroyed it). Safe to call any time.
    public static IntPtr TrayWindowHwnd()
    {
        try
        {
            return _trayWindow == null
                ? IntPtr.Zero
                : Win32Interop.GetWindowFromWindowId(_trayWindow.AppWindow.Id);
        }
        catch
        {
            return IntPtr.Zero; // window already destroyed (e.g. by an Explorer restart)
        }
    }

    private static void AnchorTrayWindow()
    {
        if (!TaskbarInterop.TryGetTaskbarContentRect(out var taskbar))
            return;
        if (!TaskbarInterop.TryGetTaskbarRect(out var taskbarParent))
            return;

        // Ignore transient bogus rects (display topology changes, Explorer
        // restarts): keep the last good anchor instead of jumping off-taskbar.
        if (taskbar.Width < 32 || taskbar.Height < 16)
            return;

        // Taskbars are 48 DIPs thick by design, so the scale derived from the
        // real taskbar rect doubles as the taskbar's own DPI scale. Use it —
        // not the window's DipScale, which can lag behind monitor changes —
        // and size the widget straight from the taskbar rect, minus a small
        // vertical inset: winH < taskbar.Height always, so the widget can
        // never stick out of the taskbar, whatever DPI it believes it is on.
        var horizontal = taskbar.Width >= taskbar.Height;
        var scale = (horizontal ? taskbar.Height : taskbar.Width) / TaskbarTrayWindow.WindowHeightDip;
        var windowScale = ScaleOf(_trayWindow!);
        var contentScaleChanged = TaskbarTrayWindow.SetContentScale(scale / windowScale);
        // ≥1 px even before the first layout pass (cells read 0 wide until
        // then): never hand SetWindowPos a 0-sized window.
        var winW = Math.Max(TaskbarTrayLayout.TotalWidthDip * scale, 1);
        var winH = (TaskbarTrayWindow.WindowHeightDip - 2 * TaskbarTrayWindow.WindowInsetYDip) * scale;

        double xPx, yPx;
        if (horizontal)
        {
            // Horizontal taskbar: hug its left edge, centered vertically
            // (2 DIP clear of the top/bottom edges via WindowInsetYDip).
            xPx = taskbar.Left + 8 * scale;
            yPx = taskbar.Top + (taskbar.Height - winH) / 2;
        }
        else
        {
            // Vertical taskbar: hug its bottom edge, centered horizontally.
            xPx = taskbar.Left + (taskbar.Width - winW) / 2;
            yPx = taskbar.Bottom - winH - 8 * scale;
        }

        // Belt and braces: clamp inside the taskbar so a stale rect or DPI
        // mismatch can never leave the widget covering the taskbar's edge.
        xPx = Math.Clamp(xPx, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - winW));
        yPx = Math.Clamp(yPx, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - winH));

        _trayRectPx = new TaskbarInterop.RECT
        {
            Left = (int)xPx,
            Top = (int)yPx,
            Right = (int)(xPx + winW),
            Bottom = (int)(yPx + winH),
        };

        // Per-cell hit-test rects, left to right in the shared layout's order
        // (clock, CPU, memory, then one cell per tracked Copilot session —
        // the same order the window renders them).
        _cellRectsPx.Clear();
        var cellLeft = xPx;
        foreach (var cell in TaskbarTrayLayout.Cells)
        {
            _cellRectsPx.Add((cell.Id, CellRect(cellLeft, yPx, cell.CellWidthDip * scale, winH)));
            cellLeft += cell.CellWidthDip * scale;
        }

        SyncTaskbarColor(in taskbar, scale);

        var sizeChanged = Math.Abs(winW - _lastTraySize.W) > 0.5 || Math.Abs(winH - _lastTraySize.H) > 0.5;
        var posChanged = Math.Abs(xPx - _lastTrayPos.X) > 0.5 || Math.Abs(yPx - _lastTrayPos.Y) > 0.5;
        if (!sizeChanged && !posChanged)
            return;

        _lastTraySize = (winW, winH);
        _lastTrayPos = (xPx, yPx);
        if (contentScaleChanged || sizeChanged)
            CopilotSessionTracker.NotifyChanged();

        if (_injected)
        {
            var hwnd = TrayWindowHwnd();
            if (hwnd == IntPtr.Zero)
                return;

            // Child window: coordinates are relative to the taskbar's client area.
            var positioned = TaskbarInterop.SetWindowPos(hwnd, TaskbarInterop.HWND_TOP,
                (int)(xPx - taskbarParent.Left), (int)(yPx - taskbarParent.Top),
                (int)winW, (int)winH,
                TaskbarInterop.SWP_NOACTIVATE);
            TaskbarInterop.TryGetWindowRect(hwnd, out var actual);
            LogAnchor("injected", taskbar, taskbarParent, scale, windowScale, winW, winH, xPx, yPx, hwnd, positioned, actual);
        }
        else
        {
            // Floating fallback: Reactor window APIs take DIPs, not pixels;
            // convert with the window's own DPI scale.
            var winScale = ScaleOf(_trayWindow!);
            if (sizeChanged)
                _trayWindow!.SetSize(winW / winScale, winH / winScale);
            if (posChanged)
                _trayWindow!.SetPosition(xPx / winScale, yPx / winScale);
            TaskbarInterop.TryGetWindowRect(TrayWindowHwnd(), out var actual);
            LogAnchor("floating", taskbar, taskbarParent, scale, windowScale, winW, winH, xPx, yPx, TrayWindowHwnd(), true, actual);
        }
    }

    private static void LogAnchor(
        string mode,
        in TaskbarInterop.RECT taskbar,
        in TaskbarInterop.RECT taskbarParent,
        double taskbarScale,
        double windowScale,
        double widthPx,
        double heightPx,
        double xPx,
        double yPx,
        IntPtr hwnd,
        bool positioned,
        in TaskbarInterop.RECT actual)
    {
        var cells = string.Join(", ", TaskbarTrayLayout.Cells.Select(c => $"{c.Id}:{c.CellWidthDip:F1}dip"));
        TaskbarDiagnostics.Log(
            $"anchor mode={mode} positioned={positioned} hwnd={hwnd} parent={TaskbarInterop.GetAncestor(hwnd, TaskbarInterop.GA_PARENT)} " +
            $"taskbar=({taskbar.Left},{taskbar.Top})-({taskbar.Right},{taskbar.Bottom}) {taskbar.Width}x{taskbar.Height}px " +
            $"shell=({taskbarParent.Left},{taskbarParent.Top})-({taskbarParent.Right},{taskbarParent.Bottom}) {taskbarParent.Width}x{taskbarParent.Height}px " +
            $"scale={taskbarScale:F3} windowDipScale={windowScale:F3} contentScale={TaskbarTrayWindow.ContentScale:F3} " +
            $"desired=({xPx:F1},{yPx:F1}) {widthPx:F1}x{heightPx:F1}px " +
            $"actual=({actual.Left},{actual.Top})-({actual.Right},{actual.Bottom}) {actual.Width}x{actual.Height}px " +
            $"cells=[{cells}]");
    }

    private static TaskbarInterop.RECT CellRect(double xPx, double yPx, double wPx, double hPx) =>
        new()
        {
            Left = (int)xPx,
            Top = (int)yPx,
            Right = (int)(xPx + wPx),
            Bottom = (int)(yPx + hPx),
        };

    private static TaskbarInterop.RECT? CellRectPx(string widgetId)
    {
        foreach (var (id, rect) in _cellRectsPx)
        {
            if (id == widgetId)
                return rect;
        }
        return null;
    }

    private static void UpdateInteractions()
    {
        var cursor = TaskbarInterop.GetCursorPosition();

        // When a session ends the tracker drops it and its cell disappears;
        // an open popup for that session goes with it.
        if (_sessionPopupId != null && CopilotSessionTracker.Find(_sessionPopupId) == null)
            HideSessionPopup();

        // A click is an up→down transition of the left button. Physical
        // clicks last ~80-120 ms, so 50 ms polling reliably catches them.
        var leftDown = TaskbarInterop.IsLeftButtonDown();
        var clickEdge = leftDown && !_wasLeftButtonDown;
        _wasLeftButtonDown = leftDown;

        // Which widget cell is under the cursor, if any?
        string? hoverId = null;
        foreach (var (id, rect) in _cellRectsPx)
        {
            if (rect.Contains(cursor))
            {
                hoverId = id;
                break;
            }
        }

        // Hover/pressed feedback like the native clock: SubtleFillColorSecondary
        // on hover, SubtleFillColorTertiary while the button is held — on the
        // hovered cell's own overlay brush (mutated live, no re-render).
        if (hoverId != _hoverWidgetId || leftDown != _pressed)
        {
            if (_hoverWidgetId != null && hoverId != _hoverWidgetId)
                TaskbarTrayWindow.HoverBrushFor(_hoverWidgetId).Color = TaskbarTrayWindow.HoverOverlayHidden;
            _hoverWidgetId = hoverId;
            _pressed = leftDown;
            ApplyEffectiveBrushColor();
        }

        // Tooltip: after a short dwell on a session cell, show the session's
        // name above the cell; hide on leave and on any click, and don't
        // re-dwell while a popup is open.
        if (clickEdge)
        {
            HideTooltip();
            _tooltipHoverTicks = 0;
        }
        if (hoverId != _tooltipHoverId)
        {
            _tooltipHoverId = hoverId;
            _tooltipHoverTicks = 0;
            HideTooltip();
        }
        else if (hoverId != null
                 && hoverId != TaskbarTrayWindow.ClockWidgetId
                 && hoverId != TaskbarTrayWindow.CpuWidgetId
                 && hoverId != TaskbarTrayWindow.MemoryWidgetId
                 && !AnyPopupVisible())
        {
            _tooltipHoverTicks++;
            if (_tooltipHoverTicks == TooltipDwellTicks)
                ShowTooltip(hoverId);
        }

        // Click dispatch: toggle the clicked cell's popup, dismiss on outside
        // click (native Windows 11 flyout style). Clicks inside an open popup
        // itself (e.g., its button) don't dismiss it. One popup at a time:
        // opening one closes the other.
        if (!clickEdge)
            return;

        if (hoverId == TaskbarTrayWindow.ClockWidgetId)
        {
            HideMetricsPopup();
            HideSessionPopup();
            var popup = _popupWindow;
            if (popup is { IsVisible: true })
                popup.Hide();
            else
                EnsurePopupVisible();
        }
        else if (hoverId == TaskbarTrayWindow.CpuWidgetId)
        {
            HideSessionPopup();
            if (_popupWindow is { IsVisible: true })
                _popupWindow.Hide();
            ToggleMetricsPopup(SystemMetricKind.Cpu, hoverId);
        }
        else if (hoverId == TaskbarTrayWindow.MemoryWidgetId)
        {
            HideSessionPopup();
            if (_popupWindow is { IsVisible: true })
                _popupWindow.Hide();
            ToggleMetricsPopup(SystemMetricKind.Memory, hoverId);
        }
        else if (hoverId != null)
        {
            if (_popupWindow is { IsVisible: true })
                _popupWindow.Hide();
            HideMetricsPopup();
            if (_sessionPopupId == hoverId && _sessionPopupWindow is { IsVisible: true })
                HideSessionPopup(); // clicked its cell again: toggle off
            else
                ShowSessionPopup(hoverId);
        }
        else
        {
            if (_popupWindow is { IsVisible: true } helloPopup && !_popupRectPx.Contains(cursor))
                helloPopup.Hide();
            if (_sessionPopupWindow is { IsVisible: true } && !_sessionPopupRectPx.Contains(cursor))
                HideSessionPopup();
            if (_metricsPopupWindow is { IsVisible: true } && !_metricsPopupRectPx.Contains(cursor))
                HideMetricsPopup();
        }
    }

    private static bool AnyPopupVisible() =>
        _popupWindow is { IsVisible: true }
        || _sessionPopupWindow is { IsVisible: true }
        || _metricsPopupWindow is { IsVisible: true };

    // Shows the session-name tooltip above the session's cell (below it when
    // the taskbar is at the screen's top edge).
    private static void ShowTooltip(string sessionId)
    {
        var session = CopilotSessionTracker.Find(sessionId);
        var cell = CellRectPx(sessionId);
        if (session == null || cell == null || _trayWindow == null)
            return;

        HideTooltip();
        var scale = ScaleOf(_trayWindow);
        var xDip = cell.Value.Left / scale;
        var yDip = cell.Value.Top / scale - TooltipWindow.WindowHeightDip - 4;
        if (yDip < 0)
            yDip = cell.Value.Bottom / scale + 4;

        var tooltip = new TooltipWindow(session.Name);
        _tooltipWindow = ReactorApp.OpenWindow(
            tooltip.CreateSpec((xDip, yDip)),
            () => tooltip);
        _tooltipWindow.SetSize(TooltipWindow.WidthFor(session.Name), TooltipWindow.WindowHeightDip);
        _tooltipWindow.SetPosition(xDip, yDip);
        if (!_tooltipWindow.IsVisible)
            _tooltipWindow.Show();
    }

    private static void HideTooltip()
    {
        try { _tooltipWindow?.Close(); } catch { /* window may already be gone */ }
        _tooltipWindow = null;
    }

    // Opens the popup for one Copilot session above its cell (below it when
    // the taskbar is at the screen's top edge).
    private static void ShowSessionPopup(string sessionId)
    {
        HideSessionPopup();
        var cell = CellRectPx(sessionId);
        if (cell == null || _trayWindow == null)
            return;

        var scale = ScaleOf(_trayWindow);
        var popupW = SessionPopupWindow.WindowWidthDip;  // DIPs
        var popupH = SessionPopupWindow.WindowHeightDip; // DIPs

        // Compute in DIPs — Reactor window APIs take DIPs; the physical pixel
        // rect below is only for cursor hit-testing (GetCursorPos is physical).
        var xDip = cell.Value.Left / scale;
        var yDip = cell.Value.Top / scale - popupH - 6;
        if (yDip < 0)
            yDip = cell.Value.Bottom / scale + 6;

        _sessionPopupRectPx = CellRect(xDip * scale, yDip * scale, popupW * scale, popupH * scale);

        var popup = new SessionPopupWindow(sessionId);
        _sessionPopupWindow = ReactorApp.OpenWindow(
            popup.CreateSpec((xDip, yDip)),
            () => popup);
        _sessionPopupWindow.SetSize(popupW, popupH);
        _sessionPopupWindow.SetPosition(xDip, yDip);
        if (!_sessionPopupWindow.IsVisible)
            _sessionPopupWindow.Show();
        _sessionPopupId = sessionId;
    }

    private static void HideSessionPopup()
    {
        try { _sessionPopupWindow?.Close(); } catch { /* window may already be gone */ }
        _sessionPopupWindow = null;
        _sessionPopupId = null;
    }

    private static void ToggleMetricsPopup(SystemMetricKind kind, string cellId)
    {
        if (_metricsPopupWindow is { IsVisible: true } && _metricsPopupKind == kind)
        {
            HideMetricsPopup();
            return;
        }

        HideMetricsPopup();
        var cell = CellRectPx(cellId);
        if (cell == null || _trayWindow == null)
            return;

        var scale = ScaleOf(_trayWindow);
        var (popupW, popupH) = kind == SystemMetricKind.Cpu
            ? (CpuMetricsPopupWindow.WindowWidthDip, CpuMetricsPopupWindow.WindowHeightDip)
            : (MemoryMetricsPopupWindow.WindowWidthDip, MemoryMetricsPopupWindow.WindowHeightDip);

        var xDip = cell.Value.Left / scale;
        var yDip = cell.Value.Top / scale - popupH - 6;
        if (yDip < 0)
            yDip = cell.Value.Bottom / scale + 6;

        _metricsPopupRectPx = CellRect(xDip * scale, yDip * scale, popupW * scale, popupH * scale);

        var popup = kind == SystemMetricKind.Cpu
            ? (Component)new CpuMetricsPopupWindow()
            : new MemoryMetricsPopupWindow();
        var spec = kind == SystemMetricKind.Cpu
            ? ((CpuMetricsPopupWindow)popup).CreateSpec((xDip, yDip))
            : ((MemoryMetricsPopupWindow)popup).CreateSpec((xDip, yDip));

        _metricsPopupWindow = ReactorApp.OpenWindow(spec, () => popup);
        _metricsPopupWindow.SetSize(popupW, popupH);
        _metricsPopupWindow.SetPosition(xDip, yDip);
        if (!_metricsPopupWindow.IsVisible)
            _metricsPopupWindow.Show();
        _metricsPopupKind = kind;
        SystemMetricsTracker.SetPopupVisible(kind, true);
    }

    private static void HideMetricsPopup()
    {
        if (_metricsPopupWindow == null)
            return;

        try { _metricsPopupWindow.Close(); } catch { /* window may already be gone */ }
        if (_metricsPopupKind != null)
            SystemMetricsTracker.SetPopupVisible(_metricsPopupKind.Value, false);
        _metricsPopupWindow = null;
        _metricsPopupKind = null;
    }

    private static void EnsurePopupVisible()
    {
        var scale = ScaleOf(_trayWindow!);
        var popupW = HoverPopupWindow.WindowWidthDip;  // DIPs
        var popupH = HoverPopupWindow.WindowHeightDip; // DIPs

        // Compute in DIPs — Reactor window APIs take DIPs; the physical pixel
        // rect below is only for cursor hit-testing (GetCursorPos is physical).
        var xDip = _trayRectPx.Left / scale;
        var yDip = _trayRectPx.Top / scale - popupH - 6;
        if (yDip < 0)
            yDip = _trayRectPx.Bottom / scale + 6; // taskbar at the top edge: open below

        _popupRectPx = new TaskbarInterop.RECT
        {
            Left = (int)(xDip * scale),
            Top = (int)(yDip * scale),
            Right = (int)((xDip + popupW) * scale),
            Bottom = (int)((yDip + popupH) * scale),
        };

        if (_popupWindow == null)
        {
            _popupWindow = ReactorApp.OpenWindow(
                HoverPopupWindow.CreateSpec((xDip, yDip)),
                () => new HoverPopupWindow());
        }

        _popupWindow.SetSize(popupW, popupH);
        _popupWindow.SetPosition(xDip, yDip);
        if (!_popupWindow.IsVisible)
            _popupWindow.Show();
    }

    private static double ScaleOf(ReactorWindow window) =>
        window.DipScale > 0 ? window.DipScale : 1.0;
}
