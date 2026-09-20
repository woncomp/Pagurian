using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Pagurian;

internal sealed class ShellConfigurationPageMotion : IDisposable
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

    private FrameworkElement? _element;
    private Microsoft.UI.Composition.Visual? _visual;
    private RoutedEventHandler? _loaded;
    private Action? _startWhenLoaded;
    private Action? _completed;
    private float _extent = 1;
    private float _position;
    private float _start;
    private float _target;
    private long _started;
    private TimeSpan _segmentDuration;
    private int _generation;
    private bool _rendering;
    private bool _disposed;
    private bool _exiting;

    internal float CurrentXForDiagnostics => _position;

    internal void Attach(FrameworkElement element, double extent, bool startOffscreen)
    {
        if (_disposed)
            return;

        _element = element;
        _visual = ElementCompositionPreview.GetElementVisual(element);
        _extent = Extent(extent);
        _position = startOffscreen ? _extent : 0;
        _start = _position;
        _target = _position;
        _visual.Opacity = 1;
        _visual.Offset = new Vector3(_position, 0, 0);
    }

    internal void Enter(Action completed)
    {
        _exiting = false;
        StartWhenLoaded(() => AnimateTo(0, completed));
    }

    internal void Exit(Action completed)
    {
        _exiting = true;
        if (_element != null)
            _element.IsHitTestVisible = false;
        CancelPendingStart();
        if (_element?.IsLoaded != true)
        {
            CompleteAt(offscreen: true);
            completed();
            return;
        }
        AnimateTo(_extent, completed);
    }

    internal void CompleteAt(bool offscreen)
    {
        CancelAnimation();
        _position = offscreen ? _extent : 0;
        _start = _position;
        _target = _position;
        if (_visual != null)
            _visual.Offset = new Vector3(_position, 0, 0);
    }

    internal void Stop()
    {
        CancelPendingStart();
        CancelAnimation();
    }

    internal void UpdateExtent(double extent)
    {
        var next = Extent(extent);
        if (Math.Abs(next - _extent) < 0.5f)
            return;

        var previous = _extent;
        _extent = next;
        if (_rendering && _exiting)
            AnimateTo(_extent, _completed ?? (static () => { }));
        else if (!_rendering && _position >= previous - 0.5f)
        {
            _position = _extent;
            _start = _extent;
            _target = _extent;
            if (_visual != null)
                _visual.Offset = new Vector3(_position, 0, 0);
        }
    }

    private void AnimateTo(float target, Action completed)
    {
        if (_disposed || _visual == null)
            return;

        UpdatePosition();
        CancelAnimation();
        _start = _position;
        _target = target;
        _completed = completed;
        var distanceFraction = Math.Abs(_target - _start) / Math.Max(1, _extent);
        _segmentDuration = TimeSpan.FromMilliseconds(
            Math.Clamp(Duration.TotalMilliseconds * distanceFraction, 60, Duration.TotalMilliseconds));
        _started = Stopwatch.GetTimestamp();
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        if (_disposed || !_rendering || _visual == null)
            return;

        UpdatePosition();
        if (Math.Abs(_position - _target) > 0.01f)
            return;

        var generation = _generation;
        var completed = _completed;
        CancelAnimation();
        if (_disposed || generation + 1 != _generation)
            return;
        completed?.Invoke();
    }

    private void UpdatePosition()
    {
        if (!_rendering || _visual == null)
            return;

        var elapsed = Stopwatch.GetElapsedTime(_started);
        var progress = Math.Clamp(
            elapsed.TotalMilliseconds / Math.Max(1, _segmentDuration.TotalMilliseconds),
            0,
            1);
        var eased = progress * progress * (3 - 2 * progress);
        _position = _start + (_target - _start) * (float)eased;
        if (progress >= 1)
            _position = _target;
        _visual.Offset = new Vector3(_position, 0, 0);
    }

    private void StartWhenLoaded(Action action)
    {
        CancelPendingStart();
        if (_element?.IsLoaded == true)
        {
            action();
            return;
        }
        if (_element == null)
            return;

        _startWhenLoaded = action;
        _loaded = (_, _) =>
        {
            var pending = _startWhenLoaded;
            CancelPendingStart();
            pending?.Invoke();
        };
        _element.Loaded += _loaded;
    }

    private void CancelPendingStart()
    {
        if (_element != null && _loaded != null)
            _element.Loaded -= _loaded;
        _loaded = null;
        _startWhenLoaded = null;
    }

    private void CancelAnimation()
    {
        _generation++;
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        _completed = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelPendingStart();
        CancelAnimation();
        if (_visual != null)
        {
            _visual.Offset = Vector3.Zero;
            _visual.Opacity = 1;
        }
        if (_element != null)
            _element.IsHitTestVisible = true;
        _element = null;
        _visual = null;
    }

    private static float Extent(double value) =>
        (float)Math.Clamp(double.IsFinite(value) ? value : 0, 1, 100_000);
}
