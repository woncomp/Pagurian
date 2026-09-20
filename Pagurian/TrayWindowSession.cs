using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Pagurian;

internal enum TrayWindowSessionState { Preparing, Visible, Closing, Closed }

// One persistent HWND. Only startup, empty-to-nonempty and reparenting need a
// presentation gate; ordinary cell edits never hide or replace the window.
internal sealed class TrayWindowSession
{
    private static int _nextId;
    private readonly int _id = ++_nextId;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Border _root = new();
    private readonly Border _target = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Func<TaskbarTrayPlacement.Surface?> _getSurface;
    private readonly Func<nint> _getParent;
    private readonly Action<string> _log;
    private readonly Action<bool>? _themeSink;
    private readonly Func<TaskbarTrayLayout, Component> _content;
    private readonly WindowKey? _windowKey;
    private readonly TrayBackgroundSampler _background;
    private readonly NaturalTrayPanel _panel;
    private readonly LinearGradientBrush _brush = new();
    private readonly DispatcherQueueTimer _fallbackTimer;
    private TaskbarTrayPlacement.Surface? _surface;
    private ReactorWindow? _window;
    private nint _parent;
    private bool _started, _queued, _committing, _rendered, _colorReady, _waitingFrame, _shown;
    private bool _frameInFlight, _invalidatedDuringCommit, _retryLayout;
    private long _revision, _frameTicket, _presentationVersion, _paintedVersion = -1;
    private DateTime _retryInjection;

    internal TrayWindowSessionState State { get; private set; } = TrayWindowSessionState.Preparing;
    internal ReactorWindow? Window => _window;
    internal nint Hwnd { get; private set; }
    internal TrayLayoutSnapshot? Snapshot { get; private set; }
    internal TaskbarTrayLayout Layout { get; } = new();
    internal int MeasureCount { get; private set; }
    internal int CommitCount { get; private set; }
    internal bool Injected => _parent != 0;
    internal event Action? Committed;
    internal event Action? Revealed;

    internal TrayWindowSession(Func<TaskbarTrayPlacement.Surface?>? getSurface = null,
        Func<nint>? getParent = null, Func<TaskbarInterop.RECT, Task<byte[]?>>? capture = null,
        Action<string>? log = null, Action<bool>? themeSink = null,
        Func<TaskbarTrayLayout, Component>? content = null, WindowKey? windowKey = null)
    {
        _getSurface = getSurface ?? (() => TaskbarTrayPlacement.TryGetSurface(out var s) ? s : null);
        _getParent = getParent ?? TaskbarInterop.FindTaskbar;
        _log = log ?? (message => PagurianLog.Tray(_id.ToString(), message));
        _themeSink = themeSink;
        _content = content ?? (layout => new TaskbarTrayWindow(layout));
        _windowKey = windowKey;
        _panel = new NaturalTrayPanel(_target, QueueCommit, () => { if (_queued) Commit(insideArrange: true); });
        _root.Child = _panel;
        for (int i = 0; i < TaskbarTrayWindow.GradientStopCount; i++)
            _brush.GradientStops.Add(new GradientStop { Offset = (double)i / (TaskbarTrayWindow.GradientStopCount - 1) });
        _root.Background = _brush;
        _background = new(_dispatcher, capture);
        _background.Changed += OnBackgroundChanged;
        _fallbackTimer = _dispatcher.CreateTimer();
        _fallbackTimer.Interval = TimeSpan.FromMilliseconds(500);
        _fallbackTimer.IsRepeating = false;
        _fallbackTimer.Tick += OnFallback;
        Layout.Diagnostic += Log;
    }
    private void Log(string message) => _log($"tray-session={_id} {message}");
    private bool Active => State is TrayWindowSessionState.Preparing or TrayWindowSessionState.Visible;
    private double WindowScale => _window is { DipScale: > 0 } w ? w.DipScale : 1;

    internal void Start()
    {
        if (_started || !Active) return;
        _started = true;
        try
        {
            ApplyBackground();
            TrayManager.Changed += OnCellsChanged;
            _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
            ReactorApp.OpenWindow(TaskbarTrayWindow.CreateSpec(_windowKey), () => _content(Layout), host =>
            {
                _window = host.OwningWindow!;
                Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window.NativeWindow);
                // DWM cloaking of child HWNDs requires WS_EX_LAYERED. Keep the
                // style for the whole session so injection does not discard its
                // redirection surface or make DWMWA_CLOAK fail with E_HANDLE.
                var extended = GetWindowLongPtr(Hwnd, -20);
                SetWindowLongPtr(Hwnd, -20, (nint)((long)extended | 0x00080000));
                if (!SetLayeredWindowAttributes(Hwnd, 0, 255, 2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                SetCloak(true);
                SetDwmAttribute(3, true);
                _window.NativeWindow.Content = _root;
                host.ContentTarget = _target;
                host.OnRenderComplete = (_, _, _) =>
                {
                    _rendered = true;
                    // Reconciliation already batches component updates. Fit
                    // the viewport before returning to XAML's next paint, not
                    // in a later low-priority dispatcher turn.
                    Commit();
                };
                _window.DpiChanged += OnDpiChanged;
                _window.Closed += OnClosed;
                Log($"created hwnd={Hwnd}");
                CheckEnvironment();
            });
            _fallbackTimer.Start();
            QueueCommit();
        }
        catch (Exception ex) { Log($"start failed: {ex}"); Close(); }
    }

    // Called by the input timer, but queues layout ONLY when the environment changes.
    internal bool CheckEnvironment()
    {
        if (!Active || Hwnd == 0 || !TaskbarInterop.IsWindow(Hwnd)) return false;
        nint parent = _getParent();
        if (_parent != 0 && (parent != _parent || TaskbarInterop.GetAncestor(Hwnd, TaskbarInterop.GA_PARENT) != _parent))
            return false;
        if (_retryLayout) QueueCommit();
        var surface = _getSurface();
        if (surface is not { } next) return true; // retain last valid position during transient failures
        if (_surface != next)
        {
            _surface = next;
            _background.SetSurface(next);
            QueueCommit();
        }
        if (_parent == 0 && parent != 0 && DateTime.UtcNow >= _retryInjection)
        {
            _retryInjection = DateTime.UtcNow.AddSeconds(1.25);
            BeginPreparation();
            // Establish monitor DPI before converting the top-level window to a child.
            var staging = next.Place(1);
            TaskbarInterop.SetWindowPos(Hwnd, TaskbarInterop.HWND_TOP,
                staging.Left, staging.Top, 1, staging.Height, TaskbarInterop.SWP_NOACTIVATE);
            var style = TaskbarInterop.GetWindowStyle(Hwnd);
            TaskbarInterop.SetWindowStyle(Hwnd, (nint)(((int)style & ~TaskbarInterop.WS_POPUP) | TaskbarInterop.WS_CHILD));
            TaskbarInterop.SetParent(Hwnd, parent);
            // A successful SetParent can return NULL when the old parent was NULL.
            // Inspect the actual parent instead of treating that return as failure.
            if (TaskbarInterop.GetAncestor(Hwnd, TaskbarInterop.GA_PARENT) == parent) _parent = parent;
            else TaskbarInterop.SetWindowStyle(Hwnd, style);
            Snapshot = null;
            Log($"injection parent={_parent} hwnd={Hwnd}");
            QueueCommit();
        }
        return true;
    }
    private void OnCellsChanged() => QueueCommit();
    private void OnDpiChanged(object? sender, uint dpi) => QueueCommit();
    private void OnSystemColorsChanged(UISettings sender, object args) => _dispatcher.TryEnqueue(() =>
    {
        if (!Active) return;
        ApplyBackground();
        QueueCommit();
    });
    private void OnBackgroundChanged()
    {
        if (!Active) return;
        _colorReady = true;
        ApplyBackground();
        if (State == TrayWindowSessionState.Preparing) QueueCommit();
    }
    private void OnFallback(DispatcherQueueTimer sender, object args)
    {
        if (!Active) return;
        _colorReady = true;
        Log("initial color deadline reached; using sample or system theme fallback");
        ApplyBackground();
        QueueCommit();
    }

    private void QueueCommit()
    {
        if (!Active) return;
        if (_committing) { _invalidatedDuringCommit = true; return; }
        if (_queued) return;
        _queued = true;
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => { if (_queued) Commit(); })) _queued = false;
    }
    private void Commit(bool insideArrange = false)
    {
        _queued = false;
        if (!Active || _committing || !_rendered || _surface is not { } surface || _window == null) return;
        _committing = true;
        try
        {
            MeasureCount++;
            _panel.ContentScale = surface.Scale / WindowScale;
            if (!insideArrange)
                _panel.Measure(new Size(double.PositiveInfinity, TaskbarTrayWindow.ContentHeightDip * _panel.ContentScale));
            var next = Layout.Measure(_revision + 1, surface, WindowScale);
            if (next == null) return;
            if (next.Cells.IsEmpty)
            {
                BeginPreparation();
                _window.Hide();
                _shown = false;
            }
            if (!TaskbarTrayLayout.Equivalent(Snapshot, next))
            {
                var rect = next.BoundsPx;
                if (!insideArrange)
                    _panel.Arrange(new Rect(0, 0, rect.Width / WindowScale, rect.Height / WindowScale));
                bool positioned = Position(rect);
                if (!positioned)
                {
                    _retryLayout = true;
                    Log("geometry commit failed; retaining prior snapshot and retrying on environment check");
                    return;
                }
                _retryLayout = false;
                Snapshot = next;
                _revision = next.Version;
                _presentationVersion++;
                CommitCount++;
                ApplyBackground();
                Log($"commit={_revision} hwnd={Hwnd} bounds={rect.Left},{rect.Top},{rect.Width},{rect.Height} cells={next.Cells.Length} measures={MeasureCount}");
                Committed?.Invoke();
            }
            if (State == TrayWindowSessionState.Preparing && _colorReady && !next.Cells.IsEmpty)
                PrepareFrame();
        }
        catch (Exception ex) { Log($"layout failed: {ex}"); }
        finally
        {
            _committing = false;
            if (_invalidatedDuringCommit)
            {
                _invalidatedDuringCommit = false;
                QueueCommit();
            }
        }
    }

    private void ApplyBackground()
    {
        bool highContrast = _accessibility.HighContrast;
        Color systemBackground = _uiSettings.GetColorValue(UIColorType.Background);
        bool dark = Luminance(systemBackground) <= 140;
        var fallback = highContrast ? systemBackground : Color.FromArgb(255, dark ? (byte)32 : (byte)239,
            dark ? (byte)32 : (byte)239, dark ? (byte)32 : (byte)239);
        var colors = !highContrast && Snapshot is { } snapshot ? _background.Colors(snapshot.BoundsPx) : null;
        _brush.StartPoint = _surface is { IsHorizontal: false } ? new Point(0.5, 0) : new Point(0, 0.5);
        _brush.EndPoint = _surface is { IsHorizontal: false } ? new Point(0.5, 1) : new Point(1, 0.5);
        bool changed = false;
        for (int i = 0; i < _brush.GradientStops.Count; i++)
        {
            var color = colors?[i] ?? fallback;
            var previous = _brush.GradientStops[i].Color;
            if (previous.A != color.A || Math.Abs(previous.R - color.R) > 2 ||
                Math.Abs(previous.G - color.G) > 2 || Math.Abs(previous.B - color.B) > 2)
            {
                _brush.GradientStops[i].Color = color;
                changed = true;
            }
        }
        if (changed) _presentationVersion++;
        dark = _brush.GradientStops.Average(stop => Luminance(stop.Color)) <= 140;
        _root.RequestedTheme = highContrast ? ElementTheme.Default : dark ? ElementTheme.Dark : ElementTheme.Light;
        // The owning surface's theme follows the sampled taskbar luminance;
        // trays bound to this surface (and their shells) share that instance.
        _themeSink?.Invoke(dark);
    }
    private static double Luminance(Color c) => .299 * c.R + .587 * c.G + .114 * c.B;

    private void BeginPreparation()
    {
        StopFrameGate();
        _frameTicket++;
        _paintedVersion = -1;
        _frameInFlight = false;
        SetCloak(true);
        State = TrayWindowSessionState.Preparing;
    }
    private void PrepareFrame()
    {
        if (_waitingFrame || _frameInFlight || Snapshot == null) return;
        _waitingFrame = true;
        CompositionTarget.Rendering += OnRendering;
        CompositionTarget.Rendered += OnRendered;
        if (!_shown)
        {
            // AppWindow.Show also initializes the WinUI content pipeline;
            // raw ShowWindow alone can leave XAML permanently unloaded.
            _window!.Show();
            _shown = true;
            if (Snapshot is { } snapshot) Position(snapshot.BoundsPx);
        }
        _root.InvalidateArrange();
    }
    private void OnRendering(object? sender, object args) { }
    private void OnRendered(object? sender, object args)
    {
        if (!_waitingFrame || !_root.IsLoaded || Snapshot == null) return;
        _paintedVersion = _presentationVersion;
        if (_frameInFlight) return;
        FlushPaintedFrame();
    }
    private void FlushPaintedFrame()
    {
        _frameInFlight = true;
        long ticket = ++_frameTicket;
        _ = WaitForDesktopAsync(ticket, _paintedVersion);
    }
    private async Task WaitForDesktopAsync(long ticket, long paintedVersion)
    {
        int result;
        try { result = await Task.Run(DwmFlush).ConfigureAwait(false); }
        catch { result = -1; }
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (State != TrayWindowSessionState.Preparing || ticket != _frameTicket) return;
            try
            {
                if (result < 0) { Log("desktop presentation failed"); Close(); return; }
                // Drain no-op layout requests without asking static content for
                // a second dirty frame. Keep observing renders during DwmFlush
                // so a newer content/color frame can be flushed directly.
                if (_queued) Commit();
                _frameInFlight = false;
                if (Snapshot == null || Snapshot.Cells.IsEmpty) return;
                if (paintedVersion != _presentationVersion)
                {
                    if (_paintedVersion == _presentationVersion) FlushPaintedFrame();
                    return;
                }
                StopFrameGate();
                SetCloak(false);
                State = TrayWindowSessionState.Visible;
                _fallbackTimer.Stop();
                Log($"visible hwnd={Hwnd} commit={_revision}");
                Revealed?.Invoke();
            }
            catch (Exception ex) { Log($"reveal failed: {ex}"); Close(); }
        });
    }
    private void StopFrameGate()
    {
        if (!_waitingFrame) return;
        _waitingFrame = false;
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendered -= OnRendered;
    }
    internal void Close()
    {
        if (!Active) return;
        State = TrayWindowSessionState.Closing;
        Cleanup();
        try
        {
            if (Hwnd != 0 && TaskbarInterop.IsWindow(Hwnd))
            {
                _window?.Hide();
                _window?.Close();
            }
            else
            {
                // Explorer already destroyed this child. Native Close would
                // re-enter destruction and can AV instead of throwing.
                if (_window != null) ReactorDestroyedWindow.CompleteClose(_window);
            }
        }
        catch (Exception ex) { Log($"close failed: {ex.Message}"); }
        State = TrayWindowSessionState.Closed;
        Log($"closed hwnd={Hwnd}");
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        Cleanup();
        State = TrayWindowSessionState.Closed;
    }
    private void Cleanup()
    {
        StopFrameGate();
        _frameTicket++;
        _background.Dispose();
        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= OnFallback;
        TrayManager.Changed -= OnCellsChanged;
        _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
        if (_window != null)
        {
            _window.DpiChanged -= OnDpiChanged;
            _window.Closed -= OnClosed;
        }
    }
    private bool Position(TaskbarInterop.RECT rect)
    {
        var point = new TaskbarInterop.POINT { X = rect.Left, Y = rect.Top };
        if (_parent != 0 && !ScreenToClient(_parent, ref point)) return false;
        return TaskbarInterop.SetWindowPos(Hwnd, TaskbarInterop.HWND_TOP,
            point.X, point.Y, rect.Width, rect.Height, TaskbarInterop.SWP_NOACTIVATE);
    }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref TaskbarInterop.POINT point);
    private void SetCloak(bool enabled) => SetDwmAttribute(13, enabled);
    private void SetDwmAttribute(int attribute, bool enabled)
    {
        int value = enabled ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Hwnd, attribute, ref value, sizeof(int)));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // The HWND's provisional width never constrains the content's natural
    // measurement. XAML layout invalidations also detect child-only updates.
    private sealed class NaturalTrayPanel : Panel
    {
        private readonly Border _content;
        private readonly Action _changed, _arranged;
        private readonly ScaleTransform _transform = new();
        private double _scale = 1;
        private Size _last;
        internal double ContentScale
        {
            get => _scale;
            set { if (_scale == value) return; _scale = value; InvalidateMeasure(); }
        }
        internal NaturalTrayPanel(Border content, Action changed, Action arranged)
        {
            _content = content;
            _changed = changed;
            _arranged = arranged;
            content.RenderTransform = _transform;
            Children.Add(content);
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            _content.Measure(new Size(double.PositiveInfinity, TaskbarTrayWindow.ContentHeightDip));
            var desired = new Size(_content.DesiredSize.Width * ContentScale, TaskbarTrayWindow.ContentHeightDip * ContentScale);
            if (_last != desired) { _last = desired; _changed(); }
            return desired;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            _transform.ScaleX = _transform.ScaleY = ContentScale;
            _content.Arrange(new Rect(0, 0, _content.DesiredSize.Width, TaskbarTrayWindow.ContentHeightDip));
            // A child-only render can reach XAML layout before the queued low
            // priority callback. Commit the HWND before this arrange is painted,
            // otherwise the newly wider content is clipped for one frame.
            _arranged();
            return finalSize;
        }
    }
}
