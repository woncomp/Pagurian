using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pagurian;
using Pagurian.Sdk;
using System.Runtime.InteropServices;
using Windows.Graphics;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    if (Environment.GetCommandLineArgs().Any(a => a is "--right" or "--secondary-taskbar"))
    {
        new RightTrayHarness().Run();
        return;
    }
    if (Environment.GetCommandLineArgs().Contains("--overflow"))
    {
        new OverflowHarness().Run();
        return;
    }
    if (Environment.GetCommandLineArgs().Contains("--geometry"))
    {
        GeometryProbe.Run();
        return;
    }
    var harness = new Harness();
    Application.Current.UnhandledException += (_, e) => harness.Fail(e.Exception.ToString());
    harness.Run();
});

sealed class ProbeModel
{
    internal double Width = 60;
    internal Action<double>? Resize;
    internal int Mounts, Unmounts;
}
public sealed class ProbeShell : Shell
{
    internal ProbeModel Model = new();
    internal ShellCellHandle? Handle;
    public override void Startup() => Handle = AddCell<ProbeCell>(Model);
    public override void Shutdown() { if (Handle != null) RemoveCell(Handle); }
    internal void Add() => Handle = AddCell<ProbeCell>(new ProbeModel());
    internal void Remove() { if (Handle != null) RemoveCell(Handle); }
}
public sealed class ProbeCell : ShellCell
{
    private static readonly SolidColorBrush Fill = new(Microsoft.UI.Colors.Teal);
    public override Element Render()
    {
        var model = ModelAs<ProbeModel>();
        var (width, setWidth) = UseState(model.Width);
        model.Resize = setWidth;
        UseEffect(() => { model.Mounts++; return () => model.Unmounts++; }, []);
        return Border(Body("TRAY").Foreground(Theme.TextBrush)).Width(width).Height(28)
            .Background(Fill);
    }
}
sealed class Background : Component
{
    private static readonly SolidColorBrush Fill = new(Microsoft.UI.Colors.Black);
    public override Element Render() => Border(null).Background(Fill);
}

public sealed class SizedProbeShell : Shell
{
    public override void Startup()
    {
        var width = InstanceId switch { "2001" => 50d, "2002" => 60d, "2003" => 70d, _ => 80d };
        AddCell<ProbeCell>(new ProbeModel { Width = width });
    }
}

sealed class OverflowHarness
{
    private DispatcherQueueTimer _timer = null!;
    private ReactorWindow _parent = null!;
    private TrayWindowSession _session = null!;
    private OverflowTraySession? _overflow;
    private TaskbarTrayPlacement.Surface _surface;
    private nint _parentHwnd;
    private int _phase, _ticks;
    private DateTime _deadline;
    private ThemeService _theme = new();
    private static TrayConfig.Entry Entry(string id) => new(typeof(SizedProbeShell).FullName!, id, null);

    internal void Run()
    {
        try
        {
            TrayPresentationTests.Run();
            _parent = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Overflow isolated parent", Width = 800, Height = 48,
                ActivateOnOpen = false, Style = WindowStyle.None, NoActivate = true,
                ShowInTaskbar = false, ShowInSwitcher = false,
                StartPosition = WindowStartPosition.Manual, ManualPosition = (180, 180),
            }, () => new Background());
            _parent.Show();
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_parent.NativeWindow);
            TaskbarInterop.TryGetWindowRect(_parentHwnd, out var r);
            var scale = r.Height / 48.0;
            var island = r with { Left = r.Left + (int)(220 * scale) };
            _surface = new(r, r, true, scale, TaskContentRect: island);
            Console.WriteLine($"Overflow region: {r.Left},{r.Top},{r.Width},{r.Height}");
            ModuleLoader.Catalog[typeof(SizedProbeShell).FullName!] = new ShellAttribute { ShellType = typeof(SizedProbeShell) };
            TrayManager.ApplyConfig([new(TrayId.PrimaryLeft,
                [Entry("2001"), Entry("2002"), Entry("2003"), Entry("2004")])]);
            _session = new(() => _surface, () => _parentHwnd,
                capture: _ => Task.FromResult<byte[]?>(Enumerable.Repeat((byte)24, r.Width * r.Height * 4).ToArray()),
                log: Console.WriteLine, themeSink: _theme.Apply);
            _session.Start();
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(50);
            _timer.Tick += (_, _) => Tick();
            _deadline = DateTime.UtcNow.AddSeconds(8);
            _timer.Start();
        }
        catch (Exception ex) { Finish(ex); }
    }

    private void Tick()
    {
        try
        {
            Require(_session.CheckEnvironment(), "overflow session lost parent");
            _ticks++;
            Require(DateTime.UtcNow < _deadline, $"overflow phase {_phase} timed out");
            switch (_phase)
            {
                case 0:
                    if (_session.State != TrayWindowSessionState.Visible) break;
                    var snapshot = _session.Snapshot!;
                    Require(snapshot.Plan.Overflow && snapshot.OverflowArrowBoundsPx != null,
                        "narrow capacity did not enter overflow");
                    Require(snapshot.Cells.Select(cell => cell.Key).Count() == 2,
                        "overflow main cells did not stop at the first non-fit");
                    Require(snapshot.Plan.OverflowCells.Select(cell => cell.Key).Count() == 2,
                        "overflow set changed");
                    var arrow = snapshot.OverflowArrowBoundsPx!.Value;
                    Require(TaskbarInterop.TryGetMonitorWorkArea(arrow, out var workArea), "overflow work area unavailable");
                    Require(arrow.Right <= workArea.Right && arrow.Left >= workArea.Left, "arrow left system area");
                    _overflow = new OverflowTraySession(
                        () => _session.Snapshot?.OverflowArrowBoundsPx is { } current
                            ? new OverflowAnchor(current, workArea, _surface.Scale, TrayEdge.Left)
                            : null,
                        () => _session.Snapshot?.Plan.OverflowCells ?? [],
                        _theme, new SurfaceKey("overflow-fixture", TrayEdge.Left), Console.WriteLine);
                    _overflow.Start();
                    _phase = 1;
                    break;
                case 1:
                    if (_overflow is not { State: OverflowTrayState.Visible } overflow) break;
                    var bounds = overflow.BoundsPx;
                    var expectedHeight = (int)Math.Round(TaskbarTrayWindow.WindowHeightDip * _surface.Scale);
                    Require(Math.Abs(bounds.Height - expectedHeight) <= 1, "overflow height left taskbar thickness");
                    Require(bounds.Y < _surface.ContentRect.Top, "overflow did not open above the taskbar");
                    Require(overflow.Snapshot!.Cells.Length == 2, "overflow cell count changed");
                    Require(overflow.Snapshot.Cells[0].Key != overflow.Snapshot.Cells[1].Key,
                        "overflow duplicated a cell key");
                    Require(overflow.Snapshot.Cells.All(cell =>
                        cell.BoundsPx.Left >= bounds.X && cell.BoundsPx.Right <= bounds.X + bounds.Width),
                        "overflow cell rect escaped the window");
                    Finish();
                    return;
            }
        }
        catch (Exception ex) { Finish(ex); }
    }

    private void Finish(Exception? error = null)
    {
        _timer?.Stop();
        _overflow?.Close();
        _session?.Close();
        _parent?.Close();
        TrayManager.ShutdownAll();
        if (error != null) Console.Error.WriteLine(error);
        else Console.WriteLine("Tray lifecycle passed: overflow presentation geometry and session.");
        Environment.Exit(error == null ? 0 : 1);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
sealed class Harness
{
    private readonly Queue<(int Delay, Action Action)> _steps = new();
    private DispatcherQueueTimer _timer = null!;
    private ReactorWindow _parent = null!;
    private TrayWindowSession _session = null!;
    private TaskbarTrayPlacement.Surface _surface;
    private nint _parentHwnd, _originalHwnd;
    private ProbeModel[] _models = [];
    private int _checks, _publications, _commits, _measures;
    private bool _realTaskbar;
    private TaskCompletionSource<byte[]?>? _delayed;
    private TrayBackgroundSampler? _sampler;
    private int _captures, _samples;
    private readonly ThemeService _theme = new();
    private static TrayConfig.Entry Entry(string id) => new(typeof(ProbeShell).FullName!, id, null);
    private static TrayConfig.TrayGroup Group(params TrayConfig.Entry[] entries) => new(TrayId.PrimaryLeft, entries);
    internal void Run()
    {
        try
        {
            _realTaskbar = Environment.GetCommandLineArgs().Contains("--taskbar");
            SystemAreaTests.Run();
            TrayPresentationTests.Run();
            _parent = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Tray lifecycle isolated parent", Width = 800, Height = 48,
                ActivateOnOpen = false, Style = WindowStyle.None, NoActivate = true,
                ShowInTaskbar = false, ShowInSwitcher = false, Level = WindowLevel.AlwaysOnTop,
                StartPosition = WindowStartPosition.Manual, ManualPosition = (180, 180),
            }, () => new Background());
            _parent.Show();
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_parent.NativeWindow);
            TaskbarInterop.TryGetWindowRect(_parentHwnd, out var r);
            _surface = new(r, r, true, r.Height / 48.0, TaskContentRect: RightIsland(r, 400));
            if (_realTaskbar && TaskbarTrayPlacement.TryGetSurface(out var real))
            {
                var region = real.ContentRect;
                region.Left += (int)(300 * real.Scale);
                region.Right = Math.Min(region.Right, region.Left + (int)(800 * real.Scale));
                // This fixture locks widths/click geometry, not live taskbar
                // occupancy; give the shifted strip an isolated content island.
                _surface = real with { ContentRect = region, TaskContentRect = RightIsland(region, 1) };
                _parentHwnd = TaskbarInterop.FindTaskbar();
                _parent.Hide();
            }
            Console.WriteLine($"Capture region: {_surface.ContentRect.Left},{_surface.ContentRect.Top},{_surface.ContentRect.Width},{_surface.ContentRect.Height} hwnd={_parentHwnd}");
            ModuleLoader.Catalog[typeof(ProbeShell).FullName!] = new ShellAttribute { ShellType = typeof(ProbeShell) };
            TrayManager.Changed += () => _publications++;
            TrayManager.ApplyConfig([Group(Entry("1001"), Entry("1002"), Entry("1003"))]);
            _models = TrayManager.Shells.Cast<ProbeShell>().Select(s => s.Model).ToArray();
            Require(_publications == 1, "initial cells were published more than once");
            _session = Create();
            _session.Start();
            _originalHwnd = _session.Hwnd;
            Require(_session.State == TrayWindowSessionState.Preparing, "window was exposed during startup");
            Step(2000, () =>
            {
                Visible(198);
                Require(_session.Injected, "child window injection failed");
                Require(_session.Layout.MountCount == 3, "initial cells not mounted exactly once");
                _publications = 0;
                TrayManager.ApplyConfig([Group(Entry("1001"), Entry("1003"))]);
                Require(_publications == 1, "shutdown published intermediate lists");
            });
            Step(180, () =>
            {
                Visible(132);
                Require(_models[0].Mounts == 1 && _models[2].Mounts == 1, "survivors remounted on middle deletion");
                Require(_models[1].Unmounts == 1, "deleted cell did not unmount");
                Require(_session.Hwnd == _originalHwnd, "ordinary deletion replaced HWND");
                TrayManager.ApplyConfig([Group(Entry("1003"))]);
            });
            Step(180, () => { Visible(66); _models[2].Resize!(117); });
            Step(180, () =>
            {
                Visible(123);
                _commits = _session.CommitCount; _measures = _session.MeasureCount;
            });
            Step(600, () =>
            {
                Require(_session.CommitCount == _commits, "idle tray kept committing geometry");
                Require(_session.MeasureCount == _measures, "color sampling caused idle measurement");
                _publications = 0;
                var shell = (ProbeShell)TrayManager.Shells[0];
                shell.Add(); shell.Remove(); shell.Add();
            });
            Step(200, () =>
            {
                Require(_publications == 1, "dynamic cell edits were not coalesced");
                Visible(189);
                TrayManager.ApplyConfig([]);
            });
            Step(180, () =>
            {
                Require(_session.State == TrayWindowSessionState.Preparing && !IsWindowVisible(_session.Hwnd), "empty tray remained visible");
                TrayManager.ApplyConfig([Group(Entry("1004"))]);
            });
            Step(350, () =>
            {
                Visible(66);
                Require(_session.Hwnd == _originalHwnd, "empty-to-nonempty replaced HWND");
                _surface = _surface with { ContentRect = Shift(_surface.ContentRect, 20) };
                _session.CheckEnvironment();
            });
            Step(200, () =>
            {
                Visible(66);
                Require(_session.Snapshot!.BoundsPx.Left == _surface.Place(66).Left, "taskbar movement did not re-anchor");
                _session.Close();
                _session = Create(capture: _ => Task.FromResult<byte[]?>(null));
                _session.Start();
            });
            Step(250, () => Require(_session.State == TrayWindowSessionState.Preparing, "failed capture skipped initial color deadline"));
            Step(450, () =>
            {
                Visible(66);
                _session.Close();
                _session = Create(capture: r => Task.FromResult<byte[]?>(Pixels(r, 239)));
                _session.Start();
            });
            Step(500, () =>
            {
                Visible(66);
                Require(!_theme.IsDark, "light sample did not select light text theme");
                _session.Close();
                _session = Create(capture: r => Task.FromResult<byte[]?>(Pixels(r, 24)));
                _session.Start();
            });
            Step(500, () =>
            {
                Visible(66);
                Require(_theme.IsDark, "dark sample did not select dark text theme");
                _session.Close();
                _session = Create(capture: _ => { _delayed = new(); return _delayed.Task; });
                _session.Start();
            });
            Step(80, () =>
            {
                _session.Close();
                _delayed?.SetResult(null);
            });
            Step(150, () =>
            {
                Require(_session.State == TrayWindowSessionState.Closed, "late callback revived closed session");
                _session = Create(parent: () => 0);
                _session.Start();
            });
            Step(700, () => { Visible(66); Require(!_session.Injected, "floating fallback unexpectedly injected"); _session.Close(); TestStaleSamples(); });
            Step(100, () =>
            {
                Require(_sampler!.DiscardedSamples == 1 && _samples == 1, "stale sample was applied or fresh sample was lost");
                var captures = _captures;
                _sampler.SetSurface(_surface with
                {
                    ContentRect = Shift(_surface.ContentRect, 40),
                    Edge = TrayEdge.Right, SystemAreaLeftPx = _surface.ContentRect.Right - 83,
                });
                Require(_sampler.HasSample && _captures == captures, "anchor-only change discarded spatial background");
                _sampler.Dispose();
                _session = Create(); _session.Start();
            });
            Step(700, () =>
            {
                Visible(66);
                if (!_realTaskbar)
                {
                    _parent.Close();
                    Require(!_session.CheckEnvironment(), "destroyed parent was not detected");
                }
                _session.Close();
                _session = Create(parent: () => 0);
                _session.Start();
            });
            Step(700, () =>
            {
                Visible(66);
                _session.Close();
                TrayManager.ShutdownAll();
                if (_realTaskbar) _parent.Close();
                Console.WriteLine($"Tray lifecycle passed: {_checks} assertions.");
                Environment.Exit(0);
            });
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Next();
            Next();
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }
    private TrayWindowSession Create(Func<nint>? parent = null, Func<TaskbarInterop.RECT, Task<byte[]?>>? capture = null)
    {
        var session = new TrayWindowSession(() => _surface, parent ?? (() => _parentHwnd), capture, Console.WriteLine, _theme.Apply);
        session.Revealed += () =>
        {
            Require(session.Snapshot is { Cells.IsEmpty: false }, "revealed an empty snapshot");
            Require(!IsCloaked(session.Hwnd), "revealed window remains cloaked");
        };
        return session;
    }
    private void TestStaleSamples()
    {
        var pending = new TaskCompletionSource<byte[]?>();
        TaskbarInterop.RECT oldRegion = default;
        _sampler = new(ReactorApp.UIDispatcher!, rect =>
        {
            _captures++;
            if (_captures == 1) { oldRegion = rect; return pending.Task; }
            return Task.FromResult<byte[]?>(Pixels(rect, 24));
        });
        _sampler.Changed += () => _samples++;
        _sampler.SetSurface(_surface);
        _sampler.SetSurface(_surface with { ContentRect = Shift(_surface.ContentRect, 40) });
        pending.SetResult(Pixels(oldRegion, 240));
    }
    private static byte[] Pixels(TaskbarInterop.RECT r, byte value) => Enumerable.Repeat(value, r.Width * r.Height * 4).ToArray();
    private static TaskbarInterop.RECT Shift(TaskbarInterop.RECT r, int x) => new() { Left = r.Left + x, Right = r.Right + x, Top = r.Top, Bottom = r.Bottom };
    private static TaskbarInterop.RECT RightIsland(TaskbarInterop.RECT r, int width) => new()
    { Left = r.Right - width, Right = r.Right, Top = r.Top, Bottom = r.Bottom };
    private void Visible(double width)
    {
        Require(_session.State == TrayWindowSessionState.Visible, $"expected Visible, got {_session.State}");
        var s = _session.Snapshot!;
        // Desired-size rounding can differ by one physical pixel on scaled
        // displays; compare in surface DIPs with that same tolerance.
        Require(Math.Abs(s.WidthDip - width) < 1 / s.Scale + .01,
            $"natural width {s.WidthDip}, expected {width}, cells=[{string.Join(",", s.Cells.Select(c => $"{c.WidthDip:F3}"))}]");
        TaskbarInterop.TryGetWindowRect(_session.Hwnd, out var actual);
        Require(actual.Equals(s.BoundsPx), $"native bounds disagree with snapshot: {actual.Left},{actual.Top},{actual.Width},{actual.Height}");
        Require(s.Cells[0].BoundsPx.Left == actual.Left && s.Cells[^1].BoundsPx.Right == actual.Right, "hit-test cells do not fill native width");
        Require(IsWindowVisible(_session.Hwnd), "visible session has hidden HWND");
    }
    private void Step(int delay, Action action) => _steps.Enqueue((delay, action));
    private Action? _action;
    private void Next()
    {
        try
        {
            _action?.Invoke();
            if (_steps.TryDequeue(out var step))
            {
                _action = step.Action;
                _timer.Interval = TimeSpan.FromMilliseconds(step.Delay);
                _timer.Start();
            }
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }
    private void Require(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    internal void Fail(string message)
    {
        Console.Error.WriteLine(message);
        try { _session?.Close(); _parent?.Close(); } catch { }
        Environment.Exit(1);
    }
    private static bool IsCloaked(nint hwnd) { DwmGetWindowAttribute(hwnd, 14, out int value, 4); return value != 0; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
}

static class GeometryProbe
{
    internal static void Run()
    {
        if (!TaskbarTrayPlacement.TryGetSurface(out var raw))
        {
            Console.Error.WriteLine("geometry probe: taskbar unavailable");
            Environment.Exit(1);
        }
        using var observer = new TaskbarSystemAreaObserver(log: Console.WriteLine);
        var observed = observer.Observe(TaskbarInterop.FindTaskbar(),
            TaskbarInterop.WindowProcessId(TaskbarInterop.FindTaskbar()), raw);
        if (!SpinWait.SpinUntil(() => observer.Observe(TaskbarInterop.FindTaskbar(),
            TaskbarInterop.WindowProcessId(TaskbarInterop.FindTaskbar()), raw).TaskContentRect != null, 5000))
        {
            var failed = TaskbarClockAutomation.Read(TaskbarInterop.FindTaskbar(),
                TaskbarInterop.WindowProcessId(TaskbarInterop.FindTaskbar()), raw.ContentRect);
            Console.Error.WriteLine(
                $"geometry probe: content island unavailable reason={failed.Reason} " +
                $"bounds={(failed.ContentBounds is { } bounds ? $"{bounds.Left}-{bounds.Right}" : "none")}");
            Environment.Exit(1);
        }
        observed = observer.Observe(TaskbarInterop.FindTaskbar(),
            TaskbarInterop.WindowProcessId(TaskbarInterop.FindTaskbar()), raw);
        Console.WriteLine(
            $"geometry probe: content={observed.TaskContentRect!.Value.Left}-{observed.TaskContentRect.Value.Right} " +
            $"systemArea={observed.SystemAreaLeftPx?.ToString() ?? "unknown"} " +
            $"leftCapacity={new TaskbarTrayPlacement.Surface(observed.ContentRect, observed.ParentRect, true, observed.Scale, TrayEdge.Left, TaskContentRect: observed.TaskContentRect).AvailableWidthDip:F2}DIP");
        Console.WriteLine("Tray lifecycle passed: live taskbar geometry.");
        Environment.Exit(0);
    }
}
