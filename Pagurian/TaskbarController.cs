using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Pagurian.Sdk;

namespace Pagurian;

// The timer observes the external taskbar and polls input. TrayWindowSession
// owns rendering, measurement, native geometry and presentation.
static class TaskbarController
{
    private static TrayWindowSession? _traySession;
    private static ReactorWindow? _trayWindow => _traySession?.Window;
    private static BillboardSession? _billboardSession;
    private static string? _billboardOwnerKey;
    private static ReactorWindow? _tooltipWindow;
    private static int _billboardCount;
    private static DateTime _nextSessionAttempt;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private static string? _hoverCellKey, _tooltipHoverKey;
    private static bool _pressed, _wasLeftButtonDown;
    private static int _tooltipHoverTicks;
    private const int TooltipDwellTicks = 8;
    private static IEnumerable<(string Key, TaskbarInterop.RECT Rect)> CellRects =>
        _traySession is { State: TrayWindowSessionState.Visible, Snapshot: { } snapshot }
            ? snapshot.Cells.Select(c => (c.Key, c.BoundsPx)) : [];

    public static void Start()
    {
        TrayShells.Changed += OnCellsChanged;
        ThemeService.Instance.Changed += ApplyEffectiveBrushColor;
        StartSession();
        _timer = ReactorApp.UIDispatcher!.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private static void StartSession()
    {
        _nextSessionAttempt = DateTime.UtcNow.AddSeconds(1.25);
        _traySession?.Close();
        _traySession = new TrayWindowSession();
        _traySession.Start();
    }

    private static void OnCellsChanged()
    {
        if (_billboardOwnerKey != null && TrayShells.FindCell(_billboardOwnerKey) == null)
            CloseBillboard();
        if (_tooltipHoverKey != null && TrayShells.FindCell(_tooltipHoverKey) == null)
        {
            HideTooltip();
            _tooltipHoverKey = null;
            _tooltipHoverTicks = 0;
        }
        if (_hoverCellKey != null && TrayShells.FindCell(_hoverCellKey) == null)
            _hoverCellKey = null;
    }

    public static void Stop()
    {
        _timer?.Stop();
        TrayShells.Changed -= OnCellsChanged;
        ThemeService.Instance.Changed -= ApplyEffectiveBrushColor;
        CloseBillboard();
        HideTooltip();
        _traySession?.Close();
        _traySession = null;
    }

    private static void Tick()
    {
        if (_traySession == null) return;
        if (!_traySession.CheckEnvironment() && DateTime.UtcNow >= _nextSessionAttempt) StartSession();
        UpdateInteractions();
    }

    public static nint TrayWindowHwnd() => _traySession?.Hwnd ?? 0;

    private static void ApplyEffectiveBrushColor()
    {
        if (_hoverCellKey != null)
            TaskbarTrayWindow.HoverBrushFor(_hoverCellKey).Color =
                TaskbarTrayWindow.HoverOverlayColorFor(ThemeService.Instance.IsDark, _pressed);
    }

    private static TaskbarInterop.RECT? CellRectPx(string cellKey)
    {
        foreach (var (key, rect) in CellRects)
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
        foreach (var (key, rect) in CellRects)
        {
            if (TrayShells.FindCell(key) != null && rect.Contains(cursor))
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
                 && (_billboardSession == null || _billboardSession.State == BillboardSessionState.Closed))
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
        else if (_billboardSession is { State: not BillboardSessionState.Closed } session &&
                 !ToNativeRect(session.BoundsPx).Contains(cursor))
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
        if (_billboardSession is { State: not BillboardSessionState.Closed } &&
            _billboardOwnerKey == cell.Key)
        {
            CloseBillboard();
            return;
        }
        CloseBillboard();
        HideTooltip();

        try
        {
            var billboard = cell.Props.CreateBillboard!();
            if (billboard == null || CellRectPx(cell.Key) == null || _trayWindow == null)
                return;
            billboard.OwnerCell = cell;
            _billboardOwnerKey = cell.Key;
            var session = new BillboardSession(
                BillboardSpec(billboard), billboard, ThemeService.Instance,
                () => GetBillboardAnchor(cell.Key), billboard.OnOpened, billboard.OnClosed,
                message => PagurianLog.Host($"{message} owner={cell.Key}"));
            _billboardSession = session;
            session.Start();
        }
        catch (Exception ex)
        {
            PagurianLog.Host($"billboard creation failed owner={cell.Key}: {ex}");
            CloseBillboard();
        }
    }

    private static BillboardAnchor? GetBillboardAnchor(string ownerKey)
    {
        if (TrayShells.FindCell(ownerKey) == null || CellRectPx(ownerKey) is not { } owner ||
            !TryGetBillboardWorkArea(owner, out var workArea))
            return null;
        return new BillboardAnchor(ToPixelRect(owner), ToPixelRect(workArea));
    }

    private static Windows.Graphics.RectInt32 ToPixelRect(TaskbarInterop.RECT rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static TaskbarInterop.RECT ToNativeRect(Windows.Graphics.RectInt32 rect) => new()
    {
        Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height,
    };

    private static bool TryGetBillboardWorkArea(
        in TaskbarInterop.RECT ownerRectPx,
        out TaskbarInterop.RECT workAreaPx)
    {
        if (TaskbarInterop.TryGetMonitorWorkArea(ownerRectPx, out workAreaPx))
            return true;

        // MonitorFromRect/GetMonitorInfo are available on every supported
        // Windows version, but keep billboard opening functional if native
        // monitor discovery transiently fails. The tray currently belongs to
        // Shell_TrayWnd on the primary display, so its screen rectangle is the
        // closest safe approximation to the previous placement behavior.
        if (TaskbarInterop.TryGetDesktopRects(
                out _,
                out var primaryScreenPx))
        {
            workAreaPx = primaryScreenPx;
            PagurianLog.Host("billboard monitor work area unavailable; using primary screen bounds");
            return true;
        }

        workAreaPx = default;
        return false;
    }

    private static void CloseBillboard()
    {
        var session = _billboardSession;
        // Clear controller ownership before module callbacks can reenter it.
        _billboardSession = null;
        _billboardOwnerKey = null;
        session?.Close();
    }

    // Host-standard billboard chrome: borderless, rounded, acrylic,
    // NoActivate, always-on-top. Modules supply only content and size.
    private static WindowSpec BillboardSpec(Billboard billboard) =>
        BillboardSession.CreateSpec(billboard.Title, billboard.WidthDip, billboard.HeightDip) with
    {
        Key = WindowKey.Of($"pagurian-billboard-{++_billboardCount}"),
        Icon = WindowIcon.FromPath(AppAssets.ApplicationIconPath),
    };

    private static double ScaleOf(ReactorWindow window) =>
        window.DipScale > 0 ? window.DipScale : 1.0;
}
