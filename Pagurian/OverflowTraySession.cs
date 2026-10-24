using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Pagurian;

internal readonly record struct OverflowAnchor(
    TaskbarInterop.RECT Arrow, TaskbarInterop.RECT WorkArea, double SurfaceScale, TrayEdge Edge);
internal enum OverflowTrayState { Preparing, Visible, Closing, Closed }

// One open overflow tray per owning surface. It is a temporary top-level
// window: cloaked preparation, a DWM presentation fence, activation for the
// acrylic backdrop, and focus-restoring close follow BillboardSession, while
// width is measured from the actual overflow cell strip at a fixed taskbar
// thickness.
internal sealed class OverflowTraySession
{
    private readonly Func<OverflowAnchor?> _getAnchor;
    private readonly Func<IReadOnlyList<ShellCellHandle>> _getCells;
    private readonly IThemeService _theme;
    private readonly Action<string> _log;
    private readonly Border _root = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly OverflowTrayWindow _content;
    private readonly OverflowTrayLayout _layout = new();
    private ReactorWindow? _window;
    private DispatcherQueueTimer? _watchdog;
    private bool _queued, _rendered, _opened, _started, _subscribed, _waitingFrame, _prepared;
    private long _layoutVersion;
    private nint _hwnd, _foregroundBeforeOpen;

    internal OverflowTrayState State { get; private set; } = OverflowTrayState.Preparing;
    internal ReactorWindow? Window => _window;
    internal nint Hwnd => _hwnd;
    internal OverflowLayoutSnapshot? Snapshot { get; private set; }
    internal OverflowTrayLayout Layout => _layout;

    internal OverflowTraySession(Func<OverflowAnchor?> getAnchor,
        Func<IReadOnlyList<ShellCellHandle>> getCells, IThemeService theme,
        SurfaceKey surface, Action<string> log)
    {
        _getAnchor = getAnchor;
        _getCells = getCells;
        _theme = theme;
        _log = log;
        _content = new(_layout, surface, _getCells);
        _content.LayoutChanged += OnContentLayoutChanged;
    }

    internal IEnumerable<(string Key, TaskbarInterop.RECT Rect)> CellRects =>
        State == OverflowTrayState.Visible && Snapshot is { } snapshot
            ? snapshot.Cells.Select(cell => (cell.Key, cell.BoundsPx))
            : [];

    internal RectInt32 BoundsPx => _window is { } window && State == OverflowTrayState.Visible
        ? new(window.AppWindow.Position.X, window.AppWindow.Position.Y,
            window.AppWindow.Size.Width, window.AppWindow.Size.Height)
        : default;

    internal void Start()
    {
        if (_started || State != OverflowTrayState.Preparing) return;
        _started = true;
        try
        {
            if (_getAnchor() is not { } anchor) { Close(); return; }
            _foregroundBeforeOpen = GetForegroundWindow();
            ApplyTheme();
            _theme.Changed += OnThemeChanged;
            _subscribed = true;
            ReactorApp.OpenWindow(CreateSpec(), () => _content, host =>
            {
                _window = host.OwningWindow!;
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window.NativeWindow);
                SetDwmAttribute(TransitionsForceDisabled, true);
                SetDwmAttribute(Cloak, true);
                _window.Closed += OnNativeClosed;
                _window.DpiChanged += OnDpiChanged;
                _window.NativeWindow.Content = _root;
                host.ContentTarget = _root;
                host.OnRenderComplete = (_, _, _) =>
                {
                    _rendered = true;
                    if (State == OverflowTrayState.Preparing) QueuePreparation();
                    else QueueRefresh();
                };
                Stage(anchor);
            });
            if (State != OverflowTrayState.Preparing) return;
            _watchdog = _root.DispatcherQueue.CreateTimer();
            _watchdog.Interval = TimeSpan.FromSeconds(2);
            _watchdog.IsRepeating = false;
            _watchdog.Tick += OnTimeout;
            _watchdog.Start();
            QueuePreparation();
        }
        catch (Exception ex) { _log($"overflow preparation failed: {ex}"); Close(); }
    }

    private static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian overflow",
        Width = 1,
        Height = TaskbarTrayWindow.WindowHeightDip,
        ActivateOnOpen = false,
        SizeToContent = WindowSizeToContent.Manual,
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
    };

    private double Scale => _window is { DipScale: > 0 } window ? window.DipScale : 1;

    private void ApplyTheme() => _root.RequestedTheme = _accessibility.HighContrast
        ? ElementTheme.Default : _theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    private void OnThemeChanged()
    {
        if (State is OverflowTrayState.Closing or OverflowTrayState.Closed) return;
        ApplyTheme();
        if (State == OverflowTrayState.Preparing) QueuePreparation();
        else QueueRefresh();
    }

    private void OnDpiChanged(object? sender, uint dpi)
    {
        if (State == OverflowTrayState.Preparing) QueuePreparation();
        else QueueRefresh();
    }
    private void OnContentLayoutChanged()
    {
        if (State == OverflowTrayState.Preparing) QueuePreparation();
        else QueueRefresh();
    }

    private void QueuePreparation()
    {
        if (State != OverflowTrayState.Preparing || _queued || _prepared) return;
        _queued = true;
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PrepareAndShow))
        {
            _queued = false;
            Close();
        }
    }

    private void QueueRefresh()
    {
        if (State != OverflowTrayState.Visible || _queued) return;
        _queued = true;
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RefreshVisible))
            _queued = false;
    }

    internal void Refresh() => QueueRefresh();

    private void Stage(OverflowAnchor anchor)
    {
        var window = _window!;
        var heightPx = Math.Clamp((int)Math.Ceiling(
            TaskbarTrayWindow.WindowHeightDip * anchor.SurfaceScale), 1, anchor.WorkArea.Height);
        window.AppWindow.Resize(new SizeInt32(1, heightPx));
        window.AppWindow.Move(new PointInt32(anchor.WorkArea.Left, anchor.WorkArea.Top));
    }

    private void PrepareAndShow()
    {
        _queued = false;
        if (State != OverflowTrayState.Preparing || _prepared || _window is not { } window) return;
        try
        {
            if (_getAnchor() is not { } anchor) { Close(); return; }
            if (!_rendered) return;
            ApplyTheme();
            double heightDip = TaskbarTrayWindow.WindowHeightDip * anchor.SurfaceScale / Scale;
            _root.Measure(new Size(double.PositiveInfinity, heightDip));
            double widthDip = _root.DesiredSize.Width;
            if (!double.IsFinite(widthDip) || widthDip <= 0)
            {
                _log("overflow natural width unavailable; closing");
                Close();
                return;
            }
            widthDip = Math.Clamp(widthDip, 1, anchor.WorkArea.Width / Scale);
            heightDip = Math.Clamp(heightDip, 1, anchor.WorkArea.Height / Scale);
            var bounds = Place(anchor,
                (int)Math.Ceiling(widthDip * Scale), (int)Math.Ceiling(heightDip * Scale), Scale);
            widthDip = bounds.Width / Scale;
            heightDip = bounds.Height / Scale;
            window.Update(window.Spec with
            {
                Width = widthDip,
                Height = heightDip,
                StartPosition = WindowStartPosition.Manual,
                ManualPosition = (bounds.X / Scale, bounds.Y / Scale),
            });
            window.AppWindow.MoveAndResize(bounds);
            _root.Measure(new Size(widthDip, heightDip));
            _root.Arrange(new Rect(0, 0, widthDip, heightDip));
            _prepared = true;
            _waitingFrame = true;
            CompositionTarget.Rendering += OnRendering;
            CompositionTarget.Rendered += OnRendered;
            window.Show();
        }
        catch (Exception ex) { _log($"overflow preparation failed: {ex}"); Close(); }
    }

    private void RefreshVisible()
    {
        _queued = false;
        if (State != OverflowTrayState.Visible || _window is not { } window) return;
        try
        {
            if (_getAnchor() is not { } anchor) { Close(); return; }
            ApplyTheme();
            double heightDip = TaskbarTrayWindow.WindowHeightDip * anchor.SurfaceScale / Scale;
            _root.Measure(new Size(double.PositiveInfinity, heightDip));
            double widthDip = Math.Clamp(_root.DesiredSize.Width, 1, anchor.WorkArea.Width / Scale);
            heightDip = Math.Clamp(heightDip, 1, anchor.WorkArea.Height / Scale);
            var bounds = Place(anchor,
                (int)Math.Ceiling(widthDip * Scale), (int)Math.Ceiling(heightDip * Scale), Scale);
            widthDip = bounds.Width / Scale;
            heightDip = bounds.Height / Scale;
            if (window.AppWindow.Size.Width != bounds.Width || window.AppWindow.Size.Height != bounds.Height)
            {
                window.Update(window.Spec with { Width = widthDip, Height = heightDip });
                window.AppWindow.MoveAndResize(bounds);
            }
            else if (window.AppWindow.Position.X != bounds.X || window.AppWindow.Position.Y != bounds.Y)
            {
                window.AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            }
            UpdateLayoutSnapshot();
        }
        catch (Exception ex) { _log($"overflow refresh failed: {ex}"); Close(); }
    }

    private void UpdateLayoutSnapshot()
    {
        if (_window is not { } window || State != OverflowTrayState.Visible) return;
        var size = window.AppWindow.Size;
        var bounds = new TaskbarInterop.RECT
        {
            Left = window.AppWindow.Position.X,
            Top = window.AppWindow.Position.Y,
            Right = window.AppWindow.Position.X + size.Width,
            Bottom = window.AppWindow.Position.Y + size.Height,
        };
        Snapshot = _content.Measure(++_layoutVersion, bounds, Scale);
    }

    internal static RectInt32 Place(OverflowAnchor anchor, int width, int height, double scale)
    {
        var arrow = anchor.Arrow;
        var work = anchor.WorkArea;
        width = Math.Clamp(width, 1, work.Width);
        height = Math.Clamp(height, 1, work.Height);
        int gap = height >= work.Height ? 0 : (int)Math.Ceiling(6 * scale);
        int x = anchor.Edge == TrayEdge.Right ? arrow.Right - width : arrow.Left;
        int above = arrow.Top - gap - height;
        int below = arrow.Bottom + gap;
        int y;
        if (above >= work.Top) y = above;
        else if (below + height <= work.Top + work.Height) y = below;
        else
        {
            int roomAbove = Math.Max(0, arrow.Top - work.Top);
            int roomBelow = Math.Max(0, work.Top + work.Height - arrow.Bottom);
            y = Math.Clamp(roomAbove >= roomBelow ? above : below,
                work.Top, work.Top + work.Height - height);
        }
        return new RectInt32(Math.Clamp(x, work.Left, work.Left + work.Width - width), y, width, height);
    }

    private void OnRendering(object? sender, object args) { }
    private void OnRendered(object? sender, object args)
    {
        if (!_waitingFrame || State != OverflowTrayState.Preparing || !_root.IsLoaded) return;
        StopFrameGate();
        _ = AwaitDesktopCompositionAsync();
    }

    private async Task AwaitDesktopCompositionAsync()
    {
        Exception? failure = null;
        try
        {
            int result = await Task.Run(DwmFlush).ConfigureAwait(false);
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception ex) { failure = ex; }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (State != OverflowTrayState.Preparing) return;
            if (failure is not null)
            {
                _log($"overflow composition failed: {failure}");
                Close();
                return;
            }
            Reveal();
        });
    }

    private void Reveal()
    {
        if (State != OverflowTrayState.Preparing || _window is not { } window) return;
        try
        {
            if (_getAnchor() is not { } anchor) { Close(); return; }
            var bounds = Place(anchor, window.AppWindow.Size.Width,
                window.AppWindow.Size.Height, Scale);
            window.AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            SetDwmAttribute(Cloak, false);
            window.SetNoActivate(false);
            window.Activate();
            State = OverflowTrayState.Visible;
            StopWatchdog();
            _opened = true;
            _content.ScrollToAnchor();
            UpdateLayoutSnapshot();
            _log($"overflow shown size={window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}px scale={Scale:F3}");
        }
        catch (Exception ex) { _log($"overflow reveal failed: {ex}"); Close(); }
    }

    private void OnTimeout(DispatcherQueueTimer sender, object args)
    {
        _log("overflow preparation timed out; closing without revealing empty content");
        Close();
    }

    private void StopWatchdog()
    {
        if (_watchdog is not { } timer) return;
        _watchdog = null;
        timer.Stop();
        timer.Tick -= OnTimeout;
    }

    private void StopFrameGate()
    {
        if (!_waitingFrame) return;
        _waitingFrame = false;
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendered -= OnRendered;
    }

    private const int TransitionsForceDisabled = 3;
    private const int Cloak = 13;
    private void SetDwmAttribute(int attribute, bool enabled)
    {
        int value = enabled ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int)));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    internal void Close()
    {
        if (State is OverflowTrayState.Closing or OverflowTrayState.Closed) return;
        State = OverflowTrayState.Closing;
        Snapshot = null;
        Detach();
        var window = _window;
        try
        {
            if (_foregroundBeforeOpen != 0 && GetForegroundWindow() == _hwnd)
                SetForegroundWindow(_foregroundBeforeOpen);
            window?.Hide();
        }
        catch (Exception ex) { _log($"overflow hide failed: {ex}"); }
        finally
        {
            try { window?.Close(); }
            catch (Exception ex) { _log($"overflow close failed: {ex}"); }
            finally { CompleteClose(); }
        }
    }

    private void OnNativeClosed(object? sender, EventArgs args) => CompleteClose();

    private void Detach()
    {
        StopWatchdog();
        StopFrameGate();
        if (_subscribed)
        {
            _subscribed = false;
            _theme.Changed -= OnThemeChanged;
        }
        if (_window is { } window)
        {
            window.Host.OnRenderComplete = null;
            window.DpiChanged -= OnDpiChanged;
        }
    }

    private void CompleteClose()
    {
        if (State == OverflowTrayState.Closed) return;
        State = OverflowTrayState.Closed;
        Snapshot = null;
        Detach();
        if (_window is { } window) window.Closed -= OnNativeClosed;
        if (_opened)
        {
            _opened = false;
            _log("overflow closed");
        }
    }
}
