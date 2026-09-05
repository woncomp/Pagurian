using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Pagurian.Sdk;

namespace Pagurian;

// Owns the ongoing behaviors of the app:
//  1. Keeps the tray window — a horizontal container of shell cells —
//     anchored to the left-bottom corner of the taskbar, resizing it as
//     cells come and go.
//  2. Per-cell interaction by polling the cursor position and left mouse
//     button: native hover/pressed highlights, a hover-dwell tooltip (the
//     cell's GetTooltip delegate), click dispatch (the cell's OnClicked
//     delegate, or its billboard toggle by default), and outside-click
//     dismissal of the open billboard.
//  3. Billboard management: one billboard at a time, positioned above its
//     owner cell (below when the taskbar is at the screen's top edge),
//     closed on outside click or when its owner cell is removed.
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
// themselves. Raw Win32 calls (SetWindowPos for the injected window, GetCursorPos
// hit-testing) operate in physical pixels. The controller computes anchor
// positions in physical pixels (taskbar rects are physical) and converts to
// DIPs only at the Reactor API boundary.
static class TaskbarController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int InjectRetryIntervalTicks = 25; // ~5s at 200ms per tick

    private static ReactorWindow? _trayWindow;
    private static ReactorWindow? _billboardWindow;
    private static Billboard? _billboard;
    private static string? _billboardOwnerKey;
    private static ReactorWindow? _tooltipWindow;
    private static int _billboardCount; // unique WindowKey per opened billboard

    private static TaskbarInterop.RECT _trayRectPx;
    private static TaskbarInterop.RECT _billboardRectPx;

    // Per-cell hit-test rects in physical pixels, keyed by cell key, rebuilt
    // on every anchor pass.
    private static readonly List<(string Key, TaskbarInterop.RECT Rect)> _cellRectsPx = new();

    private static (double X, double Y) _lastTrayPos = (double.MinValue, double.MinValue);
    private static (double W, double H) _lastTraySize = (0, 0);
    private static bool _injected;
    private static int _ticksSinceInjectAttempt = int.MaxValue; // inject on first tick
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private static Windows.UI.Color _taskbarColor = TaskbarTrayWindow.DefaultTaskbarColor; // average of the gradient stops, for theme derivation
    private static string? _hoverCellKey; // cell under the cursor (null = outside)
    private static bool _pressed;
    private static bool _wasLeftButtonDown; // previous tick's button state, for click-edge detection

    // Tooltip dwell: the tooltip appears only after the cursor rests on a
    // cell for ~400 ms (native tooltip timing).
    private const int TooltipDwellTicks = 8; // at 50 ms per tick
    private static string? _tooltipHoverKey; // cell the dwell timer is running for
    private static int _tooltipHoverTicks;

    // Screen-color sampling cadence. Every read from the screen DC synchronizes
    // with DWM composition and stalls for ~one display frame (measured: 17-33
    // ms), so sampling must NEVER run on the UI thread: a stalled UI thread
    // stops pumping, and with the tray parented into the taskbar the attached
    // input queues then wedge the whole taskbar. Sampling runs throttled on a
    // background thread and applies via the dispatcher.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static DateTime _lastSampleUtc = DateTime.MinValue;
    private static int _sampleInFlight; // 0/1, Interlocked guarded

    public static void Start(ReactorWindow trayWindow)
    {
        _trayWindow = trayWindow;
        PagurianLog.Host($"start hwnd={TrayWindowHwnd()} dipScale={ScaleOf(trayWindow):F3}");

        _timer = ReactorApp.UIDispatcher!.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop() => _timer?.Stop();

    // Keeps the theme and the hovered cell's overlay in sync with the sampled
    // taskbar colors. The theme flip (light/dark from sampled luminance) is
    // applied to the shared ThemeService: the text brush is mutated in place
    // and the Changed broadcast lets per-render-colored cells and billboards
    // re-render themselves. The hover overlay (SubtleFillColorSecondary on
    // hover, SubtleFillColorTertiary while pressed) is host chrome and is
    // mutated in place here.
    private static void ApplyEffectiveBrushColor()
    {
        ThemeService.Instance.Apply(Luminance(_taskbarColor) <= 140);

        if (_hoverCellKey != null)
        {
            var overlay = TaskbarTrayWindow.HoverOverlayColorFor(ThemeService.Instance.IsDark, _pressed);
            var brush = TaskbarTrayWindow.HoverBrushFor(_hoverCellKey);
            if (brush.Color != overlay)
                brush.Color = overlay;
        }
    }

    private static double Luminance(Windows.UI.Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    // Keeps the tray window's background in sync with the taskbar. The taskbar
    // is translucent, so its apparent color can vary along its length
    // (wallpaper showing through — measured deltas over 25 levels across the
    // tray's own width): no single color blends the tray in. Instead the tray
    // paints a gradient whose stops are sampled from the native taskbar
    // sliver the tray's 2-DIP inset leaves uncovered — below the tray for
    // horizontal taskbars, above and below for vertical ones — so each stop
    // matches the real taskbar color at that spot. Every sample is a trimmed
    // mean over a small patch, robust against acrylic noise and text/icon
    // glyph pixels.
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
            // One strip captures all stops: the native sliver below the tray
            // (uncovered thanks to WindowInsetYDip), spanning the tray's
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
            // Vertical taskbar: only the tray's top and bottom edges have an
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
    // tray's width.
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

    // UI thread. Two captured strips (above/below the tray) -> per-stop
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
            PagurianLog.Host($"inject result={_injected} trayHwnd={TrayWindowHwnd()} taskbarHwnd={TaskbarInterop.FindTaskbar()}");
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
        PagurianLog.Host($"set-parent trayHwnd={hwnd} taskbarHwnd={taskbar} previousParent={previousParent} style=0x{style:X8}");
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
        // and size the tray straight from the taskbar rect, minus a small
        // vertical inset: winH < taskbar.Height always, so the tray can never
        // stick out of the taskbar, whatever DPI it believes it is on.
        var horizontal = taskbar.Width >= taskbar.Height;
        var scale = (horizontal ? taskbar.Height : taskbar.Width) / TaskbarTrayWindow.WindowHeightDip;
        var windowScale = ScaleOf(_trayWindow!);
        TaskbarTrayWindow.SetContentScale(scale / windowScale);
        var totalWidthDip = TaskbarTrayLayout.TotalWidthDip;
        TaskbarTrayLayout.ReportMeasuredWidth(totalWidthDip);
        // ≥1 px even before the first layout pass (cells read 0 wide until
        // then): never hand SetWindowPos a 0-sized window.
        var winW = Math.Max(totalWidthDip * scale, 1);
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
        // mismatch can never leave the tray covering the taskbar's edge.
        xPx = Math.Clamp(xPx, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - winW));
        yPx = Math.Clamp(yPx, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - winH));

        _trayRectPx = new TaskbarInterop.RECT
        {
            Left = (int)xPx,
            Top = (int)yPx,
            Right = (int)(xPx + winW),
            Bottom = (int)(yPx + winH),
        };

        // Per-cell hit-test rects, left to right in the layout's order (the
        // same order the window renders them).
        _cellRectsPx.Clear();
        var cellLeft = xPx;
        foreach (var cell in TaskbarTrayLayout.Cells)
        {
            _cellRectsPx.Add((cell.Key, CellRect(cellLeft, yPx, cell.CellWidthDip * scale, winH)));
            cellLeft += cell.CellWidthDip * scale;
        }

        SyncTaskbarColor(in taskbar, scale);

        var sizeChanged = Math.Abs(winW - _lastTraySize.W) > 0.5 || Math.Abs(winH - _lastTraySize.H) > 0.5;
        var posChanged = Math.Abs(xPx - _lastTrayPos.X) > 0.5 || Math.Abs(yPx - _lastTrayPos.Y) > 0.5;
        if (!sizeChanged && !posChanged)
            return;

        _lastTraySize = (winW, winH);
        _lastTrayPos = (xPx, yPx);

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
        var cells = string.Join(", ", TaskbarTrayLayout.Cells.Select(c => $"{c.Key}:{c.CellWidthDip:F1}dip"));
        PagurianLog.Host(
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

    private static TaskbarInterop.RECT? CellRectPx(string cellKey)
    {
        foreach (var (key, rect) in _cellRectsPx)
        {
            if (key == cellKey)
                return rect;
        }
        return null;
    }

    private static void UpdateInteractions()
    {
        var cursor = TaskbarInterop.GetCursorPosition();

        // When a shell removes a cell (e.g. a Copilot session ends), an open
        // billboard owned by that cell goes with it.
        if (_billboardOwnerKey != null && TrayShells.FindCell(_billboardOwnerKey) == null)
            CloseBillboard();

        // A click is an up→down transition of the left button. Physical
        // clicks last ~80-120 ms, so 50 ms polling reliably catches them.
        var leftDown = TaskbarInterop.IsLeftButtonDown();
        var clickEdge = leftDown && !_wasLeftButtonDown;
        _wasLeftButtonDown = leftDown;

        // Which cell is under the cursor, if any?
        string? hoverKey = null;
        foreach (var (key, rect) in _cellRectsPx)
        {
            if (rect.Contains(cursor))
            {
                hoverKey = key;
                break;
            }
        }

        // Hover/pressed feedback like the native clock: SubtleFillColorSecondary
        // on hover, SubtleFillColorTertiary while the button is held — on the
        // hovered cell's own overlay brush (mutated live, no re-render).
        if (hoverKey != _hoverCellKey || leftDown != _pressed)
        {
            if (_hoverCellKey != null && hoverKey != _hoverCellKey)
                TaskbarTrayWindow.HoverBrushFor(_hoverCellKey).Color = TaskbarTrayWindow.HoverOverlayHidden;
            _hoverCellKey = hoverKey;
            _pressed = leftDown;
            ApplyEffectiveBrushColor();
        }

        // Tooltip: after a short dwell on a cell that has a GetTooltip
        // delegate, show its text above the cell; hide on leave and on any
        // click, and don't re-dwell while a billboard is open.
        if (clickEdge)
        {
            HideTooltip();
            _tooltipHoverTicks = 0;
        }
        if (hoverKey != _tooltipHoverKey)
        {
            _tooltipHoverKey = hoverKey;
            _tooltipHoverTicks = 0;
            HideTooltip();
        }
        else if (hoverKey != null
                 && TrayShells.FindCell(hoverKey)?.Props.GetTooltip != null
                 && _billboardWindow == null)
        {
            _tooltipHoverTicks++;
            if (_tooltipHoverTicks == TooltipDwellTicks)
                ShowTooltip(hoverKey);
        }

        // Click dispatch: a cell with a custom OnClicked gets it invoked
        // verbatim; otherwise the cell's billboard (if any) toggles — the
        // default OnClicked behavior. A click outside all cells dismisses
        // the open billboard (native flyout style; clicks inside the
        // billboard itself don't dismiss).
        if (!clickEdge)
            return;

        var cell = hoverKey != null ? TrayShells.FindCell(hoverKey) : null;
        if (cell != null)
        {
            if (cell.Props.OnClicked is { } onClicked)
            {
                CloseBillboard();
                onClicked();
            }
            else if (cell.Props.CreateBillboard != null)
            {
                ToggleBillboard(cell);
            }
        }
        else if (_billboardWindow != null && !_billboardRectPx.Contains(cursor))
        {
            CloseBillboard();
        }
    }

    // Shows the cell's tooltip above the cell (below it when the taskbar is
    // at the screen's top edge). The text is read from the delegate at show
    // time, so it is always current.
    private static void ShowTooltip(string cellKey)
    {
        var cell = TrayShells.FindCell(cellKey);
        var rect = CellRectPx(cellKey);
        if (cell == null || rect == null || _trayWindow == null)
            return;

        var text = cell.Props.GetTooltip?.Invoke();
        if (string.IsNullOrEmpty(text))
            return;

        HideTooltip();
        var scale = ScaleOf(_trayWindow);
        var xDip = rect.Value.Left / scale;
        var yDip = rect.Value.Top / scale - TooltipWindow.WindowHeightDip - 4;
        if (yDip < 0)
            yDip = rect.Value.Bottom / scale + 4;

        var tooltip = new TooltipWindow(text);
        _tooltipWindow = ReactorApp.OpenWindow(
            tooltip.CreateSpec((xDip, yDip)),
            () => tooltip);
        _tooltipWindow.SetSize(TooltipWindow.WidthFor(text), TooltipWindow.WindowHeightDip);
        _tooltipWindow.SetPosition(xDip, yDip);
        if (!_tooltipWindow.IsVisible)
            _tooltipWindow.Show();
    }

    private static void HideTooltip()
    {
        try { _tooltipWindow?.Close(); } catch { /* window may already be gone */ }
        _tooltipWindow = null;
    }

    private static void ToggleBillboard(ShellCellHandle cell)
    {
        // Clicked the owner cell of the open billboard: toggle off.
        if (_billboardWindow != null && _billboardOwnerKey == cell.Key)
        {
            CloseBillboard();
            return;
        }

        // One billboard at a time: opening one closes the other.
        CloseBillboard();

        var billboard = cell.Props.CreateBillboard!();
        if (billboard == null)
            return;

        var rect = CellRectPx(cell.Key);
        if (rect == null || _trayWindow == null)
            return;

        billboard.OwnerCell = cell;

        // Compute in DIPs — Reactor window APIs take DIPs; the physical pixel
        // rect below is only for cursor hit-testing (GetCursorPos is physical).
        var scale = ScaleOf(_trayWindow);
        var popupW = billboard.WidthDip;
        var popupH = billboard.HeightDip;
        var xDip = rect.Value.Left / scale;
        var yDip = rect.Value.Top / scale - popupH - 6;
        if (yDip < 0)
            yDip = rect.Value.Bottom / scale + 6; // taskbar at the top edge: open below

        _billboardRectPx = CellRect(xDip * scale, yDip * scale, popupW * scale, popupH * scale);

        _billboardWindow = ReactorApp.OpenWindow(
            BillboardSpec(billboard, (xDip, yDip)),
            () => billboard);
        _billboardWindow.SetSize(popupW, popupH);
        _billboardWindow.SetPosition(xDip, yDip);
        if (!_billboardWindow.IsVisible)
            _billboardWindow.Show();

        _billboard = billboard;
        _billboardOwnerKey = cell.Key;
        billboard.OnOpened();
    }

    private static void CloseBillboard()
    {
        if (_billboardWindow == null)
            return;

        try { _billboardWindow.Close(); } catch { /* window may already be gone */ }
        try { _billboard?.OnClosed(); } catch { /* module code must not break the host */ }
        _billboardWindow = null;
        _billboard = null;
        _billboardOwnerKey = null;
    }

    // Host-standard billboard chrome: borderless, rounded, acrylic,
    // NoActivate, always-on-top. Modules supply only content and size.
    private static WindowSpec BillboardSpec(Billboard billboard, (double X, double Y) positionDip) => new()
    {
        Title = billboard.Title,
        Width = billboard.WidthDip,
        Height = billboard.HeightDip,
        Style = WindowStyle.None,
        CornerStyle = WindowCornerStyle.Rounded,
        Backdrop = BackdropChoice.Of(BackdropKind.AcrylicThin),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        IsMinimizable = false,
        IsMaximizable = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = positionDip,
        Key = WindowKey.Of($"pagurian-billboard-{++_billboardCount}"),
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    private static double ScaleOf(ReactorWindow window) =>
        window.DipScale > 0 ? window.DipScale : 1.0;
}
