using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pagurian;
using Pagurian.Sdk;
using Windows.Graphics;
using static Microsoft.UI.Reactor.Factories;
using XamlBorder = Microsoft.UI.Xaml.Controls.Border;

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    var harness = new Harness();
    Application.Current.UnhandledException += (_, args) => harness.Fail(args.Exception.ToString());
    harness.Run();
});

sealed class FakeTheme : IThemeService
{
    public bool IsDark { get; private set; } = true;
    public Brush TextBrush { get; } = new SolidColorBrush(Microsoft.UI.Colors.White);
    public event Action? Changed;
    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
    public void Flip() { IsDark = !IsDark; Changed?.Invoke(); }
}

sealed class Probe
{
    public BillboardSession Session { get; set; } = null!;
    public Action<double>? SetHeight;
    public Action<bool>? SetMissing;
    public int Opens, Closes, Unmounts;
    public bool MountedVisible, UnmountedVisible, OwnerExists = true;
    public List<string> Log { get; } = [];
    public FakeTheme Theme { get; init; } = new();
    public bool Synthetic = true;
    public nint Hwnd;
}

sealed class TestContent(Probe probe, double initialHeight, bool initiallyMissing = false) : Component
{
    public override Element Render()
    {
        var (height, setHeight) = UseState(initialHeight);
        var (missing, setMissing) = UseState(initiallyMissing);
        probe.SetHeight = setHeight;
        probe.SetMissing = setMissing;
        UseEffect(() => () =>
        {
            probe.Unmounts++;
            probe.UnmountedVisible |= Harness.IsWindowVisible(probe.Hwnd);
        }, Array.Empty<object>());
        if (missing) return null!;
        // A scrollable root with padding reproduces the Metrics layout shape.
        return Border((ScrollViewer(
                Border(Body("Billboard lifecycle test")).Height(height)
                    .Background(Microsoft.UI.Reactor.Core.Theme.Accent)) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch))
            .Padding(8)
            .OnMount(_ => probe.MountedVisible |= Harness.IsWindowVisible(
                WinRT.Interop.WindowNative.GetWindowHandle(probe.Session.Window!.NativeWindow)));
    }
}

sealed class Harness
{
    private readonly Queue<(int DelayMs, Action Action)> _steps = new();
    private DispatcherQueueTimer _timer = null!;
    private ReactorWindow _lifetime = null!;
    private Probe? _current;
    private RectInt32 _work;
    private bool _finished;
    private int _checks;
    private RectInt32 _fixedBounds;
    private bool _cancelOnPreparingFrame;
    private int _preparingFrameChecks;
    private object? _compassOverview;
    private Type? _compassModelType;
    private Billboard? _compassDetail;
    private string? _compassAssembly;

    public void Run()
    {
        try
        {
            _work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            var arguments = Environment.GetCommandLineArgs();
            bool capture = arguments.Contains("--capture");
            int compassIndex = Array.IndexOf(arguments, "--compass");
            if (compassIndex >= 0) _compassAssembly = arguments[compassIndex + 1];
            _lifetime = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Billboard test lifetime", Width = capture ? 600 : 1, Height = capture ? 700 : 1,
                Style = WindowStyle.None, ShowInSwitcher = false, Level = WindowLevel.AlwaysOnTop,
                ActivateOnOpen = false, NoActivate = true, ShowInTaskbar = false,
            }, () => new EmptyContent());
            if (capture)
            {
                _lifetime.AppWindow.MoveAndResize(new RectInt32(_work.X + 16, _work.Y + 16, 550, 650));
                _lifetime.Show();
                Console.WriteLine($"Capture region: {_work.X + 16},{_work.Y + 16},550,650 hwnd={WinRT.Interop.WindowNative.GetWindowHandle(_lifetime.NativeWindow)}");
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CheckPreparingFrame;
            Geometry();
            Step(1, () => _current = Open(100));
            Step(250, () =>
            {
                CheckVisible(_current!, 116);
                _fixedBounds = _current!.Session.BoundsPx;
                _current.SetHeight!(900);
            });
            Step(150, () =>
            {
                Require(_current!.Session.BoundsPx.Equals(_fixedBounds), "live content resized the window");
                _current.Theme.Flip();
            });
            Step(100, () =>
            {
                var root = (XamlBorder)_current!.Session.Window!.NativeWindow.Content;
                Require(root.RequestedTheme == ElementTheme.Light, "live theme did not reach window root");
                Dismiss(_current);
                _current.Theme.Flip();
                Require(_current.Theme.Subscribers == 0, "closed theme subscription leaked");
                _current = Open(3000, maxHeightPx: 220);
            });
            Step(200, () =>
            {
                CheckVisible(_current!, null);
                Require(_current!.Session.BoundsPx.Height <= 220, "overflow escaped work area");
                var outer = (XamlBorder)_current.Session.Window!.Host.ContentTarget!.Child;
                var scroll = (Microsoft.UI.Xaml.Controls.ScrollViewer)outer.Child;
                Require(scroll.ScrollableHeight > 0, "constrained content is not scrollable");
                Dismiss(_current);
                var cancelled = Open(100);
                cancelled.Session.Close();
                Require(cancelled.Opens == 0 && cancelled.Closes == 0, "cancelled opening invoked lifecycle hooks");
                _current = Open(140);
            });
            Step(150, () => { CheckVisible(_current!, 156); Dismiss(_current!); _current = Open(100, missing: true); });
            Step(200, () =>
            {
                Require(_current!.Session.State == BillboardSessionState.Preparing && _current.Opens == 0,
                    "missing content revealed an empty window");
                _current.SetMissing!(false);
            });
            Step(200, () => { CheckVisible(_current!, 116); Dismiss(_current!); _current = Open(100); _current.OwnerExists = false; });
            Step(100, () =>
            {
                Require(_current!.Session.State == BillboardSessionState.Closed && _current.Opens == 0,
                    "removed owner was revealed");
                _current = Open(100, missing: true);
            });
            Step(2300, () =>
            {
                Require(_current!.Session.State == BillboardSessionState.Closed && _current.Opens == 0,
                    "missing content watchdog failed");
                Require(_current.Log.Any(x => x.Contains("timed out")), "timeout was not logged");
                _current = Open(180);
            });
            for (int i = 0; i < 12; i++)
            {
                Step(80, () => { CheckVisible(_current!, 196); Dismiss(_current!); _current = Open(180); });
            }
            Step(150, () =>
            {
                CheckVisible(_current!, 196);
                Dismiss(_current!);
                _cancelOnPreparingFrame = true;
                _current = Open(100);
            });
            Step(150, () =>
            {
                Require(!_cancelOnPreparingFrame && _current!.Session.State == BillboardSessionState.Closed,
                    "cancellation during cloaked rendering failed");
                Require(_current!.Opens == 0 && _current.Closes == 0, "cancelled rendering invoked lifecycle hooks");
                Require(_preparingFrameChecks > 0, "cloaked preparation was never observed");
                _current = OpenProduction(new Pagurian.Modules.Hello.HelloShell(), "HelloBillboard");
            });
            Step(800, () =>
            {
                CheckVisible(_current!, null);
                Require(_current!.Session.BoundsPx.Height / _current.Session.Window!.DipScale < 288,
                    "Hello's flexible layout expanded to the monitor instead of fitting content");
                Dismiss(_current!);
                SeedMetrics();
                _current = OpenProduction(new Pagurian.Modules.Metrics.CpuShell(), "CpuBillboard"); });
            Step(800, () => { CheckVisible(_current!, null); Dismiss(_current!);
                _current = OpenProduction(new Pagurian.Modules.Metrics.MemShell(), "MemBillboard"); });
            Step(800, () => { CheckVisible(_current!, null); Dismiss(_current!); });
            if (_compassAssembly is not null)
            {
                Step(1, OpenCompass);
                Step(800, () =>
                {
                    CheckVisible(_current!, Math.Min(520, 600 / _current!.Session.Window!.DipScale));
                    _fixedBounds = _current.Session.BoundsPx;
                    InvokeButton(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button).StartsWith("查看 Build"));
                });
                Step(200, () =>
                {
                    Require(_current!.Session.BoundsPx.Equals(_fixedBounds), "Compass navigation resized window");
                    Require(Buttons(_current.Session.Window!.NativeWindow.Content).Any(button =>
                        button.Content?.ToString()?.Contains("返回 build 列表") == true), "Compass detail navigation failed");
                    InvokeButton(button => button.Content?.ToString()?.Contains("返回 build 列表") == true);
                });
                Step(200, () =>
                {
                    Require(_current!.Session.BoundsPx.Equals(_fixedBounds), "Compass Back resized window");
                    _compassOverview!.GetType().GetMethod("UpdateBuilds")!.Invoke(_compassOverview,
                        [Array.CreateInstance(_compassModelType!, 0)]);
                });
                Step(200, () =>
                {
                    Require(_current!.Session.BoundsPx.Equals(_fixedBounds), "empty Compass list resized window");
                    Dismiss(_current);
                    var theme = new FakeTheme();
                    Bind(_compassDetail!, new TestShell(), theme);
                    _current = Open(100, maxHeightPx: 220, realContent: _compassDetail, theme: theme);
                });
                Step(800, () =>
                {
                    CheckVisible(_current!, null);
                    Require(_current!.Session.BoundsPx.Height <= 220, "Compass detail exceeded monitor constraint");
                    Dismiss(_current);
                });
            }
            Step(1, Finish);
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) =>
            {
                try { _steps.Dequeue().Action(); Next(); }
                catch (Exception ex) { Fail(ex.ToString()); }
            };
            Next();
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }

    private Probe Open(double height, int? maxHeightPx = null, bool missing = false, Billboard? realContent = null, FakeTheme? theme = null)
    {
        var probe = new Probe { Theme = theme ?? new(), Synthetic = realContent is null };
        var area = new RectInt32(_work.X + 32, _work.Y + 32,
            Math.Min(500, _work.Width - 64), maxHeightPx ?? Math.Min(600, _work.Height - 64));
        var anchor = new BillboardAnchor(new RectInt32(area.X, area.Y + area.Height, 30, 30), area);
        // Use the same creation spec as the taskbar click path. A separately
        // assembled test spec previously missed an invalid ManualPosition.
        var spec = BillboardSession.CreateSpec("Billboard lifecycle test",
            realContent?.WidthDip ?? 320, realContent?.HeightDip ?? 400);
        spec.Validate();
        probe.Session = new BillboardSession(spec,
            (Component?)realContent ?? new TestContent(probe, height, missing), probe.Theme,
            () => probe.OwnerExists ? anchor : null,
            () => { probe.Opens++; realContent?.OnOpened(); },
            () => { probe.Closes++; realContent?.OnClosed(); }, probe.Log.Add);
        probe.Session.Start();
        Require(probe.Session.Window is not null && probe.Session.State == BillboardSessionState.Preparing,
            "window creation failed: " + string.Join(" | ", probe.Log));
        if (probe.Session.Window is { } window)
            probe.Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
        Require(!IsWindowVisible(probe.Hwnd), "Start revealed the window before queued preparation");
        return probe;
    }

    private static string Describe(UIElement? element)
    {
        if (element is null) return "null";
        if (element is Microsoft.UI.Xaml.Controls.TextBlock text) return text.Text;
        if (element is XamlBorder border) return Describe(border.Child);
        if (element is Microsoft.UI.Xaml.Controls.Panel panel)
            return string.Join("; ", panel.Children.Select(Describe));
        if (element is ContentControl control) return control.Content is UIElement child ? Describe(child) : control.Content?.ToString() ?? "";
        return element.GetType().Name;
    }

    private void CheckVisible(Probe probe, double? desiredHeight)
    {
        Require(probe.Session.State == BillboardSessionState.Visible,
            "window did not reveal: " + string.Join(" | ", probe.Log) + " content=" + Describe(probe.Session.Window?.Host.ContentTarget?.Child) +
            " state=" + string.Join(",", typeof(BillboardSession).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(bool) || f.FieldType == typeof(int)).Select(f => $"{f.Name}={f.GetValue(probe.Session)}")) +
            $" loaded={probe.Session.Window?.Host.ContentTarget?.IsLoaded} nativeVisible={IsWindowVisible(probe.Hwnd)}");
        Require(probe.Opens == 1 && !probe.MountedVisible, "incorrect opening order");
        Require(IsWindowVisible(probe.Hwnd) && !IsCloaked(probe.Hwnd), "window is not natively revealed");
        var window = probe.Session.Window!;
        Require(window.Spec.SizeToContent == WindowSizeToContent.Manual, "autosizing remains enabled");
        var root = (XamlBorder)window.NativeWindow.Content;
        Require(root.IsLoaded && root.ActualHeight > 0 && root.Child is not null, "revealed empty/unloaded content");
        if (desiredHeight is { } h)
            Require(Math.Abs(window.AppWindow.Size.Height / window.DipScale - h) <= 2,
                $"height did not fit content: actual={window.AppWindow.Size.Height / window.DipScale}, expected={h}");
    }

    private void Dismiss(Probe probe)
    {
        probe.Session.Close();
        probe.Session.Close();
        Require(probe.Session.State == BillboardSessionState.Closed, "close incomplete");
        Require(probe.Closes == 1 && (!probe.Synthetic || probe.Unmounts == 1), "teardown was not exactly once");
        Require(probe.Theme.Subscribers == 0, "host or module theme subscription leaked");
        Require(!probe.UnmountedVisible, "content teardown occurred while HWND was visible");
        Require(!IsWindowVisible(probe.Hwnd), "closed HWND still visible");
    }

    private Probe OpenProduction(Shell shell, string billboardName)
    {
        var theme = new FakeTheme();
        var type = shell.GetType().Assembly.GetType(shell.GetType().Namespace + "." + billboardName)!;
        var billboard = (Billboard)Activator.CreateInstance(type, nonPublic: true)!;
        Bind(billboard, shell, theme);
        return Open(100, realContent: billboard, theme: theme);
    }

    private static void Bind(Billboard billboard, Shell shell, FakeTheme theme)
    {
        typeof(Shell).GetProperty(nameof(Shell.Theme))!.SetValue(shell, theme);
        var cell = new ShellCellHandle("fixture", typeof(TestCell), new ShellCellProps(shell, null, null, null, null));
        typeof(Billboard).GetProperty("OwnerCell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(billboard, cell);
    }

    private void OpenCompass()
    {
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(_compassAssembly!));
        Type Type(string name) => assembly.GetType("Pagurian.Modules.Compass." + name)!;
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "id": 42, "status": "In Progress", "changelist": 123,
              "description": "Isolated billboard fixture", "tasks": [] }
            """);
        var snapshot = Type("CompassBuild").GetMethod("FromDetails")!.Invoke(null, [json.RootElement]);
        _compassModelType = Type("CompassBuildModel");
        var model = Activator.CreateInstance(_compassModelType, snapshot)!;
        _compassOverview = Activator.CreateInstance(Type("CompassOverviewModel"), nonPublic: true)!;
        var models = Array.CreateInstance(_compassModelType, 1);
        models.SetValue(model, 0);
        _compassOverview.GetType().GetMethod("UpdateBuilds")!.Invoke(_compassOverview, [models]);
        var overview = (Billboard)Activator.CreateInstance(Type("CompassOverviewBillboard"), _compassOverview, (Action<int>)(_ => { }))!;
        _compassDetail = (Billboard)Activator.CreateInstance(Type("CompassBuildBillboard"), model, (Action)(() => { }))!;
        var theme = new FakeTheme();
        Bind(overview, new TestShell(), theme);
        _current = Open(100, realContent: overview, theme: theme);
    }

    private static IEnumerable<Microsoft.UI.Xaml.Controls.Button> Buttons(DependencyObject element)
    {
        if (element is Microsoft.UI.Xaml.Controls.Button button) yield return button;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var child in Buttons(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i))) yield return child;
    }

    private void InvokeButton(Func<Microsoft.UI.Xaml.Controls.Button, bool> predicate)
    {
        var button = Buttons(_current!.Session.Window!.NativeWindow.Content).First(predicate);
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
            Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }

    private static void SeedMetrics()
    {
        // Seed immutable data in the isolated process. No samplers, module
        // startup, user settings, or user log are involved.
        var assembly = typeof(Pagurian.Modules.Metrics.CpuShell).Assembly;
        Type Type(string name) => assembly.GetType("Pagurian.Modules.Metrics." + name)!;
        var cpuProcesses = Array.CreateInstance(Type("CpuProcessUsage"), 3);
        var memProcesses = Array.CreateInstance(Type("MemoryProcessUsage"), 3);
        for (int i = 0; i < 3; i++)
        {
            cpuProcesses.SetValue(Activator.CreateInstance(Type("CpuProcessUsage"), 100 + i, "Test process " + i, 12.0 - i), i);
            memProcesses.SetValue(Activator.CreateInstance(Type("MemoryProcessUsage"), 100 + i, "Test process " + i, 100000000L, 10.0), i);
        }
        var cpu = Activator.CreateInstance(Type("CpuSnapshot"), 30.0, Enumerable.Repeat(30.0, 16).ToArray(), cpuProcesses, DateTimeOffset.UtcNow);
        var memory = Activator.CreateInstance(Type("MemorySnapshot"), 16000000000UL, 8000000000UL, 50.0, memProcesses, DateTimeOffset.UtcNow);
        Type("SystemMetricsTracker").GetProperty("Cpu")!.SetValue(null, cpu);
        Type("SystemMetricsTracker").GetProperty("Memory")!.SetValue(null, memory);
    }

    private void CheckPreparingFrame(object? sender, object args)
    {
        try
        {
            if (_current is not { } probe || probe.Session.State != BillboardSessionState.Preparing ||
                !IsWindowVisible(probe.Hwnd)) return;
            _preparingFrameChecks++;
            Require(IsCloaked(probe.Hwnd) && probe.Opens == 0, "preparation exposed an unfinished frame");
            if (_cancelOnPreparingFrame)
            {
                _cancelOnPreparingFrame = false;
                probe.Session.Close();
            }
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }

    private static bool IsCloaked(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)));
        return cloaked != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private void Geometry()
    {
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            var work = new RectInt32(-1600, -900, 1600, 900);
            var bottom = new BillboardAnchor(new RectInt32(-30, 0, 30, 48), work);
            var top = new BillboardAnchor(new RectInt32(-1600, -948, 30, 48), work);
            var above = BillboardSession.Place(bottom, 400, 200, scale);
            var below = BillboardSession.Place(top, 400, 200, scale);
            Require(above.X == -400 && above.Y == -(int)(6 * scale) - 200, "bottom/right anchoring failed");
            Require(below.Y == -900 + (int)(6 * scale), "top anchoring failed");
            var full = BillboardSession.Place(bottom, 3000, 3000, scale);
            Require(full.Equals(work), "full-work-area clamp failed");
        }
    }

    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Step(int delayMs, Action action) => _steps.Enqueue((delayMs, action));
    private void Next()
    {
        if (_finished || !_steps.TryPeek(out var step)) return;
        _timer.Interval = TimeSpan.FromMilliseconds(step.DelayMs);
        _timer.Start();
    }
    public void Fail(string message)
    {
        if (_finished) return;
        _finished = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CheckPreparingFrame;
        _timer?.Stop();
        Console.WriteLine("Billboard lifecycle failure: " + message);
        _current?.Session.Close();
        ReactorApp.Exit(1);
        Environment.Exit(1);
    }
    private void Finish()
    {
        _finished = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CheckPreparingFrame;
        _timer.Stop();
        Console.WriteLine($"Billboard lifecycle passed: {_checks} assertions; fit, scrolling, theme, cancellation, timeout, rapid switching, hidden teardown, real Hello/CPU/Memory components; Compass={_compassAssembly is not null}.");
        ReactorApp.Exit(0);
        Environment.Exit(0);
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);
}

sealed class EmptyContent : Component
{
    public override Element Render() => Border(null)
        .RequestedTheme(ElementTheme.Dark)
        .Background(Microsoft.UI.Reactor.Core.Theme.Ref("SolidBackgroundFillColorBaseBrush"));
}

sealed class TestCell : ShellCell
{
    public override Element Render() => Border(null);
}

sealed class TestShell : Shell { }
