using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
sealed class CellProbe(
    ShellCellProps props,
    string status,
    string project,
    string name,
    bool isLarge,
    bool hasProjectBoundaries)
{
    public ShellCellProps Props { get; } = props;
    public string Status { get; } = status;
    public string Project { get; } = project;
    public string Name { get; } = name;
    public bool IsLarge { get; set; } = isLarge;
    public bool HasProjectBoundaries { get; } = hasProjectBoundaries;
    public FrameworkElement? Root;
}

sealed class CellsView(IReadOnlyList<CellProbe> cells) : Component
{
    public override Element Render() => VStack(0,
        Enumerable.Range(0, (int)Math.Ceiling(cells.Count / 5.0)).Select(row =>
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
    private string[] _mainHookLines = [];
    private string[] _childHookLines = [];
    private string[] _grandchildHookLines = [];
    private CellProbe _switchingLargeCell = null!;
    private double _mediumWidth;
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
    private static object New(string type, params object?[] arguments) =>
        Activator.CreateInstance(
            Type(type),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            arguments,
            culture: null)!;
    private static object Details()
    {
        var value = New("CopilotSessionDetails");
        Set(value, "Model", "fixture-model");
        Set(value, "ReasoningEffort", "high");
        Set(value, "InputTokens", 1234L);
        Set(value, "OutputTokens", 42L);
        Set(value, "NanoAiu", 9007199254740993L);
        Set(value, "IsPartial", true);
        return value;
    }

    public void Start()
    {
        try
        {
            typeof(Shell).GetProperty(nameof(Shell.Theme))!.SetValue(_shell, _theme);
            VerifyIconSizeSettings();
            foreach (string status in new[] { "Idle", "Working", "Blocked", "Unknown" })
            foreach (string client in new[] { "App", "Cli", "VSCode", "Other", "Unknown" })
                _cells.Add(CreateCell(status, client, false,
                    "A very long project name that must be ellipsized",
                    "Fixture session title"));
            foreach (string status in new[] { "Idle", "Working", "Blocked", "Unknown" })
            {
                var cell = CreateCell(status, "App", true,
                    status == "Idle" ? "Short" : "A very long project name that must be ellipsized",
                    status == "Idle" ? "Short session" : "A very long fixture session title that must be ellipsized");
                _cells.Add(cell);
                if (status == "Working")
                    _switchingLargeCell = cell;
            }
            _cellWindow = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Isolated Copilot presentation fixture", Width = 720, Height = 240,
                NoActivate = true, ActivateOnOpen = false, ShowInTaskbar = false,
                ShowInSwitcher = false, Style = WindowStyle.None,
            }, () => new CellsView(_cells));
            _cellWindow.Show();
            OpenBillboard();
            _steps.Enqueue(CheckCells);
            _steps.Enqueue(() =>
            {
                _mediumWidth = _cells.First(cell => !cell.IsLarge).Root!.ActualWidth;
                SetIconSize(_switchingLargeCell, "Medium");
                _switchingLargeCell.IsLarge = false;
            });
            _steps.Enqueue(() =>
            {
                Require(Math.Abs(_switchingLargeCell.Root!.ActualWidth - _mediumWidth) < .5,
                    "live icon-size switch did not restore Medium width");
                CheckCells();
            });
            _steps.Enqueue(() =>
            {
                Require(_billboard!.State == BillboardSessionState.Visible, "billboard not visible");
                var window = _billboard.Window!;
                Require(!window.Spec.NoActivate, "revealed billboards must be activated (25H2 focus-gated acrylic)");
                Require(_billboard.BoundsPx.Height / window.DipScale <= 642, "billboard exceeds 640 DIP ceiling");
                Require(Descendants<ScrollViewer>(window.NativeWindow.Content).Any(s => s.ScrollableHeight > 0),
                    "long body has no bounded scroll viewport");
                Require(!Texts().Any(text => text.Text is "Context tier" or "Prompt token limit" or "Context tokens / limit"),
                    "permanently-unavailable context rows must not render");
                // The session tree is a single-line list: each node name
                // appears once in its row plus once more only when that node
                // is selected in the details card (initially the root).
                Require(Texts().Count(text => text.Text == "Fixture grandchild") == 1,
                    "tree row is not single-line");
                Require(Texts().Count(text => text.Text == "Fixture main") == 3,
                    "header and selected root missing from the details card");
                Require(Texts().Any(text => text.Text == "Active · Idle"), "root local Idle was replaced by aggregate Blocked");
                // Status words sit between the status icon and the name on every
                // row. The header's aggregate StatusBadge duplicates the
                // session-level word ("Blocked" here), so count row words by
                // their distinct per-node lifecycle label instead.
                // Status words sit between the status icon and the name on every
                // row. Idle appears twice because the main node is both a tree
                // row and the initially-selected details card.
                if (!(Texts().Count(text => text.Text == "Idle") == 2 &&
                        Texts().Count(text => text.Text == "Blocked") == 1 &&
                        Texts().Count(text => text.Text == "Completed") == 1))
                    Console.WriteLine("DIAG status words: Idle=" +
                        Texts().Count(t => t.Text == "Idle") + " Blocked=" +
                        Texts().Count(t => t.Text == "Blocked") + " Completed=" +
                        Texts().Count(t => t.Text == "Completed"));
                Require(Texts().Count(text => text.Text == "Idle") == 2 &&
                        Texts().Count(text => text.Text == "Blocked") == 1 &&
                        Texts().Count(text => text.Text == "Completed") == 1,
                    "single-line tree rows are missing per-row status words");
                // Per-node hooks: the details card shows the main node's five
                // lines, never another node's, and nothing for a node with none.
                Require(_mainHookLines.All(line => Texts().Any(text => text.Text == line)),
                    "main-node recent hooks missing from the details card");
                Require(_childHookLines.Concat(_grandchildHookLines).All(line =>
                        !Texts().Any(text => text.Text == line)),
                    "details card leaked another node's recent hooks");
                Require(Texts().Any(text => text.Text.Contains("9,007,199,254,740,993") ||
                    text.Text.Contains(9007199254740993L.ToString("N0"))), "nano-AIU lost integer precision");
                Invoke("Collapse Fixture main");
            });
            _steps.Enqueue(() =>
            {
                // Collapsing the root hides its descendants; the main node
                // itself remains in its row and in the details card.
                Require(Texts().Count(text => text.Text == "Fixture grandchild") <= 1,
                    "collapse changed the tree row");
                Require(Texts().Count(text => text.Text == "Fixture main") == 3,
                    "collapse changed the details card");
                // Publish a different immutable node list and refresh the
                // production subscriptions; expansion must survive re-render.
                Set(_model, "Nodes", _nodes.Clone());
                _theme.Refresh(false);
            });
            _steps.Enqueue(() =>
            {
                Require(Buttons().Any(button => AutomationProperties.GetName(button) == "Expand Fixture main"),
                    "live update reset expansion");
                // Selection survives a re-render: the main node is still
                // selected after the immutable node list and theme changed.
                Require(Texts().Any(text => text.Text == "Active · Idle"),
                    "main-node selection did not survive re-render");
                Require(_mainHookLines.All(line => Texts().Any(text => text.Text == line)),
                    "main-node details did not survive re-render");
                Invoke("Expand Fixture main");
                CheckCells();
            });
            _steps.Enqueue(() =>
            {
                Require(Texts().Count(text => text.Text == "Fixture grandchild") <= 1,
                    "expand changed the tree row");
                Require(Texts().Any(text => text.Text == "Active · Idle"),
                    "main-node selection did not survive expand");
                // The details card follows the tree selection to the child.
                TapRow("Fixture child");
            });
            _steps.Enqueue(() =>
            {
                Require(Texts().Any(text => text.Text == "Active · Blocked"),
                    "details card did not follow the tree selection to the child");
                Require(Texts().Count(text => text.Text == "Fixture child") == 2 &&
                        Texts().Count(text => text.Text == "Fixture main") == 2,
                    "details card did not switch to the selected child node");
                Require(_childHookLines.All(line => Texts().Any(text => text.Text == line)),
                    "child-node recent hooks missing after selection");
                Require(_mainHookLines.Concat(_grandchildHookLines).All(line =>
                        !Texts().Any(text => text.Text == line)),
                    "details card leaked non-selected node hooks after selection");
                _foreground = GetForegroundWindow();
                if (Environment.GetCommandLineArgs().Contains("--clipboard"))
                    Invoke("Copy full Session ID for Fixture child");
            });
            _steps.Enqueue(() =>
            {
                if (Environment.GetCommandLineArgs().Contains("--clipboard"))
                {
                    Require(ReadClipboard() == "fixture-child", "copy button did not copy the selected node's ID");
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
                    Invoke("Copy full Session ID for Fixture child");
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

    private CellProbe CreateCell(
        string status,
        string client,
        bool isLarge,
        string project,
        string name)
    {
        var session = New("CopilotSession");
        Set(session, "SessionId", $"fixture-{status}-{client}-{isLarge}");
        Set(session, "Name", name);
        Set(session, "Cwd", @"D:\fixture\" + project);
        Set(session, "ProjectName", project);
        Set(session, "Client", EnumValue("CopilotClientKind", client));
        Set(session, "Status", EnumValue("CopilotSessionStatus", status));
        object model = isLarge
            ? New("CopilotSessionCellModel", session, EnumValue("CopilotSessionIconSize", "Large"))
            : session;
        return new(new(_shell, model, null, null, null), status, project, name, isLarge,
            isLarge);
    }

    private static void SetIconSize(CellProbe cell, string size) =>
        cell.Props.Model!.GetType().GetMethod("SetIconSize")!.Invoke(
            cell.Props.Model, [EnumValue("CopilotSessionIconSize", size)]);

    private void VerifyIconSizeSettings()
    {
        var settings = Type("CopilotSettings");
        var read = settings.GetMethod("Clients", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var write = settings.GetMethod("Write", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        object Read(string json) => read.Invoke(null, [JsonDocument.Parse(json).RootElement])!;
        string Size(object clients) => clients.GetType().GetProperty("IconSize")!.GetValue(clients)!.ToString()!;

        var missing = Read("{}");
        Require(Size(missing) == "Medium", "missing icon size must default to Medium");
        Require((bool)missing.GetType().GetProperty("CopilotCli")!.GetValue(missing)!,
            "missing clients must retain enabled defaults");
        Require(Size(Read("""{"iconSize":"unexpected"}""")) == "Medium",
            "invalid icon size must default to Medium");
        var large = Read("""{"iconSize":"LARGE","clients":{"copilotCli":false,"copilotApp":true,"vsCode":false}}""");
        Require(Size(large) == "Large", "large icon size was not read");
        var written = (JsonElement)write.Invoke(null, [large])!;
        Require(written.GetProperty("iconSize").GetString() == "large",
            "large icon size was not persisted canonically");
        Require(!written.GetProperty("clients").GetProperty("copilotCli").GetBoolean() &&
                !written.GetProperty("clients").GetProperty("vsCode").GetBoolean(),
            "icon-size write did not preserve client selections");
    }

    private static object[] BuildHooks(string tag, int count)
    {
        var constructor = Type("CopilotRecentHook").GetConstructors()
            .Single(candidate => candidate.GetParameters().Length == 4);
        var hooks = new object[count];
        for (int index = 0; index < count; index++)
        {
            int current = index;
            hooks[index] = constructor.Invoke(constructor.GetParameters().Select(parameter =>
                parameter.Name!.ToLowerInvariant() switch
                {
                    "at" => (object)DateTimeOffset.Now.AddSeconds(-current),
                    "name" => $"fixture{tag}Hook{current}",
                    "toolname" => current == 0 ? "-" : "fixtureTool",
                    "sequence" => (object)(100L - current),
                    _ => throw new InvalidOperationException("Unexpected recent-hook constructor contract"),
                }).ToArray());
        }
        return hooks;
    }

    private static string[] Lines(object[] hooks) => hooks
        .Select(hook => (string)hook.GetType().GetProperty("Line")!.GetValue(hook)!)
        .ToArray();

    private void OpenBillboard()
    {
        _model = New("CopilotSession");
        Set(_model, "SessionId", "fixture-main-full-session-id");
        Set(_model, "Name", "Fixture main");
        Set(_model, "Cwd", @"D:\fixture\very-long-project");
        Set(_model, "ProjectName", "very-long-project");
        Set(_model, "Client", EnumValue("CopilotClientKind", "App"));
        Set(_model, "Status", EnumValue("CopilotSessionStatus", "Idle"));
        Set(_model, "Details", Details());
        Set(_model, "GroupDetails", Details());
        _nodes = Array.CreateInstance(Type("CopilotSessionNode"), 3);
        object Node(string id, string? parent, string name, string status, string lifecycle, object[] hooks)
        {
            var nodeType = Type("CopilotSessionNode");
            var constructor = nodeType.GetConstructors()
                .Single(candidate => candidate.GetParameters().Length == 8);
            // The record's last parameter is IReadOnlyList<CopilotRecentHook>;
            // a plain object[] binds ambiguously through parameter reflection,
            // so copy into a typed List before invoking.
            var hookList = (System.Collections.IList)System.Activator.CreateInstance(
                typeof(List<>).MakeGenericType(Type("CopilotRecentHook")))!;
            foreach (var hook in hooks) hookList.Add(hook);
            return constructor.Invoke(
                [id, parent, name, EnumValue("CopilotSessionStatus", status), lifecycle, null, Details(), hookList]);
        }
        var hooksForMain = BuildHooks("main", 3);
        var hooksForChild = BuildHooks("child", 1);
        var hooksForGrandchild = BuildHooks("grandchild", 1);
        _mainHookLines = Lines(hooksForMain);
        _childHookLines = Lines(hooksForChild);
        _grandchildHookLines = Lines(hooksForGrandchild);
        _nodes.SetValue(Node("fixture-main-full-session-id", null, "Fixture main", "Idle", "Active", hooksForMain), 0);
        _nodes.SetValue(Node("fixture-child", "fixture-main-full-session-id", "Fixture child", "Blocked", "Active", hooksForChild), 1);
        _nodes.SetValue(Node("fixture-grandchild", "fixture-child", "Fixture grandchild", "Idle", "Completed", hooksForGrandchild), 2);
        Set(_model, "Nodes", _nodes);
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
        var mediumWidths = new List<double>();
        var largeWidths = new List<double>();
        foreach (var cell in _cells)
        {
            var root = cell.Root!;
            var labels = Descendants<XamlText>(root).ToArray();
            var project = labels.Single(text => text.Text == cell.Project);
            var projectPoint = project.TransformToVisual(root).TransformPoint(new Point());
            Require(projectPoint.Y + project.ActualHeight <= 49, "two-line cell exceeds taskbar slot");
            Require(project.TextWrapping == TextWrapping.NoWrap &&
                    project.TextTrimming == TextTrimming.CharacterEllipsis,
                "project name does not use single-line character ellipsis");
            if (!cell.IsLarge)
            {
                var status = labels.Single(text => text.Text == cell.Status);
                var statusPoint = status.TransformToVisual(root).TransformPoint(new Point());
                Require(projectPoint.Y >= statusPoint.Y + status.ActualHeight - 1,
                    "Medium cell labels are not two rows");
                if (cell.Project.Length > 16)
                    Require(project.IsTextTrimmed, "Medium project is not ellipsized in finite column");
                mediumWidths.Add(root.ActualWidth);
                continue;
            }

            var name = labels.Single(text => text.Text == cell.Name);
            var namePoint = name.TransformToVisual(root).TransformPoint(new Point());
            Require(namePoint.Y >= projectPoint.Y + project.ActualHeight - 1,
                "Large session title is not on the second row");
            Require(namePoint.Y + name.ActualHeight <= 49, "Large cell exceeds taskbar slot");
            Require(project.HorizontalAlignment == HorizontalAlignment.Center &&
                    project.TextAlignment == TextAlignment.Center,
                "Large project name does not use centered layout and text alignment");
            Require(name.HorizontalAlignment == HorizontalAlignment.Center &&
                    name.TextAlignment == TextAlignment.Center,
                "Large session title does not use centered layout and text alignment");
            Require(name.TextWrapping == TextWrapping.NoWrap &&
                    name.TextTrimming == TextTrimming.CharacterEllipsis,
                "session name does not use single-line character ellipsis");
            var boundaries = labels.Where(text => text.Text is "[" or "]").ToArray();
            Require(boundaries.Length == (cell.HasProjectBoundaries ? 2 : 0),
                "Large project boundary markers do not match the available project width");
            if (cell.HasProjectBoundaries)
            {
                var left = boundaries.Single(text => text.Text == "[");
                var right = boundaries.Single(text => text.Text == "]");
                var leftPoint = left.TransformToVisual(root).TransformPoint(new Point());
                var rightPoint = right.TransformToVisual(root).TransformPoint(new Point());
                Require(leftPoint.X <= .5 && rightPoint.X + right.ActualWidth >= root.ActualWidth - .5,
                    "Large project boundary markers are not at the shell cell edges");
                Require(Math.Abs(leftPoint.Y - projectPoint.Y) <= 1,
                    "Large project boundary markers are not on the first row");
            }
            if (cell.Project.Length > 16)
                Require(project.IsTextTrimmed, "Large project is not ellipsized in its finite column");
            if (cell.Project == "Short")
                Require(!project.IsTextTrimmed, "Short Large project was unexpectedly ellipsized");
            if (cell.Name == "Short session")
                Require(!name.IsTextTrimmed, "Short Large session title was unexpectedly ellipsized");
            else
                Require(name.IsTextTrimmed, "Large session title is not ellipsized in its finite row");
            switch (cell.Status)
            {
                case "Idle":
                    Require(Descendants<Ellipse>(root).Count() == 1 &&
                            !labels.Any(text => text.Text is "!" or "?"),
                        "Idle Large status icon is not a hollow circle");
                    break;
                case "Working":
                    Require(Descendants<ProgressRing>(root).Single().IsActive,
                        "Working Large status icon is not an active progress ring");
                    break;
                case "Blocked":
                    Require(Descendants<Ellipse>(root).Count() == 1 &&
                            labels.Any(text => text.Text == "!"),
                        "Blocked Large status icon is not an exclamation circle");
                    break;
                case "Unknown":
                    Require(Descendants<Ellipse>(root).Count() == 1 &&
                            labels.Any(text => text.Text == "?"),
                        "Unknown Large status icon is not a question-mark circle");
                    break;
            }
            Require(Descendants<FrameworkElement>(root).Any(element =>
                    AutomationProperties.GetName(element) == $"{cell.Name}, {cell.Status}, {cell.Project}"),
                "Large cell accessibility name changed");
            largeWidths.Add(root.ActualWidth);
        }
        Require(mediumWidths.Max() - mediumWidths.Min() < 0.5,
            "Medium client/status variants have different widths");
        Require(Math.Abs(mediumWidths[0] / 4 - Math.Round(mediumWidths[0] / 4)) < .01,
            "Medium width not rounded to 4 DIP");
        if (largeWidths.Count > 0)
        {
            Require(largeWidths.Max() - largeWidths.Min() < .5,
                "Large status variants have different widths");
            Require(Math.Abs(largeWidths[0] / mediumWidths[0] - 1.6) < .01,
                "Large width is not 1.6 times Medium width");
        }
    }

    private IEnumerable<XamlText> Texts() => Descendants<XamlText>(_billboard!.Window!.NativeWindow.Content);
    private IEnumerable<XamlButton> Buttons() => Descendants<XamlButton>(_billboard!.Window!.NativeWindow.Content);
    private void Invoke(string name)
    {
        var button = Buttons().Single(button => AutomationProperties.GetName(button) == name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private void TapRow(string rowName)
    {
        var automationId = rowName switch
        {
            "Fixture main" => "Active · Idle",
            "Fixture child" => "Active · Blocked",
            _ => "Completed · Idle",
        };
        var buttons = Descendants<XamlButton>(_billboard!.Window!.NativeWindow.Content)
            .Where(button => AutomationProperties.GetName(button) == automationId)
            .ToArray();
        if (buttons.Length != 1)
            throw new InvalidOperationException(
                $"expected one tree row named '{automationId}', found {buttons.Length}");
        // The row is a Button, so the UIAutomation Invoke pattern drives the
        // same click path a real pointer tap reaches.
        ((IInvokeProvider)new ButtonAutomationPeer(buttons[0])
            .GetPattern(PatternInterface.Invoke)).Invoke();
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
