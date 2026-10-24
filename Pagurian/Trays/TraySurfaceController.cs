using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Pagurian.Sdk;

namespace Pagurian;

// Owns the set of live tray surfaces and the input/topology observer. The
// 50 ms timer polls cursor input and the environment; every ~1.25 s the
// display topology is refreshed and the surface set reconciled:
//   - the primary display always gets a left surface (the tray icon's menu
//     needs an owner HWND even with an empty tray);
//   - every other display gets one only while TrayManager binds cells to it.
// Interaction state (hover/tooltip/click) is tracked with the owning surface
// because DIP scales and monitor bounds differ per display; the tooltip
// window and the billboard session stay process-global (one at a time).
static class TraySurfaceController
{
    private static readonly Dictionary<SurfaceKey, TraySurface> _surfaces = new();
    private static BillboardSession? _billboardSession;
    private static string? _billboardOwnerKey;
    private static ReactorWindow? _tooltipWindow;
    private static int _billboardCount;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private static string? _hoverCellKey;
    private static TraySurface? _hoverSurface;
    private static string? _tooltipHoverKey;
    private static TraySurface? _tooltipSurface;
    private static TraySurface? _overflowSurface;
    private static TaskbarInterop.RECT? _overflowArrowRect;
    private static BillboardAnchor? _billboardCachedAnchor;
    private static bool _pressed, _wasLeftButtonDown, _wasEscapeDown, _reconcileQueued;
    private static int _tooltipHoverTicks;
    private static DateTime _nextTopologyCheck;
    private const int TooltipDwellTicks = 8;

    public static void Start()
    {
        TrayManager.MonitorHistory = key =>
            DisplayTopology.TryGetRecorded(key, out var recorded) ? recorded : null;
        DisplayTopology.Refresh();
        TrayManager.SetTopology(DisplayTopology.Displays);
        TrayManager.Changed += OnTraysChanged;
        ReconcileSurfaces();
        _timer = ReactorApp.UIDispatcher!.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        TrayManager.Changed -= OnTraysChanged;
        TrayManager.MonitorHistory = null;
        CloseBillboard();
        HideTooltip();
        foreach (var surface in _surfaces.Values)
            surface.Close();
        _surfaces.Clear();
    }

    // The primary display's left surface HWND owns the native tray-icon
    // context menu (a right surface may exist without one).
    public static nint TrayWindowHwnd()
    {
        foreach (var (key, surface) in _surfaces)
            if (key.Edge == TrayEdge.Left &&
                DisplayTopology.Find(key.DisplayKey) is { IsPrimary: true })
                return surface.Hwnd;
        return _surfaces.Values.FirstOrDefault()?.Hwnd ?? 0;
    }

    private static void OnTraysChanged()
    {
        // Cells or bindings changed: prune interaction state pointing at dead
        // cells, and re-check whether the surface set should grow/shrink.
        if (_billboardOwnerKey != null && TrayManager.FindCell(_billboardOwnerKey) == null)
            CloseBillboard();
        if (_tooltipHoverKey != null && TrayManager.FindCell(_tooltipHoverKey) == null)
        {
            HideTooltip();
            _tooltipHoverKey = null;
            _tooltipSurface = null;
            _tooltipHoverTicks = 0;
        }
        if (_hoverCellKey != null && TrayManager.FindCell(_hoverCellKey) == null)
        {
            _hoverCellKey = null;
            _hoverSurface = null;
        }
        _reconcileQueued = true;
    }

    private static void Tick()
    {
        if (DateTime.UtcNow >= _nextTopologyCheck)
        {
            _nextTopologyCheck = DateTime.UtcNow.AddSeconds(1.25);
            // Reconcile unconditionally: the settings window can trigger its
            // own Refresh, and reconciliation is a cheap no-op when nothing
            // changed.
            DisplayTopology.Refresh();
            ReconcileSurfaces();
        }
        if (_reconcileQueued)
        {
            _reconcileQueued = false;
            ReconcileSurfaces();
        }
        foreach (var (key, surface) in _surfaces.ToList())
        {
            if (!surface.CheckEnvironment())
            {
                surface.Close();
                _surfaces.Remove(key);
                _reconcileQueued = true;
            }
        }
        UpdateInteractions();
    }

    private static void ReconcileSurfaces()
    {
        // Binding targets only live displays, so push the topology first.
        TrayManager.SetTopology(DisplayTopology.Displays);

        var wanted = new HashSet<SurfaceKey>();
        foreach (var display in DisplayTopology.Displays.Where(d => d.HasTaskbar))
        {
            var leftKey = new SurfaceKey(display.IdentityKey, TrayEdge.Left);
            if (display.IsPrimary || TrayManager.CellsForSurface(leftKey).Count > 0)
                wanted.Add(leftKey);

            // Right surfaces exist only while cells are bound to them; the
            // tray icon's menu never needs one, and an empty right tray
            // injects nothing.
            var rightKey = new SurfaceKey(display.IdentityKey, TrayEdge.Right);
            if (TrayManager.CellsForSurface(rightKey).Count > 0)
                wanted.Add(rightKey);
        }

        foreach (var (key, surface) in _surfaces.ToList())
        {
            if (wanted.Contains(key))
                continue;
            if (_hoverSurface == surface) { _hoverSurface = null; _hoverCellKey = null; }
            if (_tooltipSurface == surface) { _tooltipSurface = null; _tooltipHoverKey = null; HideTooltip(); }
            surface.Close();
            _surfaces.Remove(key);
            PagurianLog.Host($"tray surface {key}: closed (no longer wanted)");
        }
        foreach (var key in wanted)
        {
            if (_surfaces.ContainsKey(key))
                continue;
            if (DisplayTopology.Find(key.DisplayKey) == null)
                continue;
            var surface = new TraySurface(key);
            _surfaces.Add(key, surface);
            surface.Start();
            PagurianLog.Host($"tray surface {key}: started");
        }
    }

    private static void ApplyEffectiveBrushColor()
    {
        if (_hoverCellKey != null && _hoverSurface != null)
            TaskbarTrayWindow.HoverBrushFor(_hoverCellKey).Color =
                TaskbarTrayWindow.HoverOverlayColorFor(_hoverSurface.Theme.IsDark, _pressed);
    }

    private static TaskbarInterop.RECT? CellRectPx(string cellKey)
    {
        foreach (var surface in _surfaces.Values)
        foreach (var (key, rect) in surface.CellRects)
            if (key == cellKey)
                return rect;
        foreach (var surface in _surfaces.Values)
        foreach (var (key, rect) in surface.OverflowCellRects)
            if (key == cellKey)
                return rect;
        return null;
    }

    private static void CloseOverflow()
    {
        _overflowSurface?.CloseOverflow();
        _overflowSurface = null;
        _overflowArrowRect = null;
    }

    private static void ToggleOverflow(TraySurface surface)
    {
        if (_overflowSurface != null && _overflowSurface != surface)
            CloseOverflow();
        if (surface.OverflowArrowRect is not { } arrow ||
            !TryGetBillboardWorkArea(arrow, out var workArea))
        {
            CloseOverflow();
            return;
        }
        var anchor = new OverflowAnchor(arrow, workArea, surface.Snapshot?.Scale ?? 1, surface.Key.Edge);
        if (surface.ToggleOverflow(anchor))
        {
            _overflowSurface = surface;
            _overflowArrowRect = arrow;
        }
        else if (_overflowSurface == surface)
        {
            _overflowSurface = null;
            _overflowArrowRect = null;
        }
    }

    private static bool OverflowContains(TaskbarInterop.POINT cursor) =>
        _overflowSurface?.Overflow?.BoundsPx is { } bounds && ToNativeRect(bounds).Contains(cursor);

    private static void UpdateInteractions()
    {
        var cursor = TaskbarInterop.GetCursorPosition();

        // The overflow affordance is host-owned: it disappears as soon as its
        // snapshot no longer contains an arrow, and with its owning surface.
        if (_overflowSurface is { } overflow &&
            (!_surfaces.ContainsValue(overflow) || overflow.OverflowArrowRect == null))
            CloseOverflow();
        if (_overflowSurface?.Overflow is { State: OverflowTrayState.Visible } open &&
            _overflowSurface.OverflowArrowRect is { } currentArrow &&
            (_overflowArrowRect is not { } knownArrow || !currentArrow.Equals(knownArrow)))
        {
            _overflowArrowRect = currentArrow;
            open.Refresh();
        }

        // When a shell removes a cell (e.g. a Copilot session ends), an open
        // billboard owned by that cell goes with it. A billboard anchored from
        // a closed overflow window keeps its cached anchor until dismissal.
        if (_billboardOwnerKey != null && TrayManager.FindCell(_billboardOwnerKey) == null)
            CloseBillboard();
        else if (_billboardOwnerKey != null && _billboardCachedAnchor == null &&
                 CellRectPx(_billboardOwnerKey) == null)
            CloseBillboard();

        // A click is an up→down transition of the left button. Physical
        // clicks last ~80-120 ms, so 50 ms polling reliably catches them.
        var leftDown = TaskbarInterop.IsLeftButtonDown();
        var clickEdge = leftDown && !_wasLeftButtonDown;
        _wasLeftButtonDown = leftDown;

        // Esc closes whichever focused flyout holds the keyboard. The overflow
        // window activates at reveal, so it is foreground while open.
        var escDown = TaskbarInterop.IsEscapeDown();
        var escEdge = escDown && !_wasEscapeDown;
        _wasEscapeDown = escDown;
        if (escEdge)
        {
            var overflowHwnd = _overflowSurface?.Overflow?.Hwnd ?? 0;
            if (overflowHwnd != 0 && TaskbarInterop.GetForegroundWindowHandle() == overflowHwnd)
                CloseOverflow();
            else if (_billboardSession is { State: BillboardSessionState.Visible } focused &&
                TaskbarInterop.GetForegroundWindowHandle() == focused.Hwnd)
            {
                CloseBillboard();
            }
        }

        // Which cell or host affordance is under the cursor? Cell rects are
        // disjoint across displays and only one overflow window is open.
        string? hoverKey = null;
        TraySurface? hoverSurface = null;
        bool hoverInOverflow = false;
        foreach (var surface in _surfaces.Values)
        {
            foreach (var (key, rect) in surface.CellRects)
            {
                if (TrayManager.FindCell(key) != null && rect.Contains(cursor))
                {
                    hoverKey = key;
                    hoverSurface = surface;
                    break;
                }
            }
            if (hoverKey == null && surface.OverflowArrowRect is { } arrow && arrow.Contains(cursor))
            {
                hoverKey = TaskbarTrayWindow.OverflowArrowKey;
                hoverSurface = surface;
            }
            if (hoverKey != null)
                break;
        }
        if (hoverKey == null && _overflowSurface is { } openOverflow)
        {
            foreach (var (key, rect) in openOverflow.OverflowCellRects)
            {
                if (TrayManager.FindCell(key) != null && rect.Contains(cursor))
                {
                    hoverKey = key;
                    hoverSurface = openOverflow;
                    hoverInOverflow = true;
                    break;
                }
            }
        }

        // Hover/pressed feedback like the native clock: SubtleFillColorSecondary
        // on hover, SubtleFillColorTertiary while the button is held — on the
        // hovered cell's own overlay brush (mutated live, no re-render), tinted
        // by the surface's sampled theme.
        if (hoverKey != _hoverCellKey || leftDown != _pressed)
        {
            if (_hoverCellKey != null && hoverKey != _hoverCellKey)
                TaskbarTrayWindow.HoverBrushFor(_hoverCellKey).Color = TaskbarTrayWindow.HoverOverlayHidden;
            _hoverCellKey = hoverKey;
            _hoverSurface = hoverSurface;
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
            _tooltipSurface = hoverSurface;
            _tooltipHoverTicks = 0;
            HideTooltip();
        }
        else if (hoverKey != null && hoverSurface != null
                 && TrayManager.FindCell(hoverKey)?.Props.GetTooltip != null
                 && (_billboardSession == null || _billboardSession.State == BillboardSessionState.Closed))
        {
            _tooltipHoverTicks++;
            if (_tooltipHoverTicks == TooltipDwellTicks)
                ShowTooltip(hoverKey, hoverSurface);
        }

        // Click dispatch: a cell with a custom OnClicked gets it invoked
        // verbatim; otherwise the cell's billboard (if any) toggles — the
        // default OnClicked behavior. A click outside all cells dismisses
        // the open flyouts (native flyout style; clicks inside them don't).
        if (!clickEdge)
            return;

        if (hoverKey == TaskbarTrayWindow.OverflowArrowKey && hoverSurface != null)
        {
            CloseBillboard();
            HideTooltip();
            ToggleOverflow(hoverSurface);
            return;
        }

        var cell = hoverKey != null ? TrayManager.FindCell(hoverKey) : null;
        if (cell != null && hoverSurface != null)
        {
            BillboardAnchor? cachedAnchor = null;
            if (hoverInOverflow)
            {
                if (CellRectPx(cell.Key) is { } owner &&
                    TryGetBillboardWorkArea(owner, out var workArea))
                    cachedAnchor = new(ToPixelRect(owner), ToPixelRect(workArea));
                CloseOverflow();
            }
            else if (_overflowSurface != null)
            {
                CloseOverflow();
            }
            if (cell.Props.OnClicked is { } onClicked)
            {
                CloseBillboard();
                onClicked();
            }
            else if (cell.Props.CreateBillboard != null)
            {
                ToggleBillboard(cell, hoverSurface, cachedAnchor);
            }
        }
        else
        {
            if (_overflowSurface != null && !OverflowContains(cursor) &&
                hoverKey != TaskbarTrayWindow.OverflowArrowKey)
                CloseOverflow();
            if (_billboardSession is { State: not BillboardSessionState.Closed } session &&
                !ToNativeRect(session.BoundsPx).Contains(cursor))
            {
                CloseBillboard();
            }
        }
    }

    // Shows the cell's tooltip above the cell (below it when there is no room
    // above on that display). The text is read from the delegate at show
    // time, so it is always current.
    private static void ShowTooltip(string cellKey, TraySurface surface)
    {
        var cell = TrayManager.FindCell(cellKey);
        var rect = CellRectPx(cellKey);
        if (cell == null || rect == null || surface.Window == null)
            return;

        var text = cell.Props.GetTooltip?.Invoke();
        if (string.IsNullOrEmpty(text))
            return;

        HideTooltip();
        var scale = ScaleOf(surface.Window);
        var xDip = rect.Value.Left / scale;
        var yDip = rect.Value.Top / scale - TooltipWindow.WindowHeightDip - 4;
        // Secondary displays may sit at negative coordinates; flip against
        // this display's top edge, not the primary's origin.
        if (yDip < surface.MonitorRect.Top / scale)
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

    private static void ToggleBillboard(ShellCellHandle cell, TraySurface surface,
        BillboardAnchor? cachedAnchor = null)
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
            if (billboard == null || (cachedAnchor == null && CellRectPx(cell.Key) == null))
                return;
            billboard.OwnerCell = cell;
            _billboardOwnerKey = cell.Key;
            _billboardCachedAnchor = cachedAnchor;
            Func<BillboardAnchor?> getAnchor = cachedAnchor is { } anchor
                ? () => TrayManager.FindCell(cell.Key) == null ? null : anchor
                : () => GetBillboardAnchor(cell.Key);
            var session = new BillboardSession(
                BillboardSpec(billboard), billboard, surface.Theme,
                getAnchor, billboard.OnOpened, billboard.OnClosed,
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
        if (TrayManager.FindCell(ownerKey) == null || CellRectPx(ownerKey) is not { } owner ||
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
        // monitor discovery transiently fails; the primary screen bounds are
        // the closest safe approximation then.
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
        _billboardCachedAnchor = null;
        session?.Close();
    }

    // Host-standard billboard chrome: borderless, rounded, acrylic,
    // always-on-top, activated at reveal (25H2 renders SystemBackdrop only
    // for focused windows). Modules supply only content and size.
    private static WindowSpec BillboardSpec(Billboard billboard) =>
        BillboardSession.CreateSpec(billboard.Title, billboard.WidthDip, billboard.HeightDip) with
    {
        Key = WindowKey.Of($"pagurian-billboard-{++_billboardCount}"),
        Icon = WindowIcon.FromPath(AppAssets.ApplicationIconPath),
    };

    private static double ScaleOf(ReactorWindow window) =>
        window.DipScale > 0 ? window.DipScale : 1.0;
}
