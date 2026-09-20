using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Pagurian;
using Pagurian.Sdk;

sealed class RightTrayHarness
{
    private ReactorWindow? _parent;
    private TrayWindowSession _session = null!;
    private TaskbarSystemAreaObserver _observer = null!;
    private DispatcherQueueTimer _timer = null!;
    private TaskbarTrayPlacement.Surface _raw, _observed;
    private nint _parentHwnd, _original;
    private ProbeModel _model = null!;
    private int _phase, _ticks, _commits, _measures, _mode, _probeFailures, _clockExtra;
    private bool _real, _geometryMissing;
    private DateTime _deadline;
    private TaskbarInterop.RECT _stableBounds;

    internal void Run()
    {
        try
        {
            SystemAreaTests.Run();
            var args = Environment.GetCommandLineArgs();
            int secondary = Array.IndexOf(args, "--secondary-taskbar");
            _real = secondary >= 0;
            if (_real)
            {
                Require(secondary + 1 < args.Length && long.TryParse(args[secondary + 1], out _), "secondary HWND required");
                _parentHwnd = (nint)long.Parse(args[secondary + 1]);
                Require(_parentHwnd != TaskbarInterop.FindTaskbar() &&
                    TaskbarInterop.FindAllTaskbars().Any(t => t.Hwnd == _parentHwnd), "specified secondary taskbar unavailable");
                Require(TaskbarTrayPlacement.TryGetSurface(_parentHwnd, TrayEdge.Right, out _raw), "taskbar geometry unavailable");
            }
            else
            {
                _parent = ReactorApp.OpenWindow(new WindowSpec
                {
                    Title = "Right tray isolated parent", Width = 800, Height = 48,
                    ActivateOnOpen = false, Style = WindowStyle.None, NoActivate = true,
                    ShowInTaskbar = false, ShowInSwitcher = false,
                    StartPosition = WindowStartPosition.Manual, ManualPosition = (180, 180),
                }, () => new Background());
                _parent.Show();
                _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_parent.NativeWindow);
                Require(TaskbarTrayPlacement.TryGetSurface(_parentHwnd, TrayEdge.Right, out _raw), "isolated parent geometry unavailable");
            }
            var region = _raw.ContentRect;
            region.Left = Math.Max(region.Left, region.Right - 360);
            Console.WriteLine($"Capture region: {region.Left},{region.Top},{region.Width},{region.Height} hwnd={_parentHwnd}");
            ModuleLoader.Catalog[typeof(ProbeShell).FullName!] = new ShellAttribute { ShellType = typeof(ProbeShell) };
            TrayManager.ApplyConfig([new(TrayId.PrimaryLeft, [new(typeof(ProbeShell).FullName!, "1001", null)])]);
            _model = ((ProbeShell)TrayManager.Shells[0]).Model;
            _observer = _real ? new(log: Console.WriteLine) : new(Probe, Console.WriteLine);
            _session = new(ReadSurface, () => _parentHwnd,
                capture: _real ? null : r => Task.FromResult<byte[]?>(Enumerable.Repeat((byte)24, r.Width * r.Height * 4).ToArray()),
                log: Console.WriteLine);
            _session.Start();
            _original = _session.Hwnd;
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(50);
            _timer.Tick += (_, _) => Tick();
            _deadline = DateTime.UtcNow.AddSeconds(8);
            _phase = _real ? 1 : 0;
            _timer.Start();
        }
        catch (Exception ex) { Finish(ex); }
    }

    private TaskbarClockAutomation.Result Probe(TaskbarSystemAreaObserver.Context context)
    {
        if (Volatile.Read(ref _mode) != 1)
        {
            Interlocked.Increment(ref _probeFailures);
            return new(null, "fixture-unavailable");
        }
        var content = context.Geometry.ContentRect;
        var clock = content;
        clock.Left = content.Right - 83 - Volatile.Read(ref _clockExtra);
        clock.Right = content.Right - 12;
        TaskbarInterop.TryGetWindowRect(_original, out var own);
        var left = TaskbarSystemArea.SelectNative(context.ProcessId, content,
        [
            new(context.ProcessId, true, "TrayClockWClass", clock),
            new(context.ProcessId, true, "WinUIDesktopWin32WindowClass", own),
            new(context.ProcessId, true, "Windows.UI.Composition.DesktopWindowContentBridge", content),
        ]);
        return new(left, "fixture-native-selector");
    }

    private TaskbarTrayPlacement.Surface? ReadSurface()
    {
        if (_geometryMissing) { _observer.Invalidate(); return null; }
        if (_real && !TaskbarTrayPlacement.TryGetSurface(_parentHwnd, TrayEdge.Right, out _raw)) return null;
        _observed = _observer.Observe(_parentHwnd, TaskbarInterop.WindowProcessId(_parentHwnd), _raw);
        return _observed;
    }

    private void Tick()
    {
        try
        {
            Require(_session.CheckEnvironment(), "session lost parent");
            _ticks++;
            if (_phase != 3) Require(DateTime.UtcNow < _deadline, $"right tray phase {_phase} timed out");
            switch (_phase)
            {
                case 0:
                    Require(_session.State == TrayWindowSessionState.Preparing && _session.Snapshot == null,
                        "unknown boundary exposed initial tray");
                    if (_ticks < 20) break;
                    Volatile.Write(ref _mode, 1);
                    Next(1);
                    break;
                case 1:
                    if (_session.State != TrayWindowSessionState.Visible) break;
                    CheckVisible();
                    Next(2);
                    break;
                case 2:
                    CheckVisible();
                    if (_ticks < 10) break;
                    _commits = _session.CommitCount; _measures = _session.MeasureCount;
                    _stableBounds = _session.Snapshot!.BoundsPx;
                    Next(3);
                    break;
                case 3:
                    CheckVisible();
                    Require(_session.CommitCount == _commits && _session.MeasureCount == _measures,
                        "stable observer/polling caused layout work");
                    Require(_session.Snapshot!.BoundsPx.Equals(_stableBounds), "stable tray oscillated");
                    if (_ticks < 200) break;
                    if (_real) { Finish(); return; }
                    Volatile.Write(ref _mode, 2);
                    _probeFailures = 0;
                    Next(4);
                    break;
                case 4:
                    CheckVisible();
                    Require(_session.CommitCount == _commits, "transient failure moved known tray");
                    if (Volatile.Read(ref _probeFailures) == 0) break;
                    _geometryMissing = true;
                    _session.CheckEnvironment();
                    Require(_session.Snapshot == null && _session.State == TrayWindowSessionState.Preparing,
                        "invalid geometry retained visible hit targets");
                    _model.Resize!(90);
                    Next(5);
                    break;
                case 5:
                    Require(_session.State == TrayWindowSessionState.Preparing && _session.Snapshot == null,
                        "content render revived invalidated location");
                    if (_ticks < 20) break;
                    _geometryMissing = false;
                    Volatile.Write(ref _mode, 1);
                    Next(6);
                    break;
                case 6:
                    if (_session.State != TrayWindowSessionState.Visible) break;
                    CheckVisible();
                    Require(Math.Abs(_session.Snapshot!.WidthDip - 96) < .01, "hidden content changes were lost");
                    _stableBounds = _session.Snapshot.BoundsPx;
                    _commits = _session.CommitCount;
                    Volatile.Write(ref _clockExtra, 20);
                    Next(7);
                    break;
                case 7:
                    if (_session.Snapshot!.BoundsPx.Left != _stableBounds.Left - 20) break;
                    CheckVisible();
                    Require(_session.CommitCount == _commits + 1, "one clock boundary update caused multiple commits");
                    Finish();
                    break;
            }
        }
        catch (Exception ex) { Finish(ex); }
    }

    private void CheckVisible()
    {
        Require(_session.State == TrayWindowSessionState.Visible && _session.Snapshot != null, "expected visible right tray");
        Require(_session.Hwnd == _original && _model.Mounts == 1 && _model.Unmounts == 0, "right tray recovery remounted content");
        var bounds = _session.Snapshot!.BoundsPx;
        Require(TaskbarInterop.TryGetWindowRect(_original, out var actual) && actual.Equals(bounds), "snapshot/native geometry disagree");
        Require(_observed.SystemAreaLeftPx is { } left && bounds.Right <= left &&
            Math.Abs(bounds.Right - (left - 8 * _observed.Scale)) < 1.01, "tray overlaps clock or has incorrect gap");
    }
    private void Next(int phase) { _phase = phase; _ticks = 0; _deadline = DateTime.UtcNow.AddSeconds(8); }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Finish(Exception? error = null)
    {
        _timer?.Stop();
        _observer?.Dispose();
        _session?.Close();
        _parent?.Close();
        TrayManager.ShutdownAll();
        if (error != null) Console.Error.WriteLine(error);
        else Console.WriteLine($"Tray lifecycle passed: right tray {(_real ? "secondary-taskbar" : "isolated")} boundaries and 200 stable environment polls.");
        Environment.Exit(error == null ? 0 : 1);
    }
}
