namespace Pagurian;

// UIA never runs on the taskbar-attached UI thread. The observer publishes
// only immutable physical geometry; the input timer reads this cache.
internal sealed class TaskbarSystemAreaObserver : IDisposable
{
    internal readonly record struct Context(nint Taskbar, uint ProcessId,
        TaskbarTrayPlacement.Surface Geometry);

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<Context, TaskbarClockAutomation.Result> _probe;
    private readonly Action<string> _log;
    private Context? _context;
    private int? _left;
    private long _generation;
    private bool _disposed;
    private string? _diagnostic;
    private DateTime _nextDiagnostic;
    private int _suppressed;

    internal TaskbarSystemAreaObserver(Func<Context, TaskbarClockAutomation.Result>? probe = null,
        Action<string>? log = null)
    {
        _probe = probe ?? Probe;
        _log = log ?? (message => PagurianLog.Tray("system-area", message));
        var thread = new Thread(Run) { IsBackground = true, Name = "Pagurian taskbar system area" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal TaskbarTrayPlacement.Surface Observe(nint taskbar, uint processId,
        TaskbarTrayPlacement.Surface geometry)
    {
        var context = new Context(taskbar, processId, geometry with { SystemAreaLeftPx = null });
        lock (_gate)
        {
            if (_disposed) return context.Geometry;
            if (_context != context)
            {
                _context = context;
                _generation++;
                _left = null;
                _wake.Set();
            }
            return context.Geometry with { SystemAreaLeftPx = _left };
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed || _context == null) return;
            _context = null;
            _generation++;
            _left = null;
            _wake.Set();
        }
    }

    private static TaskbarClockAutomation.Result Probe(Context context)
    {
        // A new worker need not inherit the UI thread's DPI override. Match
        // native GetWindowRect to UIA's always-physical screen coordinates.
        var previousDpi = TaskbarInterop.SetThreadDpiAwarenessContext((nint)(-4));
        if (previousDpi == 0) return new(null, "physical-coordinates-unavailable");
        try
        {
            if (!TaskbarInterop.IsWindow(context.Taskbar) ||
                TaskbarInterop.WindowProcessId(context.Taskbar) != context.ProcessId || context.ProcessId == 0)
                return new(null, "taskbar-unavailable");
            if (TaskbarInterop.TryGetTaskbarSystemAreaLeft(context.Taskbar, context.Geometry.ContentRect, out var left))
                return new(left, "native");
            if (!TaskbarInterop.IsSecondaryTaskbar(context.Taskbar))
                return new(null, "native-system-area-unavailable");
            return TaskbarClockAutomation.Read(context.Taskbar, context.ProcessId, context.Geometry.ContentRect);
        }
        finally
        {
            if (TaskbarInterop.SetThreadDpiAwarenessContext(previousDpi) == 0)
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    private void Run()
    {
        int delay = Timeout.Infinite;
        int failures = 0;
        try
        {
            while (true)
            {
                _wake.WaitOne(delay);
                Context? context;
                long generation;
                lock (_gate)
                {
                    if (_disposed) return;
                    context = _context;
                    generation = _generation;
                }
                if (context is not { } current) { delay = Timeout.Infinite; continue; }
                TaskbarClockAutomation.Result result;
                try { result = _probe(current); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or
                    System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    result = new(null, $"probe-error-0x{ex.HResult:X8}");
                }
                lock (_gate)
                {
                    if (_disposed) return;
                    if (generation != _generation) { delay = 0; failures = 0; continue; }
                    if (result.LeftPx is { } left &&
                        left > current.Geometry.ContentRect.Left && left < current.Geometry.ContentRect.Right)
                    {
                        _left = left;
                        failures = 0;
                    }
                    else
                    {
                        result = new(null, result.LeftPx.HasValue ? "invalid-boundary" : result.Reason);
                        failures = Math.Min(failures + 1, 4);
                    }
                    var diagnostic = $"taskbar={current.Taskbar} owner={current.ProcessId} generation={generation} " +
                        $"source={result.Reason} left={_left?.ToString() ?? "unknown"} retained={!result.LeftPx.HasValue && _left.HasValue}";
                    if (diagnostic != _diagnostic)
                    {
                        if (DateTime.UtcNow >= _nextDiagnostic)
                        {
                            _log($"{diagnostic} suppressed={_suppressed}");
                            _suppressed = 0;
                            _diagnostic = diagnostic;
                            _nextDiagnostic = DateTime.UtcNow.AddSeconds(1);
                        }
                        else _suppressed++;
                    }
                    delay = result.LeftPx.HasValue ? 500 : Math.Min(500 * (1 << failures), 4000);
                }
            }
        }
        finally
        {
            lock (_gate) _wake.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _context = null;
            _left = null;
            _wake.Set();
        }
    }
}
