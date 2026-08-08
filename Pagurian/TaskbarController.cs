using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Reactor;

namespace Pagurian;

// Owns the ongoing behaviors of the app:
//  1. Keeps the icon window — a horizontal container of widget cells (the
//     clock replica plus one cell per tracked Copilot session) — anchored to
//     the left-bottom corner of the taskbar, resizing it as cells come and go.
//  2. Per-cell interaction by polling the cursor position and left mouse
//     button: native hover/pressed highlights, a tooltip with the session
//     name after a short hover dwell, and popups — a click on the clock cell
//     toggles the Hello popup, a click on a session cell toggles that
//     session's popup (one popup at a time, native flyout style), an outside
//     click dismisses whichever is open.
// Runs on a DispatcherQueueTimer on the UI thread.
//
// The icon window is injected into the taskbar via SetParent (same approach as
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

    private static ReactorWindow? _iconWindow;
    private static ReactorWindow? _popupWindow;
    private static ReactorWindow? _sessionPopupWindow;
    private static string? _sessionPopupId;
    private static ReactorWindow? _tooltipWindow;

    private static TaskbarInterop.RECT _iconRectPx;
    private static TaskbarInterop.RECT _popupRectPx;
    private static TaskbarInterop.RECT _sessionPopupRectPx;

    // Per-cell hit-test rects in physical pixels (ClockWidgetId first, then
    // session ids in tracker order), rebuilt on every anchor pass.
    private static readonly List<(string Id, TaskbarInterop.RECT Rect)> _cellRectsPx = new();

    private static (double X, double Y) _lastIconPos = (double.MinValue, double.MinValue);
    private static (double W, double H) _lastIconSize = (0, 0);
    private static bool _injected;
    private static int _ticksSinceInjectAttempt = int.MaxValue; // inject on first tick
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private static Windows.UI.Color _taskbarColor = TaskbarIconWindow.DefaultTaskbarColor; // average of the gradient stops, for theme derivation
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

    public static void Start(ReactorWindow iconWindow)
    {
        _iconWindow = iconWindow;

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
            TaskbarIconWindow.TextBrush.Color = TaskbarIconWindow.TextColorFor(isDark);
            // Session status colors are per-render brushes, so the container
            // and any open session popup must re-render to pick the new theme.
            CopilotSessionTracker.NotifyChanged();
        }

        if (_hoverWidgetId != null)
        {
            var overlay = TaskbarIconWindow.HoverOverlayColorFor(isDark, _pressed);
            var brush = TaskbarIconWindow.HoverBrushFor(_hoverWidgetId);
            if (brush.Color != overlay)
                brush.Color = overlay;
        }
    }

    private static double Luminance(Windows.UI.Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    // Keeps the icon window's background in sync with the taskbar. The taskbar
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
        var brush = TaskbarIconWindow.TaskbarColorBrush;

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
            var sx = _iconRectPx.Left;
            var sy = _iconRectPx.Bottom;
            var sw = Math.Max(1, _iconRectPx.Right - _iconRectPx.Left);
            var sh = Math.Max(1, taskbar.Bottom - sy); // rows [iconBottom, taskbarBottom - 1]
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
            var yTop = Math.Clamp(_iconRectPx.Top - 3, taskbar.Top, taskbar.Bottom - 3);
            var yBottom = Math.Clamp(_iconRectPx.Bottom + 1, taskbar.Top, taskbar.Bottom - 3);
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
        var count = TaskbarIconWindow.GradientStopCount;
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

        var count = TaskbarIconWindow.GradientStopCount;
        var colors = new Windows.UI.Color[count];
        for (var i = 0; i < count; i++)
            colors[i] = LerpColor(tc, bc, StopFraction(i, count));
        ApplyStopColors(colors);
    }

    // UI thread. Writes the new stop colors into the live gradient brush (no
    // re-render needed) and re-derives the theme from their average.
    private static void ApplyStopColors(Windows.UI.Color[] colors)
    {
        var brush = TaskbarIconWindow.TaskbarColorBrush;
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
        if (_iconWindow == null)
            return;

        EnsureInjected();
        AnchorIconWindow();
        UpdateInteractions();
    }

    private static void EnsureInjected()
    {
        if (_injected)
        {
            var hwnd = IconWindowHwnd();
            var taskbar = TaskbarInterop.FindTaskbar();

            if (hwnd != IntPtr.Zero &&
                TaskbarInterop.IsWindow(hwnd) &&
                taskbar != IntPtr.Zero &&
                TaskbarInterop.GetAncestor(hwnd, TaskbarInterop.GA_PARENT) == taskbar)
            {
                return; // still injected, nothing to do
            }

            // Explorer restart destroys Shell_TrayWnd and every child window with
            // it. Recreate the icon window and re-inject on this tick.
            _injected = false;
            try { _iconWindow!.Close(); } catch { /* native window may already be gone */ }
            _iconWindow = ReactorApp.OpenWindow(
                TaskbarIconWindow.CreateSpec(), () => new TaskbarIconWindow());
            _lastIconPos = (double.MinValue, double.MinValue);
            _lastIconSize = (0, 0);
            _ticksSinceInjectAttempt = int.MaxValue;
        }

        if (_ticksSinceInjectAttempt >= InjectRetryIntervalTicks)
        {
            _ticksSinceInjectAttempt = 0;
            _injected = TryInject();
        }
        else
        {
            _ticksSinceInjectAttempt++;
        }
    }

    private static bool TryInject()
    {
        var taskbar = TaskbarInterop.FindTaskbar();
        var hwnd = IconWindowHwnd();
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
        return TaskbarInterop.SetParent(hwnd, taskbar) != IntPtr.Zero;
    }

    // HWND of the icon window, or IntPtr.Zero when it is gone (e.g. right after
    // an Explorer restart destroyed it). Safe to call any time.
    public static IntPtr IconWindowHwnd()
    {
        try
        {
            return _iconWindow == null
                ? IntPtr.Zero
                : Win32Interop.GetWindowFromWindowId(_iconWindow.AppWindow.Id);
        }
        catch
        {
            return IntPtr.Zero; // window already destroyed (e.g. by an Explorer restart)
        }
    }

    private static void AnchorIconWindow()
    {
        if (!TaskbarInterop.TryGetTaskbarRect(out var taskbar))
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
        var scale = (horizontal ? taskbar.Height : taskbar.Width) / TaskbarIconWindow.WindowHeightDip;
        var winW = TaskbarIconWindow.TotalWidthDip() * scale;
        var winH = (TaskbarIconWindow.WindowHeightDip - 2 * TaskbarIconWindow.WindowInsetYDip) * scale;

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

        _iconRectPx = new TaskbarInterop.RECT
        {
            Left = (int)xPx,
            Top = (int)yPx,
            Right = (int)(xPx + winW),
            Bottom = (int)(yPx + winH),
        };

        // Per-cell hit-test rects, left to right: the clock cell first, then
        // one cell per tracked Copilot session in first-seen order (the same
        // order the window renders them).
        _cellRectsPx.Clear();
        var cellLeft = xPx;
        _cellRectsPx.Add((TaskbarIconWindow.ClockWidgetId, CellRect(cellLeft, yPx,
            TaskbarIconWindow.ClockCellWidthDip * scale, winH)));
        cellLeft += TaskbarIconWindow.ClockCellWidthDip * scale;
        foreach (var session in CopilotSessionTracker.Sessions)
        {
            _cellRectsPx.Add((session.SessionId, CellRect(cellLeft, yPx,
                TaskbarIconWindow.SessionCellWidthDip * scale, winH)));
            cellLeft += TaskbarIconWindow.SessionCellWidthDip * scale;
        }

        SyncTaskbarColor(in taskbar, scale);

        var sizeChanged = Math.Abs(winW - _lastIconSize.W) > 0.5 || Math.Abs(winH - _lastIconSize.H) > 0.5;
        var posChanged = Math.Abs(xPx - _lastIconPos.X) > 0.5 || Math.Abs(yPx - _lastIconPos.Y) > 0.5;
        if (!sizeChanged && !posChanged)
            return;

        _lastIconSize = (winW, winH);
        _lastIconPos = (xPx, yPx);

        if (_injected)
        {
            var hwnd = IconWindowHwnd();
            if (hwnd == IntPtr.Zero)
                return;

            // Child window: coordinates are relative to the taskbar's client area.
            TaskbarInterop.SetWindowPos(hwnd, TaskbarInterop.HWND_TOP,
                (int)(xPx - taskbar.Left), (int)(yPx - taskbar.Top),
                (int)winW, (int)winH,
                TaskbarInterop.SWP_NOACTIVATE);
        }
        else
        {
            // Floating fallback: Reactor window APIs take DIPs, not pixels;
            // convert with the window's own DPI scale.
            var winScale = ScaleOf(_iconWindow!);
            if (sizeChanged)
                _iconWindow!.SetSize(winW / winScale, winH / winScale);
            if (posChanged)
                _iconWindow!.SetPosition(xPx / winScale, yPx / winScale);
        }
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
                TaskbarIconWindow.HoverBrushFor(_hoverWidgetId).Color = TaskbarIconWindow.HoverOverlayHidden;
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
        else if (hoverId != null && hoverId != TaskbarIconWindow.ClockWidgetId && !AnyPopupVisible())
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

        if (hoverId == TaskbarIconWindow.ClockWidgetId)
        {
            HideSessionPopup();
            var popup = _popupWindow;
            if (popup is { IsVisible: true })
                popup.Hide();
            else
                EnsurePopupVisible();
        }
        else if (hoverId != null)
        {
            if (_popupWindow is { IsVisible: true })
                _popupWindow.Hide();
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
        }
    }

    private static bool AnyPopupVisible() =>
        _popupWindow is { IsVisible: true } || _sessionPopupWindow is { IsVisible: true };

    // Shows the session-name tooltip above the session's cell (below it when
    // the taskbar is at the screen's top edge).
    private static void ShowTooltip(string sessionId)
    {
        var session = CopilotSessionTracker.Find(sessionId);
        var cell = CellRectPx(sessionId);
        if (session == null || cell == null || _iconWindow == null)
            return;

        HideTooltip();
        var scale = ScaleOf(_iconWindow);
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
        if (cell == null || _iconWindow == null)
            return;

        var scale = ScaleOf(_iconWindow);
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

    private static void EnsurePopupVisible()
    {
        var scale = ScaleOf(_iconWindow!);
        var popupW = HoverPopupWindow.WindowWidthDip;  // DIPs
        var popupH = HoverPopupWindow.WindowHeightDip; // DIPs

        // Compute in DIPs — Reactor window APIs take DIPs; the physical pixel
        // rect below is only for cursor hit-testing (GetCursorPos is physical).
        var xDip = _iconRectPx.Left / scale;
        var yDip = _iconRectPx.Top / scale - popupH - 6;
        if (yDip < 0)
            yDip = _iconRectPx.Bottom / scale + 6; // taskbar at the top edge: open below

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
