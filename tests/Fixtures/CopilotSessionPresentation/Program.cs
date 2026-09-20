using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pagurian;
using Pagurian.Modules.Copilot;
using Pagurian.Sdk;
using Windows.Foundation;
using Windows.Graphics;
using static Microsoft.UI.Reactor.Factories;
using XamlButton = Microsoft.UI.Xaml.Controls.Button;
using XamlText = Microsoft.UI.Xaml.Controls.TextBlock;

// Production components + host preparation, but no CopilotShell.Startup, tracker,
// hook installer, taskbar injection, user config, paid API or active app access.
ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    var harness = new Harness();
    Application.Current.UnhandledException += (_, args) => harness.Fail(args.Exception.ToString());
    harness.Start();
});

sealed class FixtureTheme : IThemeService
{
    public bool IsDark { get; private set; } = true;
    public Brush TextBrush { get; } = new SolidColorBrush(Microsoft.UI.Colors.White);
    public event Action? Changed;
    public void Refresh(bool dark) { IsDark = dark; Changed?.Invoke(); }
}

sealed class FixtureShell : Shell;
sealed record CellProbe(ShellCellProps Props, string Status, string Project)
{
    public FrameworkElement? Root;
}

sealed class CellsView(IReadOnlyList<CellProbe> cells) : Component
{
    public override Element Render() => VStack(0,
        Enumerable.Range(0, 3).Select(row =>
            HStack(0, cells.Skip(row * 5).Take(5).Select(cell =>
                Border(new ComponentElement(Harness.Type("SessionCell"), cell.Props))
                    .Height(48)
                    .OnMount(root => cell.Root = root)
                    .WithKey(cell.Props.Model!.GetHashCode().ToString())).ToArray())
                .WithKey(row.ToString())).ToArray());
}

sealed class Harness
{
    private static readonly Assembly Module = typeof(CopilotShell).Assembly;
    private readonly FixtureTheme _theme = new();
    private readonly FixtureShell _shell = new();
    private readonly List<CellProbe> _cells = [];
    private readonly Queue<Action> _steps = new();
    private ReactorWindow? _cellWindow;
    private BillboardSession? _billboard;
    private DispatcherQueueTimer? _timer;
    private object _model = null!;
    private Array _nodes = null!;
    private string[] _recentLines = [];
    private int _checks;
    private bool _finished;
    private nint _foreground;
    private readonly ManualResetEventSlim _releaseClipboard = new(false);
    private Task? _clipboardHolder;
    internal static Type Type(string name) => Module.GetType("Pagurian.Modules.Copilot." + name, true)!;
    private static object EnumValue(string type, string value) => Enum.Parse(Type(type), value);
    private static void Set(object target, string property, object? value)
    {
        var member = target.GetType().GetProperty(property)!;
        // ProjectName can be a derived basename in the parent's state contract.
        if (property == "ProjectName" && !member.CanWrite) return;
        member.SetValue(target, value);
    }
    private static object New(string type) => Activator.CreateInstance(Type(type), nonPublic: true)!;
    private static object Details()
    {
        var value = New("CopilotSessionDetails");
        Set(value, "Model", "fixture-model");
        Set(value, "InputTokens", 1234L);
        Set(value, "OutputTokens", 42L);
        Set(value, "ContextTokens", 512L);
        Set(value, "ContextLimit", 128000L);
        Set(value, "NanoAiu", 9007199254740993L);
        Set(value, "IsPartial", true);
        return value;
    }

    public void Start()
    {
        try
        {
            typeof(Shell).GetProperty(nameof(Shell.Theme))!.SetValue(_shell, _theme);
            foreach (string status in new[] { "Idle", "Working", "Blocked" })
            foreach (string client in new[] { "App", "Cli", "VSCode", "Other", "Unknown" })
            {
                var model = New("CopilotSession");
                Set(model, "SessionId", $"fixture-{status}-{client}");
                Set(model, "Name", "Fixture session");
                Set(model, "Cwd", @"D:\fixture\A very long project name that must be ellipsized");
                Set(model, "ProjectName", "A very long project name that must be ellipsized");
                Set(model, "Client", EnumValue("CopilotClientKind", client));
                Set(model, "Status", EnumValue("CopilotSessionStatus", status));
                _cells.Add(new(new(_shell, model, null, null, null), status,
                    (string)model.GetType().GetProperty("ProjectName")!.GetValue(model)!));
            }
            _cellWindow = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Isolated Copilot presentation fixture", Width = 640, Height = 144,
                NoActivate = true, ActivateOnOpen = false, ShowInTaskbar = false,
                ShowInSwitcher = false, Style = WindowStyle.None,
            }, () => new CellsView(_cells));
            _cellWindow.Show();
            OpenBillboard();
            _steps.Enqueue(CheckCells);
            _steps.Enqueue(() =>
            {
                Require(_billboard!.State == BillboardSessionState.Visible, "billboard not visible");
                var window = _billboard.Window!;
                Require(window.Spec.NoActivate, "NoActivate policy changed");
                Require(_billboard.BoundsPx.Height / window.DipScale <= 642, "billboard exceeds 640 DIP ceiling");
                Require(Descendants<ScrollViewer>(window.NativeWindow.Content).Any(s => s.ScrollableHeight > 0),
                    "long body has no bounded scroll viewport");
                Require(Texts().Count(text => text.Text == "fixture-grandchild") == 1,
                    "grandchild breakdown absent");
                Require(Texts().Any(text => text.Text == "Active · Idle"), "root local Idle was replaced by aggregate Blocked");
                Require(_recentLines.Take(5).All(line => Texts().Any(text => text.Text == line)),
                    "recent hooks do not show their exact three-field lines");
                Require(!Texts().Any(text => text.Text == _recentLines[5]), "more than five recent hooks rendered");
                Require(Texts().Any(text => text.Text.Contains("9,007,199,254,740,993") ||
                    text.Text.Contains(9007199254740993L.ToString("N0"))), "nano-AIU lost integer precision");
                Invoke("Collapse Fixture main");
            });
            _steps.Enqueue(() =>
            {
                Require(Texts().Count(text => text.Text == "fixture-grandchild") == 1,
                    "collapse hid all-node breakdown");
                // Publish a different immutable node list and refresh the
                // production subscriptions; expansion must survive re-render.
                Set(_model, "Nodes", _nodes.Clone());
                _theme.Refresh(false);
            });
            _steps.Enqueue(() =>
            {
                Require(Buttons().Any(button => AutomationProperties.GetName(button) == "Expand Fixture main"),
                    "live update reset expansion");
                Invoke("Expand Fixture main");
                CheckCells();
            });
            _steps.Enqueue(() =>
            {
                Require(Texts().Any(text => text.Text == "Completed · Idle"), "completed lifecycle missing");
                _foreground = GetForegroundWindow();
                if (Environment.GetCommandLineArgs().Contains("--clipboard"))
                    Invoke("Copy full Main session ID");
            });
            _steps.Enqueue(() =>
            {
                if (Environment.GetCommandLineArgs().Contains("--clipboard"))
                {
                    Require(ReadClipboard() == "fixture-main-full-session-id", "copy button did not copy complete ID");
                    Require(Texts().Any(text => text.Text == "Copied full session ID."), "copy success feedback missing");
                    Require(GetForegroundWindow() == _foreground, "copy activated the billboard");
                    using var ready = new ManualResetEventSlim(false);
                    bool opened = false;
                    _clipboardHolder = Task.Run(() =>
                    {
                        opened = OpenClipboard(0);
                        ready.Set();
                        if (opened)
                        {
                            try { _releaseClipboard.Wait(TimeSpan.FromSeconds(5)); }
                            finally { CloseClipboard(); }
                        }
                    });
                    Require(ready.Wait(TimeSpan.FromSeconds(2)) && opened, "could not stage clipboard contention");
                    Invoke("Copy full Main session ID");
                }
            });
            _steps.Enqueue(() =>
            {
                if (_clipboardHolder is not null)
                {
                    try
                    {
                        Require(Texts().Any(text => text.Text.StartsWith("Copy failed:", StringComparison.Ordinal)),
                            "clipboard contention falsely reported success");
                        Require(GetForegroundWindow() == _foreground, "failed copy activated the billboard");
                    }
                    finally
                    {
                        _releaseClipboard.Set();
                        _clipboardHolder.Wait(TimeSpan.FromSeconds(2));
                    }
                }
                Finish();
            });
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(600);
            _timer.Tick += (_, _) =>
            {
                try { _steps.Dequeue()(); }
                catch (Exception ex) { Fail(ex.ToString()); }
            };
            _timer.Start();
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }

    private void OpenBillboard()
    {
        _model = New("CopilotSession");
        Set(_model, "SessionId", "fixture-main-full-session-id");
        Set(_model, "Name", "Fixture main");
        Set(_model, "Cwd", @"D:\fixture\very-long-project");
        Set(_model, "ProjectName", "very-long-project");
        Set(_model, "Client", EnumValue("CopilotClientKind", "App"));
        Set(_model, "Status", EnumValue("CopilotSessionStatus", "Blocked"));
        Set(_model, "Details", Details());
        Set(_model, "GroupDetails", Details());
        _nodes = Array.CreateInstance(Type("CopilotSessionNode"), 3);
        object Node(string id, string? parent, string name, string status, string lifecycle) =>
            Activator.CreateInstance(Type("CopilotSessionNode"),
                id, parent, name, EnumValue("CopilotSessionStatus", status), lifecycle, null, Details())!;
        _nodes.SetValue(Node("fixture-main-full-session-id", null, "Fixture main", "Idle", "Active"), 0);
        _nodes.SetValue(Node("fixture-child", "fixture-main-full-session-id", "Fixture child", "Blocked", "Active"), 1);
        _nodes.SetValue(Node("fixture-grandchild", "fixture-child", "Fixture grandchild", "Idle", "Completed"), 2);
        Set(_model, "Nodes", _nodes);
        var hooks = Array.CreateInstance(Type("CopilotRecentHook"), 6);
        _recentLines = new string[6];
        var constructor = Type("CopilotRecentHook").GetConstructors()
            .Single(candidate => candidate.GetParameters().Length == 4);
        for (int index = 0; index < hooks.Length; index++)
        {
            int current = index;
            var hook = constructor.Invoke(constructor.GetParameters().Select(parameter =>
                parameter.Name!.ToLowerInvariant() switch
                {
                    "at" => (object)DateTimeOffset.Now.AddSeconds(-current),
                    "name" => $"fixtureHook{current}",
                    "toolname" => current == 0 ? "-" : "fixtureTool",
                    "sequence" => (object)(100L - current),
                    _ => throw new InvalidOperationException("Unexpected recent-hook constructor contract"),
                }).ToArray());
            hooks.SetValue(hook, index);
            _recentLines[index] = (string)hook.GetType().GetProperty("Line")!.GetValue(hook)!;
        }
        Set(_model, "RecentHooks", hooks);
        var view = (Billboard)Activator.CreateInstance(Type("SessionBillboard"), _model)!;
        var props = new ShellCellProps(_shell, _model, null, null, null);
        typeof(Billboard).GetProperty("OwnerCell", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, new ShellCellHandle("fixture", Type("SessionCell"), props));
        var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        var anchor = new BillboardAnchor(new RectInt32(work.X + 32, work.Y + work.Height - 48, 100, 40), work);
        _billboard = new(BillboardSession.CreateSpec(view.Title, view.WidthDip, view.HeightDip), view, _theme,
            () => anchor, view.OnOpened, view.OnClosed, Console.WriteLine);
        _billboard.Start();
    }

    private void CheckCells()
    {
        var widths = new List<double>();
        foreach (var cell in _cells)
        {
            var root = cell.Root!;
            var labels = Descendants<XamlText>(root).ToArray();
            var status = labels.Single(text => text.Text == cell.Status);
            var project = labels.Single(text => text.Text == cell.Project);
            var statusPoint = status.TransformToVisual(root).TransformPoint(new Point());
            var projectPoint = project.TransformToVisual(root).TransformPoint(new Point());
            Require(projectPoint.Y >= statusPoint.Y + status.ActualHeight - 1, "cell labels are not two rows");
            Require(projectPoint.Y + project.ActualHeight <= 49, "two-line cell exceeds taskbar slot");
            Require(project.IsTextTrimmed, "long project is not ellipsized in finite column");
            widths.Add(root.ActualWidth);
        }
        Require(widths.Max() - widths.Min() < 0.5, "client/status variants have different widths");
        Require(Math.Abs(widths[0] / 4 - Math.Round(widths[0] / 4)) < .01, "width not rounded to 4 DIP");
    }

    private IEnumerable<XamlText> Texts() => Descendants<XamlText>(_billboard!.Window!.NativeWindow.Content);
    private IEnumerable<XamlButton> Buttons() => Descendants<XamlButton>(_billboard!.Window!.NativeWindow.Content);
    private void Invoke(string name)
    {
        var button = Buttons().Single(button => AutomationProperties.GetName(button) == name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static string ReadClipboard()
    {
        if (!OpenClipboard(0)) throw new InvalidOperationException("Clipboard busy during readback");
        try
        {
            nint memory = GetClipboardData(13), data = GlobalLock(memory);
            if (data == 0) throw new InvalidOperationException("No Unicode clipboard data");
            try { return Marshal.PtrToStringUni(data)!; }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timer?.Stop();
        _billboard?.Close();
        _cellWindow?.Close();
        Console.WriteLine($"Copilot presentation passed: {_checks} checks. Clipboard test: " +
            (Environment.GetCommandLineArgs().Contains("--clipboard") ? "passed" : "not requested"));
        ReactorApp.Exit(0);
        Environment.Exit(0);
    }
    public void Fail(string error)
    {
        Console.Error.WriteLine(error);
        _releaseClipboard.Set();
        _timer?.Stop();
        _billboard?.Close();
        _cellWindow?.Close();
        ReactorApp.Exit(1);
        Environment.Exit(1);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}
