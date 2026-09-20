using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Pagurian;
using static Microsoft.UI.Reactor.Factories;
using XamlBorder = Microsoft.UI.Xaml.Controls.Border;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    var harness = new TransitionSmokeHarness();
    AppDomain.CurrentDomain.UnhandledException += (_, args) => harness.Fail($"Unhandled: {args.ExceptionObject}");
    Application.Current.UnhandledException += (_, args) => harness.Fail($"XAML: {args.Exception}");
    harness.LifetimeWindow = ReactorApp.OpenWindow(new WindowSpec
    {
        Title = "Configuration transition hidden lifetime owner",
        Width = 1,
        Height = 1,
        Style = WindowStyle.None,
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (-32000, -32000),
    }, () => new TransitionLifetimeView());
    harness.LifetimeWindow.Hide();
    harness.Window = ReactorApp.OpenWindow(new WindowSpec
    {
        Title = "Pagurian configuration transition smoke test",
        Width = 520,
        Height = 260,
        Style = WindowStyle.ToolWindow,
        Backdrop = BackdropChoice.Of(BackdropKind.None),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        ResizeMode = WindowResizeMode.NoResize,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (24, 24),
    }, () => new TransitionSmokeView(harness));
    harness.Window.Closed += (_, _) => harness.Diagnostics.WindowClosed("smoke-window-event");
    harness.Window.Show();
    harness.Start();
});

internal sealed class TransitionLifetimeView : Component
{
    public override Element Render() => Border(null);
}

internal sealed class TransitionSmokeView(TransitionSmokeHarness harness) : Component
{
    public override Element Render()
    {
        var (_, invalidate) = UseReducer(0);
        var transitions = UseMemo(
            () => new ShellConfigurationTransitionState(
                () => invalidate(version => version + 1),
                harness.Diagnostics.Record),
            Array.Empty<object>());
        var (shells, setShells) = UseState(true);
        UseEffect(() => () => transitions.Dispose(), transitions);
        harness.Transitions = transitions;
        harness.SetShells = visible =>
        {
            transitions.SetVisible(visible);
            setShells(visible);
        };

        if (!shells)
            return Border(Body("General fixture page"));

        var layers = new List<Element?>
        {
            ScrollView(Border(Heading("Modules")))
                .Background(Theme.SolidBackground)
                .OnMountAdd(harness.MountModules)
                .OnUnmountAdd(harness.UnmountModules)
                .WithKey("modules"),
        };
        foreach (var visit in transitions.Visits)
        {
            var captured = visit;
            var fill = captured.Entry.Id switch
            {
                "0001" => Theme.SystemCritical,
                "0002" => Theme.Accent,
                "0003" => Theme.SystemSuccess,
                _ => Theme.SystemCaution,
            };
            layers.Add(
                Component<ShellConfigurationTransitionLayer,
                        ShellConfigurationTransitionLayerProps>(
                        new ShellConfigurationTransitionLayerProps(
                            captured,
                            transitions,
                            harness.Diagnostics,
                            Border(Heading(
                                    $"Configuration {captured.Entry.Id} — visit {captured.VisitId}"))
                                .Background(fill),
                            HighContrast: false))
                    .WithKey($"visit:{captured.VisitId}"));
        }

        var host = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                layers.ToArray())
            .OnSizeChanged((sender, args) =>
            {
                transitions.SetExtent(args.NewSize.Width);
                if (sender is FrameworkElement element)
                {
                    element.Clip = new RectangleGeometry
                    {
                        Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height),
                    };
                }
            })
            .OnMountAdd(harness.MountHostCallback)
            .OnUnmountAdd(harness.UnmountHostCallback);
        return Border(host).Padding(12);
    }
}

internal sealed class TransitionSmokeHarness
{
    internal ShellNavigationDiagnostics Diagnostics { get; } = new();
    internal ShellConfigurationTransitionState? Transitions { get; set; }
    internal Action<bool>? SetShells { get; set; }
    internal XamlGrid? Host { get; set; }
    internal ReactorWindow? Window { get; set; }
    internal ReactorWindow? LifetimeWindow { get; set; }
    internal FrameworkElement? ModuleElement { get; set; }
    private readonly DispatcherQueueTimer timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
    private readonly Stopwatch elapsed = new();
    private readonly Queue<(int AtMs, Action Action)> steps = new();
    private XamlBorder? injectedPage;
    private XamlGrid? injectedHost;
    private long firstVisit;
    private FrameworkElement? initialModule;
    private int moduleMountCount;
    private bool finished;

    internal Action<FrameworkElement> MountHostCallback { get; }
    internal Action<FrameworkElement> UnmountHostCallback { get; }

    internal TransitionSmokeHarness()
    {
        MountHostCallback = element =>
        {
            Host = element as XamlGrid;
            Diagnostics.MountHostCallback(element);
        };
        UnmountHostCallback = element =>
        {
            Diagnostics.UnmountCallback(element);
            if (ReferenceEquals(Host, element))
                Host = null;
        };
    }

    internal void Start()
    {
        steps.Enqueue((250, () => Select("0001")));
        steps.Enqueue((340, CaptureBeforeReverse));
        steps.Enqueue((350, () => Select("0002")));
        steps.Enqueue((370, VerifyImmediateReverse));
        steps.Enqueue((410, () => Select("0003")));
        steps.Enqueue((470, () => Select("0001")));
        steps.Enqueue((500, () => Select("0001")));
        steps.Enqueue((850, () => CheckSettled("0001", expectedVisitIdGreaterThan: firstVisit)));
        steps.Enqueue((1700, () => CheckSettled("0001", expectedVisitIdGreaterThan: firstVisit)));
        steps.Enqueue((1800, Close));
        steps.Enqueue((2200, CheckModulesSettled));
        steps.Enqueue((2300, () => Select("0002")));
        steps.Enqueue((2400, () => SetShells!(false)));
        steps.Enqueue((2500, () => SetShells!(true)));
        steps.Enqueue((2650, () => CheckSettled("0002")));
        steps.Enqueue((2750, () =>
        {
            Transitions!.SetReducedMotion(true);
            Select("0003");
        }));
        steps.Enqueue((2820, () => CheckSettled("0003")));
        steps.Enqueue((2900, InjectResidue));
        steps.Enqueue((5050, VerifyResidueAndClose));
        steps.Enqueue((7400, VerifyClosedAndFinish));
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.IsRepeating = true;
        timer.Tick += OnTick;
        elapsed.Start();
        timer.Start();
    }

    internal void MountModules(FrameworkElement element)
    {
        ModuleElement = element;
        initialModule ??= element;
        moduleMountCount++;
        Diagnostics.MountPage(element, "Modules");
    }

    internal void UnmountModules(FrameworkElement element)
    {
        Diagnostics.Unmount(element);
        if (ReferenceEquals(ModuleElement, element))
            ModuleElement = null;
    }

    private void Select(string id)
    {
        var transitions = Transitions ?? throw new InvalidOperationException("Transitions unavailable.");
        var from = transitions.SelectedInstanceId is { } selected
            ? ShellNavigationDiagnostics.ConfigurationRoute(selected)
            : "Modules";
        var to = ShellNavigationDiagnostics.ConfigurationRoute(id);
        var opened = transitions.Open(new TrayConfig.Entry("fixture", id, null));
        Diagnostics.Request("Open", from, to, "smoke-select", ignored: !opened);
        if (opened)
            Diagnostics.RouteChanged(from, to, "Overlay");
        Diagnostics.RenderObserved(SettingsPage.Shells, to, reduceMotion: false);
        if (firstVisit == 0)
            firstVisit = transitions.SelectedVisitId ?? 0;
    }

    private void Close()
    {
        var transitions = Transitions!;
        var from = transitions.SelectedInstanceId is { } selected
            ? ShellNavigationDiagnostics.ConfigurationRoute(selected)
            : "Modules";
        var closed = transitions.Close();
        Diagnostics.Request("Close", from, "Modules", "smoke-close", ignored: !closed);
        if (closed)
            Diagnostics.RouteChanged(from, "Modules", "Overlay");
        Diagnostics.RenderObserved(SettingsPage.Shells, "Modules", reduceMotion: false);
    }

    private void CaptureBeforeReverse()
    {
        var visit = Transitions!.Visits.Single(candidate => candidate.VisitId == firstVisit);
        Console.WriteLine($"Before reverse: x={CurrentX(visit):0.##}");
        Require(visit.Phase == ShellConfigurationVisitPhase.Entering,
            "The first page must still be entering before reversal.");
    }

    private void VerifyImmediateReverse()
    {
        var visit = Transitions!.Visits.Single(candidate => candidate.VisitId == firstVisit);
        var after = CurrentX(visit);
        Console.WriteLine($"After reverse: x={after:0.##}");
        Require(visit.Phase == ShellConfigurationVisitPhase.Exiting,
            "The invalidated entering page must immediately enter Exiting.");
        Require(visit.Element is Border
            {
                IsHitTestVisible: false,
                Child: ScrollView { IsEnabled: true },
            },
            "An outgoing configuration must block input without entering a translucent disabled state.");
        Require(((XamlBorder)visit.Element!).Background is SolidColorBrush { Color.A: 255 },
            "A transitioning configuration must keep an opaque background.");
        Require(after > 1 && after < Host!.ActualWidth - 1,
            $"Reversal must retain an intermediate visible position, x={after:0.##}.");
    }

    private void CheckSettled(string id, long expectedVisitIdGreaterThan = 0)
    {
        var transitions = Transitions!;
        Require(transitions.Visits.Count == 1, $"Expected one configuration visit, got {transitions.Visits.Count}.");
        var visit = transitions.Visits[0];
        Require(visit.Entry.Id == id && visit.Phase == ShellConfigurationVisitPhase.Active,
            $"Expected active {id}, got {visit.Entry.Id}/{visit.Phase}.");
        Require(visit.VisitId > expectedVisitIdGreaterThan,
            "A repeated Shell selection must create a new visit instead of reviving the old page.");
        var host = Host ?? throw new InvalidOperationException("Host unavailable.");
        Require(host.Children.Count == 2, "The settled host must contain fixed Modules plus one configuration.");
        Require(ModuleElement != null, "The fixed Modules layer must remain mounted.");
        Require(Math.Abs(ElementCompositionPreview.GetElementVisual(ModuleElement).Offset.X) < 0.01f,
            "The Modules layer must not move during configuration transitions.");
        var activeRoot = visit.Element as XamlBorder ??
            throw new InvalidOperationException(
                $"The active configuration root must be a Border, got {visit.Element?.GetType().Name ?? "null"}.");
        Require(activeRoot.Child is ScrollView activeScroll && activeScroll.IsEnabled,
            $"The active configuration ScrollView must be enabled, got {activeRoot.Child?.GetType().Name ?? "null"}/" +
            $"{(activeRoot.Child as ScrollView)?.IsEnabled}.");
        Require(activeRoot.Background is SolidColorBrush { Color.A: 255 },
            $"The active configuration background must be opaque, got {activeRoot.Background?.GetType().Name ?? "null"}/" +
            $"{(activeRoot.Background as SolidColorBrush)?.Color.A}.");
        var activeVisual = ElementCompositionPreview.GetElementVisual(visit.Element);
        Require(Math.Abs(activeVisual.Offset.X) < 0.01f && Math.Abs(activeVisual.Opacity - 1) < 0.001f,
            "The active configuration must not inherit stale composition state.");
        if (moduleMountCount == 1)
        {
            Require(ReferenceEquals(initialModule, ModuleElement),
                "The Modules layer must keep its native identity while configurations switch.");
        }
        var expected = ShellNavigationDiagnostics.ConfigurationRoute(id);
        var snapshot = PagurianLog.Lines.LastOrDefault(line => line.Contains("snapshot phase=after-"));
        Console.WriteLine(
            $"Settled {expected}: visit={visit.VisitId}, children={host.Children.Count}, latestSnapshot={snapshot}");
    }

    private void CheckModulesSettled()
    {
        Require(Transitions!.Visits.Count == 0, "All configuration pages must be removed after close.");
        Require(Host?.Children.Count == 1, "Modules must remain as the only settled child.");
    }

    private void InjectResidue()
    {
        Transitions!.SetReducedMotion(false);
        injectedHost = Host ?? throw new InvalidOperationException("Host unavailable.");
        injectedPage = new XamlBorder { Width = 1, Height = 1 };
        Diagnostics.MountPage(injectedPage, "Configuration(9999)");
        injectedHost.Children.Add(injectedPage);
        Diagnostics.Request("Smoke", "Configuration(0003)", "Configuration(0003)", "injected-extra-page", ignored: true);
    }

    private void VerifyResidueAndClose()
    {
        Require(PagurianLog.Lines.Any(line =>
                line.Contains("suspected-residue expected=Configuration(0003)") &&
                line.Contains($"children={injectedHost!.Children.Count} ")),
            "A deliberately retained page must produce a matching residue warning.");
        Diagnostics.Unmount(injectedPage!);
        injectedHost!.Children.Remove(injectedPage);
        injectedPage = null;
        injectedHost = null;
        Diagnostics.Request("Smoke", "Configuration(0003)", "Configuration(0003)", "pending-sample-before-close", ignored: true);
        Window!.Close();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (steps.TryPeek(out var step) && elapsed.ElapsedMilliseconds >= step.AtMs)
            {
                steps.Dequeue();
                step.Action();
            }
        }
        catch (Exception ex)
        {
            Fail(ex.ToString());
        }
    }

    private void VerifyClosedAndFinish()
    {
        Require(PagurianLog.Lines.Any(line => line.Contains("reactor-unmount id=page-")),
            "Page teardown must be recorded.");
        Require(PagurianLog.Lines.Any(line =>
                line.Contains("ignored=True") && line.Contains("reason=smoke-select")),
            "Selecting the already active Shell must be ignored.");
        Require(PagurianLog.Lines.Any(line => line.Contains("session-end")),
            "Window close must end the diagnostic session.");
        Require(!PagurianLog.Lines.Any(line => line.Contains("probe-error")),
            "No diagnostic probe may fail.");
        var closed = PagurianLog.Lines.FindIndex(line => line.Contains("window-closed"));
        Require(closed >= 0 && !PagurianLog.Lines.Skip(closed + 1).Any(line => line.Contains("snapshot phase=")),
            "Queued and timed snapshots must stop after close.");
        Finish(0,
            "Configuration transition passed: fixed Modules, mid-entry reversal, A-B-C-A isolation, late-window stability, close, remount, reduced motion and residue detection.");
    }

    private static float CurrentX(ShellConfigurationVisit visit)
    {
        return visit.Motion?.CurrentXForDiagnostics
            ?? throw new InvalidOperationException("Visit motion unavailable.");
    }

    internal void Fail(string message) => Finish(1, "Configuration transition smoke failure: " + message);

    private void Finish(int code, string message)
    {
        if (finished)
            return;
        finished = true;
        timer.Stop();
        timer.Tick -= OnTick;
        Console.WriteLine(message);
        try { Window?.Close(); } catch { }
        try { LifetimeWindow?.Close(); } catch { }
        ReactorApp.Exit(code);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

namespace Pagurian
{
    internal static class TrayConfig
    {
        public sealed record Entry(string ShellType, string Id, object? Settings);
    }

    internal static class PagurianLog
    {
        internal static List<string> Lines { get; } = [];

        internal static void Navigation(string sessionId, string message, string level = "INFO")
        {
            var line = $"[{level}] [shell-navigation] session={sessionId} {message}";
            Lines.Add(line);
            Console.WriteLine(line);
        }

        internal static Task FlushNavigationAsync() => Task.CompletedTask;
    }

    internal enum SettingsPage
    {
        Shells,
        General,
    }
}
