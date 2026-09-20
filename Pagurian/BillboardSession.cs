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

internal readonly record struct BillboardAnchor(RectInt32 Owner, RectInt32 WorkArea);
internal enum BillboardSessionState { Preparing, Visible, Closing, Closed }

// One opening, including its callbacks. Native window geometry is never
// evidence that module content has finished measuring.
internal sealed class BillboardSession(
    WindowSpec spec, Component content, IThemeService theme,
    Func<BillboardAnchor?> getAnchor, Action onOpened, Action onClosed, Action<string> log)
{
    private readonly Border _root = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private ReactorWindow? _window;
    private DispatcherQueueTimer? _watchdog;
    private bool _queued, _rendered, _opened, _started, _subscribed;
    private bool _waitingForFrame;
    private bool _prepared;
    private nint _hwnd;
    private RectInt32? _monitorWorkArea;
    private double _monitorScale;

    internal BillboardSessionState State { get; private set; } = BillboardSessionState.Preparing;
    internal ReactorWindow? Window => _window;
    internal static WindowSpec CreateSpec(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
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
        // Manual positioning is selected together with its measured coordinates
        // in PrepareAndShow. Reactor validates the spec before configure/mount.
    };

    internal RectInt32 BoundsPx => _window is { } window && State == BillboardSessionState.Visible
        ? new(window.AppWindow.Position.X, window.AppWindow.Position.Y,
            window.AppWindow.Size.Width, window.AppWindow.Size.Height) : default;

    internal void Start()
    {
        if (_started || State != BillboardSessionState.Preparing) return;
        _started = true;
        try
        {
            if (getAnchor() is not { } anchor) { Close(); return; }
            ApplyTheme();
            theme.Changed += OnThemeChanged;
            _subscribed = true;
            _uiSettings.ColorValuesChanged += OnHighContrastChanged;
            ReactorApp.OpenWindow(spec with
            {
                ActivateOnOpen = false,
                SizeToContent = WindowSizeToContent.Manual,
            }, () => content, host =>
            {
                // Configure precedes Mount, which can render synchronously.
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
                    QueuePreparation();
                };
                SetMonitorConstraints(anchor);
            });
            if (State != BillboardSessionState.Preparing) return;
            _watchdog = _root.DispatcherQueue.CreateTimer();
            _watchdog.Interval = TimeSpan.FromSeconds(2);
            _watchdog.IsRepeating = false;
            _watchdog.Tick += OnTimeout;
            _watchdog.Start();
            QueuePreparation();
        }
        catch (Exception ex) { log($"billboard preparation failed: {ex}"); Close(); }
    }

    private void ApplyTheme() => _root.RequestedTheme = _accessibility.HighContrast
        ? ElementTheme.Default : theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    private void OnThemeChanged()
    {
        if (State is BillboardSessionState.Closing or BillboardSessionState.Closed) return;
        ApplyTheme();
        QueuePreparation();
    }

    private void OnHighContrastChanged(UISettings sender, object args) =>
        _root.DispatcherQueue.TryEnqueue(OnThemeChanged);
    private void OnDpiChanged(object? sender, uint dpi) => QueuePreparation();

    private void QueuePreparation()
    {
        if (State != BillboardSessionState.Preparing || _queued || _prepared) return;
        _queued = true;
        // Reconciliation/effects finish first. Every queued callback belongs
        // to this session, never to the controller's current window slot.
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PrepareAndShow))
        {
            _queued = false;
            Close();
        }
    }

    private double Scale => _window is { DipScale: > 0 } window ? window.DipScale : 1;

    private void SetMonitorConstraints(BillboardAnchor anchor)
    {
        var window = _window!;
        if (_monitorWorkArea == anchor.WorkArea && _monitorScale == Scale) return;
        // Keep the entire staging rectangle inside the owner monitor. Moving
        // to a cell near its right edge could otherwise select the next DPI.
        window.AppWindow.Resize(new SizeInt32(
            Math.Clamp(ToPixels(spec.Width, Scale), 1, anchor.WorkArea.Width),
            Math.Clamp(ToPixels(spec.Height, Scale), 1, anchor.WorkArea.Height)));
        window.AppWindow.Move(new PointInt32(anchor.WorkArea.X, anchor.WorkArea.Y));
        double scale = Scale;
        double width = Math.Min(spec.Width, anchor.WorkArea.Width / scale);
        double height = Math.Min(spec.Height, anchor.WorkArea.Height / scale);
        window.Update(window.Spec with
        {
            Width = width, Height = height,
            MaxWidth = anchor.WorkArea.Width / scale,
            MaxHeight = anchor.WorkArea.Height / scale,
        });
        window.SetSize(width, height);
        _monitorWorkArea = anchor.WorkArea;
        _monitorScale = scale;
    }

    private void PrepareAndShow()
    {
        _queued = false;
        if (State != BillboardSessionState.Preparing || _prepared || _window is not { } window) return;
        try
        {
            if (getAnchor() is not { } anchor) { Close(); return; }
            if (!_rendered || _root.Child is not FrameworkElement)
                return; // A subsequent render or the watchdog resolves this.
            SetMonitorConstraints(anchor);
            ApplyTheme();
            double scale = Scale;
            int widthPx = Math.Clamp(ToPixels(spec.Width, scale), 1, anchor.WorkArea.Width);
            double width = widthPx / scale;
            _root.InvalidateMeasure();
            // Ask for natural height first. A finite work-area height would
            // make Flex/star layouts fill the monitor instead of fitting their
            // content. Clamp the result, then measure/arrange the viewport.
            _root.Measure(new Size(width, double.PositiveInfinity));
            double height = _root.DesiredSize.Height;
            if (!double.IsFinite(height) || height <= 0)
            {
                height = spec.Height;
                log("billboard measurement unavailable; using declared height");
            }
            int heightPx = Math.Clamp(ToPixels(height, scale), 1, anchor.WorkArea.Height);
            var bounds = Place(anchor, widthPx, heightPx, scale);
            // Reactor reapplies ManualPosition on first Show, so update the
            // spec as well as the physical bounds before revealing the HWND.
            window.Update(window.Spec with
            {
                Width = width, Height = heightPx / scale,
                StartPosition = WindowStartPosition.Manual,
                ManualPosition = (bounds.X / scale, bounds.Y / scale),
            });
            window.AppWindow.MoveAndResize(bounds);
            _root.Measure(new Size(width, heightPx / scale));
            _root.Arrange(new Rect(0, 0, width, heightPx / scale));
            if (State != BillboardSessionState.Preparing) return;
            // Hidden Measure/Arrange does not produce the first compositor
            // surface. Let XAML load and paint while DWM keeps this HWND cloaked.
            _prepared = true;
            _waitingForFrame = true;
            CompositionTarget.Rendering += OnRendering;
            CompositionTarget.Rendered += OnRendered;
            window.Show();
        }
        catch (Exception ex) { log($"billboard preparation failed: {ex}"); Close(); }
    }

    private void OnRendering(object? sender, object args)
    {
        // A temporary Rendering subscription requests frames while preparing.
        // It is detached on reveal/cancellation, so idle popups do not animate.
    }

    private void OnRendered(object? sender, object args)
    {
        if (!_waitingForFrame || State != BillboardSessionState.Preparing ||
            !_root.IsLoaded || _root.Child is null)
            return;
        StopFrameGate();
        // XAML has rendered, but its surface can still be queued for desktop
        // presentation. Do not expose the HWND until that work has completed.
        // Static content need not produce another dirty XAML frame.
        _ = AwaitDesktopCompositionAsync();
    }

    private async Task AwaitDesktopCompositionAsync()
    {
        // DwmFlush blocks until the application's queued DirectX updates have
        // been presented. Never block the UI thread shared with Explorer.
        Exception? failure = null;
        try
        {
            int result = await Task.Run(DwmFlush).ConfigureAwait(false);
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception ex) { failure = ex; }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (State != BillboardSessionState.Preparing) return;
            if (failure is not null)
            {
                log($"billboard composition failed: {failure}");
                Close();
                return;
            }
            Reveal();
        });
    }

    private void Reveal()
    {
        if (State != BillboardSessionState.Preparing || _window is not { } window) return;
        try
        {
            if (getAnchor() is not { } anchor) { Close(); return; }
            if (_monitorWorkArea != anchor.WorkArea || _monitorScale != Scale)
            {
                _prepared = false;
                QueuePreparation();
                return;
            }
            // A tray cell can move while the first frame is being produced.
            // Re-anchor the finished surface without changing its fitted size.
            var bounds = Place(anchor, window.AppWindow.Size.Width, window.AppWindow.Size.Height, Scale);
            window.AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            SetDwmAttribute(Cloak, false);
            State = BillboardSessionState.Visible;
            StopWatchdog();
            _opened = true;
            log($"billboard shown size={window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}px scale={Scale:F3}");
            onOpened();
        }
        catch (Exception ex) { log($"billboard reveal failed: {ex}"); Close(); }
    }

    private void StopFrameGate()
    {
        if (!_waitingForFrame) return;
        _waitingForFrame = false;
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendered -= OnRendered;
    }

    // DWMWA_CLOAK keeps a window composed without exposing it to the desktop.
    // Disabling DWM transitions also prevents a closing animation from showing
    // a snapshot of content/backdrop teardown after Hide.
    private const int TransitionsForceDisabled = 3;
    private const int Cloak = 13;
    private void SetDwmAttribute(int attribute, bool enabled)
    {
        int value = enabled ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int)));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    internal static RectInt32 Place(BillboardAnchor anchor, int width, int height, double scale)
    {
        var owner = anchor.Owner;
        var work = anchor.WorkArea;
        width = Math.Clamp(width, 1, work.Width);
        height = Math.Clamp(height, 1, work.Height);
        int gap = height >= work.Height ? 0 : ToPixels(6, scale);
        int x = Math.Clamp(owner.X, work.X, work.X + work.Width - width);
        int above = owner.Y - gap - height;
        int below = owner.Y + owner.Height + gap;
        int y;
        if (above >= work.Y) y = above;
        else if (below + height <= work.Y + work.Height) y = below;
        else
        {
            int roomAbove = Math.Max(0, owner.Y - work.Y);
            int roomBelow = Math.Max(0, work.Y + work.Height - owner.Y - owner.Height);
            y = Math.Clamp(roomAbove >= roomBelow ? above : below, work.Y, work.Y + work.Height - height);
        }
        return new RectInt32(x, y, width, height);
    }

    private static int ToPixels(double dip, double scale) =>
        (int)Math.Clamp(Math.Ceiling(dip * scale), 1, int.MaxValue);

    private void OnTimeout(DispatcherQueueTimer sender, object args)
    {
        log("billboard preparation timed out; closing without revealing empty content");
        Close();
    }

    private void StopWatchdog()
    {
        if (_watchdog is not { } timer) return;
        _watchdog = null;
        timer.Stop();
        timer.Tick -= OnTimeout;
    }

    internal void Close()
    {
        if (State is BillboardSessionState.Closing or BillboardSessionState.Closed) return;
        State = BillboardSessionState.Closing;
        Detach();
        var window = _window;
        try
        {
            // Keep content and acrylic intact until the HWND is hidden.
            // Visible WinUI teardown can otherwise expose a white frame.
            window?.Hide();
        }
        catch (Exception ex) { log($"billboard hide failed: {ex}"); }
        finally
        {
            try { window?.Close(); }
            catch (Exception ex) { log($"billboard close failed: {ex}"); }
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
            theme.Changed -= OnThemeChanged;
            _uiSettings.ColorValuesChanged -= OnHighContrastChanged;
        }
        if (_window is { } window)
        {
            window.Host.OnRenderComplete = null;
            window.DpiChanged -= OnDpiChanged;
        }
    }

    private void CompleteClose()
    {
        if (State == BillboardSessionState.Closed) return;
        State = BillboardSessionState.Closed;
        Detach();
        if (_window is { } window) window.Closed -= OnNativeClosed;
        if (_opened)
        {
            _opened = false;
            try { onClosed(); }
            catch (Exception ex) { log($"billboard OnClosed failed: {ex}"); }
        }
    }
}
