using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pagurian;

// Passive probes on the existing controls: no wrapper, key, navigation or
// Composition Visual changes. All XAML inspection stays on the UI thread.
internal sealed class ShellNavigationDiagnostics
{
    private sealed class Node(long id, string role, string route)
    {
        public long Id { get; } = id;
        public string Role { get; } = role;
        public string Route { get; } = route;
        public bool Mounted { get; set; }
        public bool Subscribed { get; set; }
    }

    private static readonly List<WeakReference<ShellNavigationDiagnostics>> Sessions = [];
    private static readonly int[] SampleTimesMs = [250, 1000, 2000];
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly ConditionalWeakTable<FrameworkElement, Node> _nodes = new();
    private readonly List<WeakReference<FrameworkElement>> _observed = [];
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;
    private WeakReference<Grid>? _host;
    private long _nextNode;
    private long _request;
    private long _sampleStarted;
    private int _sampleIndex;
    private bool _snapshotQueued;
    private bool _closing;
    private bool _completed;
    private string _route = "Modules";
    private string? _renderedRoute;
    private SettingsPage? _page;
    private bool? _reduceMotion;

    // Reactor compares unmount actions by reference even during shallow
    // equality. Reuse these delegates so observation does not force updates.
    internal Action<FrameworkElement> UnmountCallback { get; }
    internal Action<FrameworkElement> MountHostCallback { get; }

    public ShellNavigationDiagnostics()
    {
        UnmountCallback = Unmount;
        MountHostCallback = MountHost;
        _timer = _dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTimer;
        Sessions.RemoveAll(reference => !reference.TryGetTarget(out var session) || session._completed);
        Sessions.Add(new(this));
        var reactorVersion = typeof(Microsoft.UI.Reactor.Core.Component).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        Write($"session-start reactor={reactorVersion ?? "unknown"}");
    }

    internal static string ConfigurationRoute(string id) =>
        $"Configuration({(id.Length == 4 && id.All(char.IsAsciiDigit) ? id : "invalid-id")})";

    internal void Record(string message) => Write(message);

    internal void Request(string operation, string from, string to, string reason, bool ignored = false) =>
        Safely("request", () =>
        {
            _request++;
            Write($"request operation={operation} from={from} to={to} reason={reason} ignored={ignored}");
            ScheduleSnapshots();
        });

    // The public Navigated event reports an accepted stack mutation, not the
    // completion of its animation. Keep that distinction explicit in the log.
    internal void RouteChanged(string from, string to, string mode) => Safely("route-change", () =>
    {
        _route = to;
        Write($"route-changed from={from} to={to} mode={mode}");
    });

    internal void RenderObserved(SettingsPage page, string route, bool reduceMotion) =>
        Safely("render", () =>
        {
            if (_page != page)
            {
                Write($"page-changed from={_page?.ToString() ?? "none"} to={page}");
                _page = page;
                if (page != SettingsPage.Shells)
                    _timer.Stop();
            }
            if (_renderedRoute != route || _reduceMotion != reduceMotion)
            {
                Write($"render-observed route={route} transition={(reduceMotion ? "None" : "SmoothOverlay")}");
                _renderedRoute = route;
                _reduceMotion = reduceMotion;
            }
        });

    internal void MountHost(FrameworkElement element) => Safely("host-mount", () =>
    {
        Register(element, "host", "none");
        if (element is Grid grid)
        {
            _host = new(grid);
            ScheduleSnapshots();
        }
        else
            Write($"probe-unexpected-host type={element.GetType().Name}", "WARN");
    });

    internal void MountPage(FrameworkElement element, string route) =>
        Safely("page-mount", () => Register(element, "page", route));

    private void Register(FrameworkElement element, string role, string route)
    {
        // Mount callbacks precede Ref assignment and insertion into the parent.
        // A missing parent here is normal; only delayed snapshots judge it.
        var node = _nodes.GetValue(element, _ => new Node(++_nextNode, role, route));
        node.Mounted = true;
        if (!node.Subscribed)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            node.Subscribed = true;
            _observed.RemoveAll(reference => !reference.TryGetTarget(out var target) ||
                !_nodes.TryGetValue(target, out var existing) || !existing.Subscribed);
            _observed.Add(new(element));
        }
        Write($"reactor-mount {Describe(element)}");
    }

    internal void Unmount(FrameworkElement element) => Safely("unmount", () =>
    {
        if (!_nodes.TryGetValue(element, out var node))
            return;
        node.Mounted = false;
        Write($"reactor-unmount {Describe(element)}");
        if (_host?.TryGetTarget(out var host) == true && ReferenceEquals(host, element))
        {
            _timer.Stop();
            _host = null;
        }

        // The host unmount callback runs before its child teardown. Also allow
        // XAML's queued Unloaded event to arrive before detaching our handlers.
        var weak = new WeakReference<FrameworkElement>(element);
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => Safely("detach", () =>
            {
                if (weak.TryGetTarget(out var target) &&
                    _nodes.TryGetValue(target, out var current) && !current.Mounted)
                    Detach(target, current);
            })))
            Detach(element, node);
    });

    private void OnLoaded(object sender, RoutedEventArgs args) => Safely("loaded", () =>
    {
        if (sender is FrameworkElement element)
            Write($"xaml-loaded {Describe(element)}");
    });

    private void OnUnloaded(object sender, RoutedEventArgs args) => Safely("unloaded", () =>
    {
        if (sender is FrameworkElement element)
        {
            Write($"xaml-unloaded {Describe(element)}");
            if (_nodes.TryGetValue(element, out var node) && !node.Mounted)
                Detach(element, node);
        }
    });

    private void Detach(FrameworkElement element, Node node)
    {
        if (!node.Subscribed)
            return;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        node.Subscribed = false;
    }

    private void ScheduleSnapshots()
    {
        if (_closing)
            return;
        _timer.Stop();
        _sampleStarted = Stopwatch.GetTimestamp();
        _sampleIndex = 0;
        if (!_snapshotQueued)
        {
            _snapshotQueued = true;
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    _snapshotQueued = false;
                    Safely("queued-snapshot", () =>
                    {
                        if (!_closing)
                            Snapshot("queued", false);
                    });
                }))
                _snapshotQueued = false;
        }
        _timer.Interval = TimeSpan.FromMilliseconds(SampleTimesMs[0]);
        _timer.Start();
    }

    private void OnTimer(DispatcherQueueTimer sender, object args) => Safely("timer", () =>
    {
        if (_closing || _sampleIndex >= SampleTimesMs.Length)
            return;
        var elapsed = Stopwatch.GetElapsedTime(_sampleStarted).TotalMilliseconds;
        var due = SampleTimesMs[_sampleIndex];
        if (elapsed >= due)
        {
            Snapshot($"after-{due}ms", _sampleIndex == SampleTimesMs.Length - 1);
            _sampleIndex++;
        }
        if (_sampleIndex < SampleTimesMs.Length)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, SampleTimesMs[_sampleIndex] - elapsed));
            _timer.Start();
        }
    });

    private void Snapshot(string phase, bool settled)
    {
        if (_host is null || !_host.TryGetTarget(out var host))
        {
            Write($"snapshot phase={phase} expected={_route} host=none");
            return;
        }
        var children = host.Children.ToArray();
        var descriptions = string.Join(" | ", children.Select((child, index) =>
        {
            var page = FindPageNode(child);
            return $"slot={index} {Describe(child)} pageRoute={page?.Route ?? "none"} " +
                $"pageMounted={page?.Mounted} parentIsHost={ReferenceEquals(VisualTreeHelper.GetParent(child), host)}";
        }));
        Write($"snapshot phase={phase} expected={_route} host={Identity(host)} children={children.Length} [{descriptions}]");
        var pages = children
            .Select(child => FindPageNode(child))
            .Where(node => node?.Mounted == true)
            .ToArray();
        var expectedPageCount = _route == "Modules" ? 1 : 2;
        var mismatch = children.Length != expectedPageCount ||
            pages.Length != expectedPageCount ||
            pages.Count(node => node!.Route == "Modules") != 1 ||
            (_route != "Modules" && pages.Count(node => node!.Route == _route) != 1) ||
            pages.Any(node => node!.Route != "Modules" && node.Route != _route);
        // Extra configuration layers are expected only while their independent
        // exit animations are active. At 2s they indicate incomplete cleanup.
        if (settled && _page == SettingsPage.Shells && host.IsLoaded && mismatch)
        {
            Write($"suspected-residue expected={_route} host={Identity(host)} children={children.Length} tree={TreeSummary(host)}", "WARN");
        }
    }

    private Node? FindPageNode(DependencyObject root, int depth = 0)
    {
        if (root is FrameworkElement element &&
            _nodes.TryGetValue(element, out var node) &&
            node.Role == "page")
        {
            return node;
        }
        if (depth >= 3)
            return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindPageNode(VisualTreeHelper.GetChild(root, index), depth + 1) is { } child)
                return child;
        }
        return null;
    }

    private string Identity(DependencyObject? element)
    {
        if (element is null)
            return "none";
        return element is FrameworkElement fe && _nodes.TryGetValue(fe, out var node)
            ? $"{node.Role}-{node.Id}"
            : $"untracked:{element.GetType().Name}";
    }

    private string Describe(UIElement element)
    {
        var identity = Identity(element);
        if (element is not FrameworkElement fe)
            return $"id={identity} type={element.GetType().Name}";
        _nodes.TryGetValue(fe, out var node);
        // xamlOpacity is NOT the current compositor-animated opacity. Calling
        // GetElementVisual here would itself alter Reactor/WinUI behavior.
        return FormattableString.Invariant(
            $"id={identity} type={fe.GetType().Name} route={node?.Route ?? "unknown"} mounted={node?.Mounted} loaded={fe.IsLoaded} visibility={fe.Visibility} xamlOpacity={fe.Opacity:0.###} size={fe.ActualWidth:0.##}x{fe.ActualHeight:0.##} xamlRoot={fe.XamlRoot is not null} parent={Identity(VisualTreeHelper.GetParent(fe))}");
    }

    private string TreeSummary(DependencyObject root)
    {
        var result = new List<string>();
        void Visit(DependencyObject current, int depth)
        {
            if (result.Count >= 32)
                return;
            var count = VisualTreeHelper.GetChildrenCount(current);
            result.Add($"depth={depth}:{Identity(current)}:{current.GetType().Name}:children={count}");
            if (depth < 2)
                for (var i = 0; i < count && result.Count < 32; i++)
                    Visit(VisualTreeHelper.GetChild(current, i), depth + 1);
        }
        Visit(root, 0);
        return string.Join(";", result);
    }

    internal void WindowClosed(string reason) => Safely("window-close", () =>
    {
        if (_closing)
            return;
        Write($"window-closed reason={reason}");
        _closing = true;
        _timer.Stop();
        // Receive synchronous descendant teardown first, then release probes.
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Complete))
            Complete();
    });

    private void Complete() => Safely("complete", () =>
    {
        _timer.Stop();
        _timer.Tick -= OnTimer;
        var stillMounted = 0;
        foreach (var reference in _observed)
        {
            if (reference.TryGetTarget(out var element) && _nodes.TryGetValue(element, out var node))
            {
                if (node.Mounted)
                    stillMounted++;
                Detach(element, node);
            }
        }
        Write($"session-end stillMarkedMounted={stillMounted}");
        _completed = true;
        _host = null;
        _observed.Clear();
        _nodes.Clear();
        _ = PagurianLog.FlushNavigationAsync();
    });

    internal static void CompleteAllForExit()
    {
        foreach (var reference in Sessions)
            if (reference.TryGetTarget(out var session))
            {
                session.WindowClosed("process-exit");
                session.Complete();
            }
        Sessions.Clear();
    }

    private void Safely(string operation, Action action)
    {
        if (_completed)
            return;
        try { action(); }
        catch (Exception ex)
        {
            // Exception messages can contain module/configuration data. Log
            // only the diagnostic failure's type and HRESULT.
            Write($"probe-error operation={operation} type={ex.GetType().Name} hresult={ex.HResult:X8}", "WARN");
        }
    }

    private void Write(string message, string level = "INFO")
    {
        if (!_completed)
            PagurianLog.Navigation(_sessionId,
                $"nav={_request} elapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} {message}", level);
    }
}
