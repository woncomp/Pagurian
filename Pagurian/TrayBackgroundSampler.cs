using Microsoft.UI.Dispatching;
using Windows.UI;

namespace Pagurian;

// Screen reads can stall DWM: all captures run off the UI thread. Each sample
// is spatially indexed and remains useful when only the tray width changes.
internal sealed class TrayBackgroundSampler : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<TaskbarInterop.RECT, Task<byte[]?>> _capture;
    private readonly DispatcherQueueTimer _timer;
    private TaskbarTrayPlacement.Surface? _surface;
    private Sample? _sample;
    private long _generation;
    private bool _busy, _disposed;
    internal int DiscardedSamples { get; private set; }
    internal event Action? Changed;
    internal bool HasSample => _sample != null;

    internal TrayBackgroundSampler(DispatcherQueue dispatcher,
        Func<TaskbarInterop.RECT, Task<byte[]?>>? capture = null)
    {
        _dispatcher = dispatcher;
        _capture = capture ?? (r => Task.Run(() =>
            TaskbarInterop.CaptureScreenRegionPixels(r.Left, r.Top, r.Width, r.Height)));
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += OnTick;
        _timer.Start();
    }
    internal void SetSurface(TaskbarTrayPlacement.Surface surface)
    {
        if (_surface == surface) return;
        _surface = surface;
        _sample = null;
        _generation++;
        Request();
    }
    private void OnTick(DispatcherQueueTimer sender, object args) => Request();
    private async void Request()
    {
        if (_busy || _disposed || _surface is not { } surface) return;
        _busy = true;
        long generation = _generation;
        Sample? sample = null;
        try
        {
            var bar = surface.ContentRect;
            var tray = surface.Place(1);
            if (surface.IsHorizontal)
            {
                var strip = new TaskbarInterop.RECT
                {
                    Left = bar.Left, Right = bar.Right, Top = tray.Bottom, Bottom = bar.Bottom,
                };
                if (strip.Height > 0 && await _capture(strip).ConfigureAwait(false) is { } pixels)
                    sample = new(strip, pixels, null, default, true);
            }
            else
            {
                var top = new TaskbarInterop.RECT
                {
                    Left = bar.Left + 4, Right = bar.Right - 4,
                    Top = Math.Max(bar.Top, tray.Top - 3), Bottom = tray.Top,
                };
                var bottom = new TaskbarInterop.RECT
                {
                    Left = top.Left, Right = top.Right, Top = tray.Bottom,
                    Bottom = Math.Min(bar.Bottom, tray.Bottom + 3),
                };
                var a = await _capture(top).ConfigureAwait(false);
                var b = await _capture(bottom).ConfigureAwait(false);
                if (a != null && b != null) sample = new(top, a, b, bottom, false);
            }
        }
        catch { /* Retain a valid sample or the theme fallback; retry next tick. */ }
        _dispatcher.TryEnqueue(() =>
        {
            _busy = false;
            if (_disposed) return;
            if (generation != _generation)
            {
                DiscardedSamples++;
                Request();
                return;
            }
            if (sample != null) { _sample = sample; Changed?.Invoke(); }
        });
    }
    internal Color[]? Colors(TaskbarInterop.RECT bounds) => _sample?.Colors(bounds);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Changed = null;
        _sample = null;
    }

    private sealed record Sample(TaskbarInterop.RECT Region, byte[] Pixels,
        byte[]? BottomPixels, TaskbarInterop.RECT BottomRegion, bool Horizontal)
    {
        internal Color[]? Colors(TaskbarInterop.RECT bounds)
        {
            var colors = new Color[TaskbarTrayWindow.GradientStopCount];
            Color? top = null, bottom = null;
            if (!Horizontal)
            {
                top = TaskbarInterop.TrimmedMeanColor(Pixels, Region.Width, Region.Height, 0, Region.Width);
                bottom = TaskbarInterop.TrimmedMeanColor(BottomPixels!, BottomRegion.Width, BottomRegion.Height, 0, BottomRegion.Width);
                if (top == null || bottom == null) return null;
            }
            for (int i = 0; i < colors.Length; i++)
            {
                double fraction = (double)i / (colors.Length - 1);
                if (Horizontal)
                {
                    int x = (int)Math.Round(bounds.Left + fraction * Math.Max(0, bounds.Width - 1) - Region.Left);
                    x = Math.Clamp(x, 0, Region.Width - 1);
                    var color = TaskbarInterop.TrimmedMeanColor(Pixels, Region.Width, Region.Height, x - 2, x + 3);
                    if (color == null) return null;
                    colors[i] = color.Value;
                }
                else
                {
                    var a = top!.Value; var b = bottom!.Value;
                    colors[i] = Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * fraction),
                        (byte)(a.G + (b.G - a.G) * fraction), (byte)(a.B + (b.B - a.B) * fraction));
                }
            }
            return colors;
        }
    }
}
