using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pagurian;
using static Microsoft.UI.Reactor.Factories;
using XamlBorder = Microsoft.UI.Xaml.Controls.Border;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    var harness = new NavigationSmokeHarness();
    AppDomain.CurrentDomain.UnhandledException += (_, args) => harness.Fail($"Unhandled: {args.ExceptionObject}");
    Application.Current.UnhandledException += (_, args) => harness.Fail($"XAML: {args.Exception}");
    // WinUI can end its dispatcher when the final HWND closes even with
    // Reactor's Explicit policy. Keep an unshown test-only owner alive until
    // post-close timer/subscription assertions finish.
    harness.LifetimeWindow = ReactorApp.OpenWindow(new WindowSpec
    {
        Title = "Navigation diagnostics hidden lifetime owner",
        Width = 1,
        Height = 1,
        Style = WindowStyle.None,
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (-32000, -32000),
    }, () => new NavigationLifetimeView());
    harness.LifetimeWindow.Hide();
    harness.Window = ReactorApp.OpenWindow(new WindowSpec
    {
        Title = "Pagurian navigation diagnostic smoke test",
        Width = 360,
        Height = 180,
        Style = WindowStyle.ToolWindow,
        Backdrop = BackdropChoice.Of(BackdropKind.None),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = true,
        ResizeMode = WindowResizeMode.NoResize,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (24, 24),
    }, () => new NavigationSmokeView(harness));
    harness.Window.Closed += (_, _) => harness.Diagnostics.WindowClosed("smoke-window-event");
    harness.Window.Show();
    harness.Start();
});

internal abstract record SmokeRoute
{
    internal sealed record Modules : SmokeRoute;
    internal sealed record Configuration(string Id) : SmokeRoute;
}

internal sealed class NavigationLifetimeView : Component
{
    public override Element Render() => Border(null);
}

internal sealed class NavigationSmokeView(NavigationSmokeHarness harness) : Component
{
    public override Element Render()
    {
        var navigation = UseNavigation<SmokeRoute>(new SmokeRoute.Modules());
        var (shells, setShells) = UseState(true);
        harness.Navigation = navigation;
        harness.SetShells = setShells;
        UseEffect(() =>
        {
            void OnNavigated(NavigationEventArgs<SmokeRoute> args) => harness.Diagnostics.RouteChanged(
                NavigationSmokeHarness.RouteName(args.PreviousRoute), NavigationSmokeHarness.RouteName(args.Route), args.Mode.ToString());
            navigation.Navigated += OnNavigated;
            return () => navigation.Navigated -= OnNavigated;
        }, navigation);
        UseEffect(() => harness.Diagnostics.RenderObserved(shells ? SettingsPage.Shells : SettingsPage.General,
            NavigationSmokeHarness.RouteName(navigation.CurrentRoute), reduceMotion: false), shells, navigation.CurrentRoute);

        Element Page(SmokeRoute route) => FlexColumn(Body(NavigationSmokeHarness.RouteName(route)))
            .OnMountAdd(element => harness.Diagnostics.MountPage(element, NavigationSmokeHarness.RouteName(route)))
            .OnUnmountAdd(harness.Diagnostics.UnmountCallback);

        var host = (NavigationHost(navigation, Page) with
        {
            CacheMode = NavigationCacheMode.Disabled,
            Transition = NavigationTransition.Spring(),
        })
            .OnMountAdd(harness.MountHostCallback)
            .OnUnmountAdd(harness.UnmountHostCallback);

        return Border(shells ? host : Body("General fixture page")).Padding(12);
    }
}

internal sealed class NavigationSmokeHarness
{
    internal ShellNavigationDiagnostics Diagnostics { get; } = new();
    internal NavigationHandle<SmokeRoute>? Navigation { get; set; }
    internal Action<bool>? SetShells { get; set; }
    internal XamlGrid? Host { get; set; }
    internal ReactorWindow? Window { get; set; }
    internal ReactorWindow? LifetimeWindow { get; set; }
    private readonly DispatcherQueueTimer timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
    private readonly Stopwatch elapsed = new();
    private readonly Queue<(int AtMs, Action Action)> steps = new();
    private XamlBorder? injectedPage;
    private XamlGrid? injectedHost;
    private bool finished;

    internal Action<FrameworkElement> MountHostCallback { get; }
    internal Action<FrameworkElement> UnmountHostCallback { get; }

    internal NavigationSmokeHarness()
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

    internal static string RouteName(SmokeRoute route) => route is SmokeRoute.Configuration configuration
        ? ShellNavigationDiagnostics.ConfigurationRoute(configuration.Id) : "Modules";

    internal void Start()
    {
        steps.Enqueue((200, () => Select("0001")));
        steps.Enqueue((240, () => Select("0002")));
        steps.Enqueue((280, () => Select("0003")));
        steps.Enqueue((320, () => Select("0003")));
        steps.Enqueue((2600, () => CheckSettled("Configuration(0003)")));
        steps.Enqueue((2700, () =>
        {
            var navigation = Navigation ?? throw new InvalidOperationException("Navigation unavailable.");
            Diagnostics.Request("Back", RouteName(navigation.CurrentRoute), "Modules", "smoke-back");
            Require(navigation.GoBack(), "Back must return directly to Modules.");
        }));
        steps.Enqueue((4900, () => CheckSettled("Modules")));
        steps.Enqueue((5000, () =>
        {
            injectedHost = Host ?? throw new InvalidOperationException("Host unavailable.");
            injectedPage = new XamlBorder { Width = 1, Height = 1 };
            Diagnostics.MountPage(injectedPage, "Configuration(9999)");
            injectedHost.Children.Add(injectedPage);
            Diagnostics.Request("Smoke", "Modules", "Modules", "injected-extra-page", ignored: true);
        }));
        steps.Enqueue((7300, () =>
        {
            var count = injectedHost!.Children.Count;
            Require(PagurianLog.Lines.Any(line => line.Contains($"suspected-residue expected=Modules") &&
                line.Contains($"children={count} ")), "A deliberately retained page must produce a matching residue warning.");
            Diagnostics.Unmount(injectedPage!);
            injectedHost.Children.Remove(injectedPage);
            injectedPage = null;
            injectedHost = null;
            SetShells!(false);
        }));
        steps.Enqueue((7550, () =>
        {
            Require(Host is null, "Switching to General must unmount the navigation host.");
            SetShells!(true);
        }));
        steps.Enqueue((7850, () =>
        {
            Require(Host?.IsLoaded == true, "Returning to Shells must remount a loaded host.");
            Require(PagurianLog.Lines.Count(line => line.Contains("reactor-mount id=host-")) >= 2,
                "Host mount records must distinguish remounts.");
            Require(PagurianLog.Lines.Any(line => line.Contains("page-changed from=Shells to=General")),
                "Page switch must be observable.");
            Diagnostics.Request("Smoke", "Modules", "Modules", "pending-sample-before-close", ignored: true);
            Window!.Close();
        }));
        steps.Enqueue((10300, VerifyClosedAndFinish));
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.IsRepeating = true;
        timer.Tick += OnTick;
        elapsed.Start();
        timer.Start();
    }

    private void Select(string id)
    {
        var navigation = Navigation ?? throw new InvalidOperationException("Navigation unavailable.");
        string from = RouteName(navigation.CurrentRoute);
        string to = ShellNavigationDiagnostics.ConfigurationRoute(id);
        bool duplicate = navigation.CurrentRoute is SmokeRoute.Configuration current && current.Id == id;
        bool replace = navigation.CurrentRoute is SmokeRoute.Configuration;
        Diagnostics.Request(replace ? "Replace" : "Navigate", from, to, "smoke-select", ignored: duplicate);
        if (duplicate)
            return;
        if (replace)
            navigation.Replace(new SmokeRoute.Configuration(id));
        else
            navigation.Navigate(new SmokeRoute.Configuration(id));
    }

    private void CheckSettled(string expected)
    {
        Require(Host?.IsLoaded == true, "The host must be loaded for meaningful snapshots.");
        Require(RouteName(Navigation!.CurrentRoute) == expected, "The navigation stack must have the expected route.");
        var snapshot = PagurianLog.Lines.LastOrDefault(line => line.Contains("snapshot phase=after-2000ms"));
        Require(snapshot is not null && snapshot.Contains($"expected={expected} ") &&
            snapshot.Contains($"children={Host!.Children.Count} "), "Settled snapshot must match the actual host child count.");
        if (Host!.Children.Count != 1)
            Require(PagurianLog.Lines.Any(line => line.Contains($"suspected-residue expected={expected} ")),
                "A real multi-page residue must be reported, not silently passed.");
        Console.WriteLine($"Settled {expected}: actual children={Host.Children.Count} (diagnostics matched).");
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
        Require(PagurianLog.Lines.Any(line => line.Contains("ignored=True") && line.Contains("reason=smoke-select")),
            "Duplicate selection must be recorded as ignored.");
        Require(PagurianLog.Lines.Any(line => line.Contains("reactor-unmount id=page-")), "Page teardown must be recorded.");
        Require(PagurianLog.Lines.Any(line => line.Contains("xaml-loaded id=page-")), "Page Loaded events must be recorded.");
        Require(PagurianLog.Lines.Any(line => line.Contains("session-end")), "Window close must end the diagnostic session.");
        Require(!PagurianLog.Lines.Any(line => line.Contains("probe-error")), "No diagnostic probe may fail.");
        int closed = PagurianLog.Lines.FindIndex(line => line.Contains("window-closed"));
        Require(closed >= 0 && !PagurianLog.Lines.Skip(closed + 1).Any(line => line.Contains("snapshot phase=")),
            "Queued/timed snapshots must stop after close.");
        int before = PagurianLog.Lines.Count;
        Diagnostics.Record("must-not-log-after-complete");
        Require(PagurianLog.Lines.Count == before, "Completed sessions must ignore later writes.");
        Finish(0, "Live navigation diagnostics passed: rapid replacement, ignored selection, Back, residue detection, remount and close cleanup.");
    }

    internal void Fail(string message) => Finish(1, "Navigation diagnostics smoke failure: " + message);

    private void Finish(int code, string message)
    {
        if (finished)
            return;
        finished = true;
        timer.Stop();
        timer.Tick -= OnTick;
        Console.WriteLine(message);
        if (code != 0)
            foreach (var line in PagurianLog.Lines)
                Console.WriteLine(line);
        ShellNavigationDiagnostics.CompleteAllForExit();
        ReactorApp.Exit(code);
        Environment.Exit(code);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

namespace Pagurian
{
    // Only these host-neutral definitions are substituted. The observer under
    // test is linked directly from the application, not copied or mocked.
    internal enum SettingsPage { Shells, General }

    internal static class PagurianLog
    {
        internal static List<string> Lines { get; } = [];
        internal static void Navigation(string sessionId, string message, string level = "INFO") =>
            Lines.Add($"[{level}] [shell-navigation] session={sessionId} {message}");
        internal static Task FlushNavigationAsync() => Task.CompletedTask;
    }
}
