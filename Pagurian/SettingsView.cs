using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pagurian.Sdk;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Paged settings surface. Shell editing and host settings share one window;
// the Shell draft remains alive while the user moves between pages.
class SettingsView : Component
{
    private readonly ShellNavigationDiagnostics _navigationDiagnostics;

    public SettingsView(ShellNavigationDiagnostics navigationDiagnostics) =>
        _navigationDiagnostics = navigationDiagnostics;

    private static readonly ConditionalWeakTable<FrameworkElement, InstantTooltipBinding>
        InstantTooltipBindings = new();
    private static readonly HashSet<InstantTooltipBinding> MountedInstantTooltipBindings = [];
    private static readonly ConditionalWeakTable<FrameworkElement, ThresholdDragBinding>
        ThresholdDragBindings = new();
    private static int _instantTooltipSuppressionDepth;

    private const double StripPadX = 12;
    private const double ChipSize = 44;
    private const double ChipGap = 4;
    private const double ChipPitch = ChipSize + ChipGap;
    private const double EmptyTargetWidth = 160;
    private const double AutoScrollEdge = 48;
    private const double AutoScrollStep = 12;
    private const double DragThreshold = 0;
    private const double InsertionIndicatorWidth = 3;

    private sealed class InstantTooltipBinding
    {
        public InstantTooltipBinding(string text)
        {
            ToolTip = new ToolTip { Content = text };
            PointerEntered = OnPointerEntered;
            PointerExited = OnPointerExited;
            DragStarting = OnDragStarting;
            Opened = OnOpened;
        }

        public ToolTip ToolTip { get; }
        public bool IsActive { get; set; }
        public Microsoft.UI.Xaml.Input.PointerEventHandler PointerEntered { get; }
        public Microsoft.UI.Xaml.Input.PointerEventHandler PointerExited { get; }
        public Windows.Foundation.TypedEventHandler<UIElement, DragStartingEventArgs> DragStarting { get; }
        public RoutedEventHandler Opened { get; }

        private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
            SetOpen(sender, true);

        private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
            SetOpen(sender, false);

        private void OnDragStarting(UIElement sender, DragStartingEventArgs args) =>
            SetOpen(sender, false);

        private void OnOpened(object sender, RoutedEventArgs args)
        {
            if (InstantTooltipsSuppressed)
                ToolTip.IsOpen = false;
        }

        private void SetOpen(object sender, bool isOpen)
        {
            if (!IsActive ||
                sender is not FrameworkElement element ||
                !InstantTooltipBindings.TryGetValue(element, out var current) ||
                !ReferenceEquals(current, this))
            {
                return;
            }

            ToolTip.IsOpen = isOpen && !InstantTooltipsSuppressed;
        }

        public void Close() => ToolTip.IsOpen = false;
    }

    private sealed record ShellDragPayload(string? KindId, string? InstanceId)
    {
        public static ShellDragPayload ForKind(string kindId) => new(kindId, null);
        public static ShellDragPayload ForInstance(string instanceId) => new(null, instanceId);
    }

    private sealed class ThresholdDragBinding
    {
        private readonly FrameworkElement _element;
        private uint? _pointerId;
        private Point _startPoint;
        private bool _dragStarted;
        private bool _attached;

        public ThresholdDragBinding(FrameworkElement element)
        {
            _element = element;
        }

        public void Attach()
        {
            if (_attached)
                return;

            _attached = true;
            _element.CanDrag = false;
            _element.PointerPressed += OnPointerPressed;
            _element.PointerMoved += OnPointerMoved;
            _element.PointerReleased += OnPointerReleased;
            _element.PointerCanceled += OnPointerCanceled;
            _element.PointerCaptureLost += OnPointerCaptureLost;
        }

        public void Detach()
        {
            if (!_attached)
                return;

            _attached = false;
            _element.PointerPressed -= OnPointerPressed;
            _element.PointerMoved -= OnPointerMoved;
            _element.PointerReleased -= OnPointerReleased;
            _element.PointerCanceled -= OnPointerCanceled;
            _element.PointerCaptureLost -= OnPointerCaptureLost;
            Reset(releaseCapture: true);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            if (!_attached || _dragStarted)
                return;

            // Reactor enables CanDrag for its typed data pipeline. Disable its
            // built-in gesture threshold before movement starts; StartDragAsync
            // below still raises the same DragStarting/DropCompleted events.
            _element.CanDrag = false;
            var point = args.GetCurrentPoint(_element);
            var properties = point.Properties;
            var isPrimary = properties.IsLeftButtonPressed ||
                point.IsInContact &&
                !properties.IsRightButtonPressed &&
                !properties.IsMiddleButtonPressed;
            if (!isPrimary)
                return;

            _pointerId = args.Pointer.PointerId;
            _startPoint = point.Position;
            _element.CapturePointer(args.Pointer);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (!_attached || _dragStarted || _pointerId != args.Pointer.PointerId)
                return;

            var point = args.GetCurrentPoint(_element);
            var deltaX = point.Position.X - _startPoint.X;
            var deltaY = point.Position.Y - _startPoint.Y;
            if (deltaX * deltaX + deltaY * deltaY < DragThreshold * DragThreshold)
                return;

            _dragStarted = true;
            _element.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            BeginDrag(point);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (!_dragStarted && _pointerId == args.Pointer.PointerId)
                Reset(releaseCapture: true);
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
        {
            if (!_dragStarted && _pointerId == args.Pointer.PointerId)
                Reset(releaseCapture: true);
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
        {
            if (!_dragStarted && _pointerId == args.Pointer.PointerId)
                Reset(releaseCapture: false);
        }

        private async void BeginDrag(Microsoft.UI.Input.PointerPoint point)
        {
            SuspendInstantTooltips();
            try
            {
                await _element.StartDragAsync(point);
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"shell editor: failed to start drag: {ex.Message}");
            }
            finally
            {
                ResumeInstantTooltips();
                Reset(releaseCapture: true);
            }
        }

        private void Reset(bool releaseCapture)
        {
            if (releaseCapture)
                _element.ReleasePointerCaptures();
            _pointerId = null;
            _dragStarted = false;
            _element.CanDrag = false;
        }
    }

    private enum ShellDropZone
    {
        Tray,
        Remove,
    }

    private sealed record ShellDragSession(
        ShellDragPayload Payload,
        ShellDropZone Zone,
        int CandidateIndex,
        TrayId Tray);

    private abstract record ShellEditorRoute
    {
        public sealed record Modules : ShellEditorRoute;
        public sealed record Configuration(string InstanceId) : ShellEditorRoute;
    }

    private static readonly ShellEditorRoute ModulesRoute = new ShellEditorRoute.Modules();

    private static string DiagnosticRoute(ShellEditorRoute route) => route switch
    {
        ShellEditorRoute.Configuration configuration =>
            ShellNavigationDiagnostics.ConfigurationRoute(configuration.InstanceId),
        _ => "Modules",
    };

    public override Element Render()
    {
        var (page, setPage) = UseState(SettingsWindow.RequestedPage);
        var shellNavigation = UseNavigation<ShellEditorRoute>(ModulesRoute);
        UseEffect(() =>
        {
            void OnNavigated(NavigationEventArgs<ShellEditorRoute> args) =>
                _navigationDiagnostics.RouteChanged(
                    DiagnosticRoute(args.PreviousRoute), DiagnosticRoute(args.Route), args.Mode.ToString());
            shellNavigation.Navigated += OnNavigated;
            return () => shellNavigation.Navigated -= OnNavigated;
        }, shellNavigation);
        UseEffect(() =>
        {
            void OnPageRequested(SettingsPage requested)
            {
                _navigationDiagnostics.Record($"page-request source=tray-menu target={requested}");
                setPage(requested);
            }
            SettingsWindow.PageRequested += OnPageRequested;
            return () => SettingsWindow.PageRequested -= OnPageRequested;
        }, Array.Empty<object>());

        var colorScheme = UseColorScheme();
        var highContrastScheme = UseHighContrastScheme();
        var reduceMotion = UseReducedMotion();
        UseEffect(() => _navigationDiagnostics.RenderObserved(
            page, DiagnosticRoute(shellNavigation.CurrentRoute), reduceMotion),
            page, shellNavigation.CurrentRoute, reduceMotion);
        var (initialFocusRef, requestInitialFocus) = this.UseElementFocus();
        UseEffect(() =>
        {
            if (page == SettingsPage.Shells &&
                shellNavigation.CurrentRoute is ShellEditorRoute.Modules)
                requestInitialFocus();
        }, page, shellNavigation.CurrentRoute);
        var highContrast = colorScheme == ColorScheme.HighContrast;

        var configurationTheme = UseMemo(
            () => new ConfigurationThemeService(colorScheme, highContrastScheme),
            Array.Empty<object>());
        UseEffect(
            () => configurationTheme.Apply(colorScheme, highContrastScheme),
            colorScheme,
            highContrastScheme ?? "");

        var initialDraft = UseMemo(LoadDraftTrays, Array.Empty<object>());
        var (draft, setDraft) = UseState(initialDraft);
        var (dirty, setDirty) = UseState(false);
        var selectedId = shellNavigation.CurrentRoute is ShellEditorRoute.Configuration configurationRoute
            ? configurationRoute.InstanceId
            : null;
        var (selectedTrayId, setSelectedTrayId) = UseState(TrayId.PrimaryLeft);
        var (monitorPickerOpen, setMonitorPickerOpen) = UseState(false);
        var (monitorPickerOffset, setMonitorPickerOffset) = UseState(new Point(0, 0));
        var (dragSession, setDragSession) = UseState<ShellDragSession?>(null);
        var (activeTrayDragId, setActiveTrayDragId) = UseState<string?>(null);
        var (discardDialogOpen, setDiscardDialogOpen) = UseState(false);
        var initialDir = UseMemo(() => HostSettings.ConfigDir, Array.Empty<object>());
        var (dirText, setDirText) = UseState(initialDir);
        var (appliedDir, setAppliedDir) = UseState(initialDir);

        var shellPageRef = this.UseElementRef<Grid>();
        var removalPanelRef = this.UseElementRef<Border>();
        var trayScrollerRef = this.UseElementRef<ScrollView>();
        var rightTrayScrollerRef = this.UseElementRef<ScrollView>();
        var rightTrayTargetRef = this.UseElementRef<Border>();
        var monitorButtonRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.Button>();
        var windowRootRef = this.UseElementRef<Grid>();
        var activeTrayDragRef = UseRef<string?>(null);
        var draftRef = UseRef(draft);
        var selectedTrayRef = UseRef(selectedTrayId);
        var dragSessionRef = UseRef<ShellDragSession?>(dragSession);
        var autoScrollDirectionRef = UseRef(0);
        var lastTrayPointerXRef = UseRef(0d);
        var autoScrollTickRef = UseRef<Action>(() => { });
        draftRef.Current = draft;
        selectedTrayRef.Current = selectedTrayId;
        dragSessionRef.Current = dragSession;

        void OpenConfiguration(string instanceId)
        {
            var from = DiagnosticRoute(shellNavigation.CurrentRoute);
            var to = ShellNavigationDiagnostics.ConfigurationRoute(instanceId);
            if (shellNavigation.CurrentRoute is ShellEditorRoute.Configuration current)
            {
                if (current.InstanceId == instanceId)
                {
                    _navigationDiagnostics.Request("Replace", from, to, "tray-select", ignored: true);
                    return;
                }

                _navigationDiagnostics.Request("Replace", from, to, "tray-select");
                shellNavigation.Replace(new ShellEditorRoute.Configuration(instanceId));
                return;
            }

            _navigationDiagnostics.Request("Navigate", from, to, "tray-select");
            shellNavigation.Navigate(new ShellEditorRoute.Configuration(instanceId));
        }

        void BackToModules() => ReturnToModules("back-button");

        void ReturnToModules(string reason)
        {
            _navigationDiagnostics.Request("Back", DiagnosticRoute(shellNavigation.CurrentRoute), "Modules", reason,
                ignored: shellNavigation.CurrentRoute is ShellEditorRoute.Modules);
            if (shellNavigation.CurrentRoute is ShellEditorRoute.Modules)
                return;

            if (!shellNavigation.GoBack())
            {
                _navigationDiagnostics.Record("back-fallback operation=Reset target=Modules");
                shellNavigation.Reset(ModulesRoute);
            }
        }

        // The draft is a list of per-display trays; editing targets the tray
        // halves of the display chosen in the monitor picker. Selection
        // never dirties the draft — only content changes do.
        TrayId ActiveTrayId(TrayEdge edge) =>
            selectedTrayRef.Current with { Edge = edge };

        List<TrayConfig.Entry> EntriesForTray(List<DraftTray> trays, TrayId id) =>
            trays.FirstOrDefault(t => t.Id == id)?.Entries ?? [];

        IEnumerable<string> AllInstanceIds(List<DraftTray> trays) =>
            trays.SelectMany(t => t.Entries).Select(e => e.Id);

        // Replaces one tray's entry list with the mutation's result. A
        // mutation that returns its input is a no-op (no dirty flag). The
        // tray is created on first content change, so selecting a display is
        // enough to start editing it.
        bool MutateTrayEntries(TrayId trayId, Func<List<TrayConfig.Entry>, List<TrayConfig.Entry>> mutate)
        {
            var trays = draftRef.Current;
            var index = trays.FindIndex(t => t.Id == trayId);
            if (index < 0)
            {
                trays = [.. trays, new DraftTray { Id = trayId, Entries = [] }];
                index = trays.Count - 1;
            }
            var current = trays[index].Entries;
            var next = mutate(current);
            if (ReferenceEquals(next, current))
                return false;

            var nextTrays = new List<DraftTray>(trays)
            {
                [index] = new DraftTray { Id = trayId, Entries = next },
            };
            draftRef.Current = nextTrays;
            setDraft(nextTrays);
            setDirty(true);
            return true;
        }

        // Instance ids are globally unique, so id-addressed edits search all
        // trays (the configuration page does not know which tray is active).
        TrayId? TrayContaining(string instanceId) =>
            draftRef.Current.FirstOrDefault(t => t.Entries.Any(e => e.Id == instanceId))?.Id;

        bool MutateTrayContaining(string instanceId, Func<List<TrayConfig.Entry>, List<TrayConfig.Entry>> mutate)
        {
            var tray = draftRef.Current.FirstOrDefault(t => t.Entries.Any(e => e.Id == instanceId));
            return tray != null && MutateTrayEntries(tray.Id, mutate);
        }

        // The tray a display tile edits: the primary display uses the
        // portable "primary" alias unless the config already owns it by
        // hardware identity; every other display uses its EDID identity key.
        TrayId TrayIdForDisplay(DisplayInfo display)
        {
            if (display.IsPrimary)
            {
                var identityOwned = draftRef.Current.Any(t =>
                    t.Id.Edge == TrayEdge.Left && t.Id.MonitorKey == display.IdentityKey);
                var aliasOwned = draftRef.Current.Any(t => t.Id.IsPrimaryAlias);
                return identityOwned && !aliasOwned
                    ? new TrayId(display.IdentityKey, TrayEdge.Left)
                    : TrayId.PrimaryLeft;
            }
            return new TrayId(display.IdentityKey, TrayEdge.Left);
        }

        // Human-readable tray identity: the monitor's friendly name (never a
        // display number), with connection state for the picker's extra rows.
        string TrayLabel(TrayId id, out bool connected)
        {
            string name;
            if (id.IsPrimaryAlias)
            {
                var primary = DisplayTopology.Displays.FirstOrDefault(d => d.IsPrimary);
                name = primary != null ? $"{primary.FriendlyName} (Primary)" : "Primary display";
                connected = primary != null;
            }
            else if (DisplayTopology.Find(id.MonitorKey) is { } live)
            {
                name = live.FriendlyName;
                connected = true;
            }
            else if (DisplayTopology.TryGetRecorded(id.MonitorKey, out var recorded))
            {
                name = recorded.Name;
                connected = false;
            }
            else
            {
                name = id.MonitorKey;
                connected = false;
            }
            return id.Edge == TrayEdge.Right ? $"{name} — right edge" : name;
        }

        void SelectTray(TrayId id)
        {
            selectedTrayRef.Current = id;
            setSelectedTrayId(id);
            setMonitorPickerOpen(false);
        }

        void OpenMonitorPicker()
        {
            // Pick up hot-plugged displays immediately rather than waiting
            // for the controller's throttled refresh.
            DisplayTopology.Refresh();
            if (monitorButtonRef.Current is { } button && windowRootRef.Current is { } root)
            {
                var (width, height) = EstimatePickerSize();
                var origin = button.TransformToVisual(root).TransformPoint(new Point(0, 0));
                // The button sits at the bottom edge of the window: open the
                // picker fully above it (right-aligned), clamped inside the
                // window — like FlyoutPlacementMode.TopEdgeAlignedRight.
                var x = Math.Clamp(
                    origin.X + button.ActualWidth - width,
                    8,
                    Math.Max(8, root.ActualWidth - width - 8));
                var y = Math.Clamp(
                    origin.Y - height - 4,
                    8,
                    Math.Max(8, root.ActualHeight - height - 8));
                setMonitorPickerOffset(new Point(x, y));
            }
            setMonitorPickerOpen(true);
        }

        // Mirrors the BuildMonitorPicker layout math so OpenMonitorPicker can
        // place the popup without a measure pass. Slightly overestimates
        // height, which only shifts the popup further into the window.
        (double Width, double Height) EstimatePickerSize()
        {
            var displays = DisplayTopology.Displays;
            double canvasWidth = 0, canvasHeight = 0;
            if (displays.Count > 0)
            {
                var originX = displays.Min(d => d.Rect.Left);
                var originY = displays.Min(d => d.Rect.Top);
                var virtualWidth = displays.Max(d => d.Rect.Right) - originX;
                var virtualHeight = displays.Max(d => d.Rect.Bottom) - originY;
                const double maxWidth = 320, maxHeight = 180;
                var scale = Math.Min(
                    maxWidth / Math.Max(1, virtualWidth),
                    maxHeight / Math.Max(1, virtualHeight));
                canvasWidth = virtualWidth * scale;
                canvasHeight = virtualHeight * scale;
            }
            var tileKeys = displays.Select(d => TrayIdForDisplay(d).MonitorKey).ToHashSet();
            var extras = draftRef.Current.Count(tray => !tileKeys.Contains(tray.Id.MonitorKey));
            // 16 padding per side; extras cost ~22 for the header caption and
            // ~50 per row; 24 slack for borders and estimate drift.
            var width = Math.Max(canvasWidth, 200) + 32;
            var height = canvasHeight + (extras > 0 ? 22 + extras * 50 : 0) + 32 + 24;
            return (width, height);
        }

        UseEffect(() =>
        {
            if (shellNavigation.CurrentRoute is ShellEditorRoute.Configuration current &&
                draft.SelectMany(t => t.Entries).All(entry => entry.Id != current.InstanceId))
            {
                ReturnToModules("missing-instance");
            }
        }, shellNavigation.CurrentRoute, draft);

        var autoScrollTimer = UseMemo(() =>
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue
                .GetForCurrentThread()
                .CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(32);
            timer.IsRepeating = true;
            return timer;
        }, Array.Empty<object>());
        var scrollOptions = UseMemo(
            () => new ScrollingScrollOptions(
                ScrollingAnimationMode.Disabled,
                ScrollingSnapPointsMode.Ignore),
            Array.Empty<object>());
        UseEffect(() =>
        {
            void OnTick(
                Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
                object args) => autoScrollTickRef.Current();
            autoScrollTimer.Tick += OnTick;
            return () =>
            {
                autoScrollTimer.Stop();
                autoScrollTimer.Tick -= OnTick;
            };
        }, autoScrollTimer);

        UseClosingGuard(() =>
        {
            if (SettingsWindow.AllowClose || !dirty)
                return true;

            setDiscardDialogOpen(true);
            return false;
        });

        const double targetVisualScale = 1;

        void StopAutoScroll()
        {
            autoScrollDirectionRef.Current = 0;
            lastTrayPointerXRef.Current = 0;
            if (autoScrollTimer.IsRunning)
                autoScrollTimer.Stop();
        }

        void SetDragSessionState(ShellDragSession? next)
        {
            if (Equals(dragSessionRef.Current, next))
                return;

            dragSessionRef.Current = next;
            setDragSession(next);
        }

        void ClearDragSession()
        {
            StopAutoScroll();
            SetDragSessionState(null);
        }

        void BeginTrayDrag(string instanceId, TrayId sourceTray)
        {
            activeTrayDragRef.Current = instanceId;
            var payload = ShellDragPayload.ForInstance(instanceId);
            var sourceIndex = SourceIndexFor(payload, EntriesForTray(draftRef.Current, sourceTray));
            if (sourceIndex >= 0)
            {
                // Start in the no-op position so the source stays visible until
                // the pointer actually crosses an insertion boundary.
                SetDragSessionState(new ShellDragSession(
                    payload,
                    ShellDropZone.Tray,
                    sourceIndex,
                    sourceTray));
            }
            var queued = Microsoft.UI.Dispatching.DispatcherQueue
                .GetForCurrentThread()
                .TryEnqueue(() =>
                {
                    if (activeTrayDragRef.Current == instanceId)
                        setActiveTrayDragId(instanceId);
                });
            if (!queued && activeTrayDragRef.Current == instanceId)
                setActiveTrayDragId(instanceId);
        }

        void EndTrayDrag()
        {
            activeTrayDragRef.Current = null;
            setActiveTrayDragId(null);
        }

        int SourceIndexFor(
            ShellDragPayload payload,
            IReadOnlyList<TrayConfig.Entry> entries)
        {
            if (payload.InstanceId is not { } instanceId)
                return -1;
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Id == instanceId)
                    return index;
            }
            return -1;
        }

        bool IsOriginalTrayPosition(
            ShellDragSession? session,
            IReadOnlyList<TrayConfig.Entry> entries)
        {
            if (session is not { Zone: ShellDropZone.Tray } ||
                session.Payload.InstanceId == null)
            {
                return false;
            }

            var sourceIndex = SourceIndexFor(session.Payload, entries);
            return sourceIndex >= 0 && session.CandidateIndex == sourceIndex;
        }

        int CandidateIndexFor(
            ShellDragPayload payload,
            double contentX,
            IReadOnlyList<TrayConfig.Entry> entries)
        {
            // Existing drag sources keep their layout slot but are invisible.
            // Ignore that slot's midpoint so its left/right boundaries collapse
            // into one candidate position among the remaining visible icons.
            var sourceIndex = SourceIndexFor(payload, entries);
            var localX = contentX / targetVisualScale - StripPadX;
            var insertionIndex = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                if (index == sourceIndex)
                    continue;
                if (localX < index * ChipPitch + ChipSize / 2)
                    break;
                insertionIndex++;
            }

            return Math.Clamp(
                insertionIndex,
                0,
                entries.Count - (sourceIndex >= 0 ? 1 : 0));
        }

        double InsertionOffsetFor(
            ShellDragPayload payload,
            int candidateIndex,
            IReadOnlyList<TrayConfig.Entry> entries)
        {
            var sourceIndex = SourceIndexFor(payload, entries);
            var visibleIndices = Enumerable.Range(0, entries.Count)
                .Where(index => index != sourceIndex)
                .ToArray();
            if (visibleIndices.Length == 0)
                return 0;

            var insertionIndex = Math.Clamp(candidateIndex, 0, visibleIndices.Length);
            double boundaryCenter;
            if (insertionIndex == 0)
            {
                var first = visibleIndices[0];
                boundaryCenter = first == 0
                    ? 0
                    : first * ChipPitch - ChipGap / 2;
            }
            else if (insertionIndex == visibleIndices.Length)
            {
                var last = visibleIndices[^1];
                boundaryCenter = last == entries.Count - 1
                    ? last * ChipPitch + ChipSize
                    : last * ChipPitch + ChipSize + ChipGap / 2;
            }
            else
            {
                var left = visibleIndices[insertionIndex - 1];
                var right = visibleIndices[insertionIndex];
                boundaryCenter = (left * ChipPitch + ChipSize + right * ChipPitch) / 2;
            }

            return Math.Max(0, boundaryCenter - InsertionIndicatorWidth / 2);
        }

        bool TryResolveDrag(
            DragData data,
            Point rootPoint,
            out ShellDragSession resolved,
            out DragOperations operation,
            out double trayPointerX)
        {
            resolved = null!;
            operation = DragOperations.None;
            trayPointerX = 0;
            if (!TryGetPayload(data, out var payload) ||
                (payload.KindId is null) == (payload.InstanceId is null))
            {
                return false;
            }

            var sourceTray = payload.InstanceId == null
                ? null
                : TrayContaining(payload.InstanceId);
            if (payload.InstanceId != null && sourceTray == null)
                return false;
            if (payload.KindId != null && !ModuleLoader.TryGetKind(payload.KindId, out _))
                return false;

            var root = shellPageRef.Current;
            var removalPanel = removalPanelRef.Current;
            if (root == null || removalPanel == null)
                return false;

            try
            {
                foreach (var (edge, scroll) in new (TrayEdge Edge, ScrollView? Scroll)[]
                         {
                             (TrayEdge.Left, trayScrollerRef.Current),
                             (TrayEdge.Right, rightTrayScrollerRef.Current),
                         })
                {
                    if (scroll == null)
                        continue;
                    var origin = scroll.TransformToVisual(root)
                        .TransformPoint(new Point(0, 0));
                    var zonePointerX = rootPoint.X - origin.X;
                    var inTray = zonePointerX >= 0 &&
                        zonePointerX <= scroll.ActualWidth &&
                        rootPoint.Y >= origin.Y &&
                        rootPoint.Y <= origin.Y + scroll.ActualHeight;
                    if (!inTray)
                        continue;

                    var trayId = ActiveTrayId(edge);
                    var contentX = zonePointerX + scroll.HorizontalOffset;
                    if (edge == TrayEdge.Right)
                    {
                        // Right-zone chips hug the zone's right edge (the
                        // system-area side), so measure candidates as
                        // distance from that edge — the same spacing math
                        // then yields config-order indexes directly.
                        var zoneWidth = rightTrayTargetRef.Current?.ActualWidth ??
                            scroll.ActualWidth;
                        contentX = zoneWidth - contentX;
                    }

                    resolved = new ShellDragSession(
                        payload,
                        ShellDropZone.Tray,
                        CandidateIndexFor(
                            payload,
                            contentX,
                            EntriesForTray(draftRef.Current, trayId)),
                        trayId);
                    operation = payload.InstanceId == null
                        ? DragOperations.Copy
                        : DragOperations.Move;
                    trayPointerX = zonePointerX;
                    return true;
                }

                var removalOrigin = removalPanel.TransformToVisual(root)
                    .TransformPoint(new Point(0, 0));
                var inRemovalPanel = rootPoint.X >= removalOrigin.X &&
                    rootPoint.X <= removalOrigin.X + removalPanel.ActualWidth &&
                    rootPoint.Y >= removalOrigin.Y &&
                    rootPoint.Y <= removalOrigin.Y + removalPanel.ActualHeight;
                if (payload.InstanceId != null && inRemovalPanel)
                {
                    resolved = new ShellDragSession(
                        payload,
                        ShellDropZone.Remove,
                        -1,
                        sourceTray!.Value);
                    operation = DragOperations.Move;
                    return true;
                }
            }
            catch (Exception)
            {
                // Layout can detach between page navigation and a routed drag
                // event. Reject that event rather than retaining stale state.
            }

            return false;
        }

        void UpdateAutoScroll(ShellDragSession resolved, double trayPointerX)
        {
            var scroll = resolved.Tray.Edge == TrayEdge.Right
                ? rightTrayScrollerRef.Current
                : trayScrollerRef.Current;
            if (resolved.Zone != ShellDropZone.Tray || scroll == null ||
                scroll.ScrollableWidth <= 0)
            {
                StopAutoScroll();
                return;
            }

            lastTrayPointerXRef.Current = trayPointerX;
            var direction = trayPointerX < AutoScrollEdge && scroll.HorizontalOffset > 0
                ? -1
                : trayPointerX > scroll.ActualWidth - AutoScrollEdge &&
                    scroll.HorizontalOffset < scroll.ScrollableWidth
                    ? 1
                    : 0;
            autoScrollDirectionRef.Current = direction;
            if (direction == 0)
            {
                if (autoScrollTimer.IsRunning)
                    autoScrollTimer.Stop();
            }
            else if (!autoScrollTimer.IsRunning)
            {
                autoScrollTimer.Start();
            }
        }

        void UpdateDragTarget(DragTargetArgs args)
        {
            if (!TryResolveDrag(
                    args.Data,
                    args.Position,
                    out var resolved,
                    out var operation,
                    out var trayPointerX))
            {
                args.AcceptedOperation = DragOperations.None;
                args.UIOverride.IsCaptionVisible = false;
                ClearDragSession();
                return;
            }

            args.AcceptedOperation = operation;
            var movingAcrossTrays = resolved.Payload.InstanceId != null &&
                TrayContaining(resolved.Payload.InstanceId) is { } fromTray &&
                fromTray != resolved.Tray;
            args.UIOverride.Caption = resolved.Zone == ShellDropZone.Remove
                ? "Remove from Tray"
                : resolved.Payload.InstanceId == null
                    ? "Add to Tray"
                    : movingAcrossTrays
                        ? $"Move to {TrayId.EdgeName(resolved.Tray.Edge)} tray"
                        : "Reorder in Tray";
            args.UIOverride.IsCaptionVisible = true;
            args.UIOverride.IsContentVisible = true;
            args.UIOverride.IsGlyphVisible = true;
            SetDragSessionState(resolved);
            UpdateAutoScroll(resolved, trayPointerX);
        }

        void CommitDrop(ShellDragSession resolved)
        {
            StopAutoScroll();
            EndTrayDrag();
            string? removedId = null;
            string? crossTrayRemovedId = null;
            // StartDragAsync still owns the dragged element while OnDrop runs.
            // A keyed Spring move would claim that same Composition Visual and
            // can leave stale offsets or opacity behind, so the mutation
            // commits synchronously.
            var changed = MutateTrayEntries(resolved.Tray, entries =>
            {
                var next = new List<TrayConfig.Entry>(entries);
                var changedInner = false;

                if (resolved.Zone == ShellDropZone.Remove &&
                    resolved.Payload.InstanceId is { } removeId)
                {
                    changedInner = next.RemoveAll(entry => entry.Id == removeId) > 0;
                    removedId = changedInner ? removeId : null;
                }
                else if (resolved.Zone == ShellDropZone.Tray &&
                         resolved.Payload.KindId is { } kindId)
                {
                    next.Insert(
                        Math.Clamp(resolved.CandidateIndex, 0, next.Count),
                        new TrayConfig.Entry(
                            kindId,
                            TrayConfig.NextId(AllInstanceIds(draftRef.Current)),
                            null));
                    changedInner = true;
                }
                else if (resolved.Zone == ShellDropZone.Tray &&
                         resolved.Payload.InstanceId is { } instanceId)
                {
                    var from = next.FindIndex(entry => entry.Id == instanceId);
                    if (from >= 0)
                    {
                        var insertAt = Math.Clamp(
                            resolved.CandidateIndex,
                            0,
                            next.Count - 1);
                        if (insertAt != from)
                        {
                            var entry = next[from];
                            next.RemoveAt(from);
                            insertAt = Math.Clamp(insertAt, 0, next.Count);
                            next.Insert(insertAt, entry);
                            changedInner = true;
                        }
                    }
                    else
                    {
                        // Cross-tray move: the instance lives in another
                        // draft tray. Insert it here and remove it from the
                        // owning tray in a second mutation below.
                        var moved = draftRef.Current
                            .SelectMany(tray => tray.Entries)
                            .FirstOrDefault(entry => entry.Id == instanceId);
                        if (moved != null)
                        {
                            next.Insert(
                                Math.Clamp(resolved.CandidateIndex, 0, next.Count),
                                moved);
                            crossTrayRemovedId = instanceId;
                            changedInner = true;
                        }
                    }
                }

                return changedInner ? next : entries;
            });

            if (crossTrayRemovedId != null)
            {
                changed |= MutateTrayContaining(crossTrayRemovedId, entries =>
                {
                    var next = entries.Where(entry => entry.Id != crossTrayRemovedId).ToList();
                    return next.Count == entries.Count ? entries : next;
                });
            }

            if (!changed)
            {
                ClearDragSession();
                return;
            }

            dragSessionRef.Current = null;
            if ((removedId ?? crossTrayRemovedId) != null && selectedId == (removedId ?? crossTrayRemovedId))
                ReturnToModules("drag-remove");
            setDragSession(null);
        }

        autoScrollTickRef.Current = () =>
        {
            var resolved = dragSessionRef.Current;
            var scroll = resolved?.Tray.Edge == TrayEdge.Right
                ? rightTrayScrollerRef.Current
                : trayScrollerRef.Current;
            var direction = autoScrollDirectionRef.Current;
            if (resolved?.Zone != ShellDropZone.Tray || scroll == null || direction == 0)
            {
                StopAutoScroll();
                return;
            }

            var currentOffset = scroll.HorizontalOffset;
            var nextOffset = Math.Clamp(
                currentOffset + direction * AutoScrollStep,
                0,
                scroll.ScrollableWidth);
            if (Math.Abs(nextOffset - currentOffset) < 0.5)
            {
                StopAutoScroll();
                return;
            }

            scroll.ScrollBy(nextOffset - currentOffset, 0, scrollOptions);
            var contentX = lastTrayPointerXRef.Current + nextOffset;
            if (resolved.Tray.Edge == TrayEdge.Right)
            {
                contentX = (rightTrayTargetRef.Current?.ActualWidth ?? scroll.ActualWidth) -
                    contentX;
            }
            SetDragSessionState(resolved with
            {
                CandidateIndex = CandidateIndexFor(
                    resolved.Payload,
                    contentX,
                    EntriesForTray(draftRef.Current, resolved.Tray)),
            });
        };

        void RemoveInstance(string instanceId)
        {
            var removed = MutateTrayContaining(instanceId, entries =>
            {
                var next = entries.Where(entry => entry.Id != instanceId).ToList();
                return next.Count == entries.Count ? entries : next;
            });
            if (removed && selectedId == instanceId)
                ReturnToModules("remove-instance");
        }

        void UpdateEntrySettings(string instanceId, JsonElement? settings)
        {
            if (settings is { } value && value.ValueKind != JsonValueKind.Object)
            {
                PagurianLog.HostError(
                    $"shell editor: configuration view for #{instanceId} returned non-object JSON");
                return;
            }

            MutateTrayContaining(instanceId, entries =>
            {
                var index = entries.FindIndex(entry => entry.Id == instanceId);
                if (index < 0)
                    return entries;

                var next = new List<TrayConfig.Entry>(entries);
                next[index] = next[index] with
                {
                    Settings = settings is { } replacement ? replacement.Clone() : null,
                };
                return next;
            });
        }

        void SaveDraft()
        {
            if (!dirty)
                return;

            var groups = draftRef.Current
                .Select(tray => new TrayConfig.TrayGroup(tray.Id, tray.Entries.ToArray()))
                .ToArray();
            try
            {
                TrayConfig.Save(groups);
                TrayManager.ApplyConfig(groups);
            }
            catch (Exception ex)
            {
                MessageBoxes.Show(
                    $"Failed to save the configuration.\n\n{ex.Message}",
                    "Pagurian Settings");
                return;
            }

            setDirty(false);
            PagurianLog.Host(
                $"settings: applied tray configuration " +
                $"({groups.Sum(g => g.Entries.Count)} shells across {groups.Length} trays)");
        }

        void RevertDraft()
        {
            var next = LoadDraftTrays();
            if (selectedId != null &&
                next.SelectMany(t => t.Entries).All(entry => entry.Id != selectedId))
                ReturnToModules("revert");
            draftRef.Current = next;
            setDraft(next);
            setDirty(false);
        }

        void ApplyDir()
        {
            try
            {
                Directory.CreateDirectory(dirText.Trim());
                HostSettings.SetConfigDir(dirText.Trim());
                var next = LoadDraftTrays();
                TrayManager.ApplyConfig(next
                    .Select(tray => new TrayConfig.TrayGroup(tray.Id, tray.Entries.ToArray()))
                    .ToArray());
                setAppliedDir(HostSettings.ConfigDir);
                setDirText(HostSettings.ConfigDir);
                if (selectedId != null &&
                    next.SelectMany(t => t.Entries).All(entry => entry.Id != selectedId))
                    ReturnToModules("config-directory-change");
                draftRef.Current = next;
                setDraft(next);
                setDirty(false);
            }
            catch (Exception ex)
            {
                MessageBoxes.Show(
                    $"Failed to apply the configuration folder.\n\n{ex.Message}",
                    "Pagurian Settings");
            }
        }

        void Browse() => _ = BrowseAsync();
        async Task BrowseAsync()
        {
            var path = await PickFolderAsync();
            if (path != null)
                setDirText(path);
        }

        // Drag hover never projects a new ordering. At the no-op insertion
        // position the source remains visible at half opacity; after crossing
        // another boundary it becomes transparent and a non-layout insertion
        // marker is overlaid.
        //
        // One drop zone per taskbar edge of the selected display. Left zone:
        // icons fill from the left in config order. Right zone: icons hug the
        // zone's right edge (nearest the system area) in reverse config
        // order, and insertion candidates are measured as distance from that
        // edge — so the config-order spacing math works unchanged for both.
        var targetHeightDip = (ChipSize + 8) * targetVisualScale;

        Element BuildTrayTarget(TrayEdge edge, List<TrayConfig.Entry> entries)
        {
            var trayId = ActiveTrayId(edge);
            var zoneSession = dragSession is { Zone: ShellDropZone.Tray } session &&
                              session.Tray == trayId
                ? session
                : null;
            var atOriginalPosition = zoneSession != null &&
                IsOriginalTrayPosition(zoneSession, entries);
            var chips = entries
                .Select((entry, index) => TargetChip(
                    entry,
                    selectedId == entry.Id,
                    () => OpenConfiguration(entry.Id),
                    () => RemoveInstance(entry.Id),
                    () => BeginTrayDrag(entry.Id, trayId),
                    () =>
                    {
                        EndTrayDrag();
                        ClearDragSession();
                    },
                    index + 1,
                    entries.Count,
                    highContrast,
                    targetVisualScale,
                    activeTrayDragId != entry.Id
                        ? 1
                        : zoneSession != null && atOriginalPosition ? 0.5 : 0))
                .ToArray();
            if (edge == TrayEdge.Right)
                Array.Reverse(chips);

            Element trayIcons = chips.Length == 0
                ? Caption("Drop shells here")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                : HStack(ChipGap * targetVisualScale, chips)
                    .HAlign(edge == TrayEdge.Right
                        ? HorizontalAlignment.Right
                        : HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center);

            var insertionIndicatorVisible = zoneSession != null && !atOriginalPosition;
            var insertionOffset = 0d;
            if (insertionIndicatorVisible && zoneSession != null)
            {
                var insertionIndex = Math.Clamp(
                    zoneSession.CandidateIndex,
                    0,
                    entries.Count - (zoneSession.Payload.InstanceId == null ? 0 : 1));
                // For the right zone InsertionOffsetFor's result is a
                // distance from the chips' right edge — exactly where the
                // indicator anchors.
                insertionOffset = InsertionOffsetFor(
                    zoneSession.Payload,
                    insertionIndex,
                    entries);
            }
            var indicator = (Border(null) with
                {
                    CornerRadius = InsertionIndicatorWidth * targetVisualScale / 2,
                })
                .Width(InsertionIndicatorWidth * targetVisualScale)
                .Height(32 * targetVisualScale)
                .VAlign(VerticalAlignment.Center)
                .Background(highContrast
                    ? Theme.Ref("SystemColorHighlightColorBrush")
                    : Theme.Accent)
                .Opacity(insertionIndicatorVisible ? 1 : 0)
                .OnMountAdd(element => element.IsHitTestVisible = false)
                .AccessibilityHidden()
                .WithKey($"shell-insertion-indicator-{TrayId.EdgeName(edge)}");
            indicator = edge == TrayEdge.Right
                ? indicator
                    .Margin(0, 0, insertionOffset * targetVisualScale, 0)
                    .HAlign(HorizontalAlignment.Right)
                : indicator
                    .Margin(insertionOffset * targetVisualScale, 0, 0, 0)
                    .HAlign(HorizontalAlignment.Left);

            var targetContent = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [trayIcons, indicator]);

            var stripHot = zoneSession != null;
            var targetFill = stripHot
                ? highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.SystemAttentionBackground
                : highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.LayerFill;
            var targetStroke = stripHot
                ? highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.Accent
                : highContrast ? Theme.Ref("SystemColorWindowTextColorBrush") : Theme.ControlStroke;
            var borderThickness = stripHot || highContrast ? 2d : 1d;
            // Width/Height include BorderThickness and Padding. Reserve the
            // border inside the target so its 44-DIP drag surfaces do not clip.
            var paddingX = Math.Max(
                0,
                StripPadX * targetVisualScale - borderThickness);
            var paddingY = Math.Max(
                0,
                4 * targetVisualScale - borderThickness);

            Element target = (Border(targetContent) with { CornerRadius = 4 * targetVisualScale })
                .MinWidth(EmptyTargetWidth * targetVisualScale)
                .Height(targetHeightDip)
                .Padding(paddingX, paddingY, paddingX, paddingY)
                .Background(targetFill)
                .WithBorder(targetStroke, borderThickness)
                .AutomationName(edge == TrayEdge.Right
                    ? "Right tray drop target"
                    : "Left tray drop target")
                .HelpText(edge == TrayEdge.Right
                    ? "Drop module icons here to add them to the right tray, which sits left of the system clock and notification area. Icons are ordered outward from the system area."
                    : "Drop module icons here to add them to the left tray. The insertion line shows the pending position. Drag a Tray icon to the right half for the right tray, or onto the Modules panel to remove it.")
                .IsTabStop(true)
                .OnTapped((_, _) => ReturnToModules("tray-background"))
                .OnKeyDown((_, args) =>
                {
                    if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                    {
                        ReturnToModules("tray-keyboard");
                        args.Handled = true;
                    }
                });
            if (edge == TrayEdge.Left)
                target = target.Ref(initialFocusRef);
            else
                target = target.Ref(rightTrayTargetRef);
            return target;
        }

        var leftEntries = EntriesForTray(draft, ActiveTrayId(TrayEdge.Left));
        var rightEntries = EntriesForTray(draft, ActiveTrayId(TrayEdge.Right));
        var leftTarget = BuildTrayTarget(TrayEdge.Left, leftEntries);
        var rightTarget = BuildTrayTarget(TrayEdge.Right, rightEntries);

        // The right zone anchors its chips at its right edge, so content that
        // outgrows the viewport must rest scrolled fully right (ScrollView's
        // origin is the left edge otherwise). Re-applying on entry-count
        // changes keeps a just-dropped icon visible without fighting the
        // user's own scrolling.
        UseEffect(() =>
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                var scroll = rightTrayScrollerRef.Current;
                if (scroll != null && scroll.ScrollableWidth > 0)
                    scroll.ScrollTo(scroll.ScrollableWidth, scroll.VerticalOffset, scrollOptions);
            });
        }, rightEntries.Count);

        var cardFill = highContrast
            ? Theme.Ref("SystemColorWindowColorBrush")
            : Theme.CardBackground;
        var cardStroke = highContrast
            ? Theme.Ref("SystemColorWindowTextColorBrush")
            : Theme.CardStroke;

        var moduleCards = ModuleLoader.Modules
            .Select(module => (
                Module: module,
                Kinds: ModuleLoader.Kinds
                    .Where(kind => kind.ShellType.Assembly == module.GetType().Assembly)
                    .ToList()))
            .Where(group => group.Kinds.Count > 0)
            .Select(group => ModuleCard(
                group.Module,
                group.Kinds,
                ClearDragSession,
                highContrast))
            .ToArray();

        Element catalogContent = moduleCards.Length == 0
            ? Body("No modules loaded")
                .Foreground(Theme.SecondaryText)
            : (FlexRow(moduleCards) with
            {
                Wrap = FlexWrap.Wrap,
                AlignItems = FlexAlign.FlexStart,
                AlignContent = FlexAlign.FlexStart,
                RowGap = 12,
                ColumnGap = 12,
            });

        Element ModulesPanel() =>
            FlexColumn(
                Subtitle("Modules")
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                Body("Drag a Shell icon to the Tray below to add it.")
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 4, 0, 0),
                catalogContent.Margin(0, 16, 0, 0));

        Element ConfigurationPanel(string instanceId)
        {
            var selectedEntry = draft
                .SelectMany(t => t.Entries)
                .FirstOrDefault(entry => entry.Id == instanceId);
            if (selectedEntry == null)
                return ModulesPanel();

            var selectedName = NameFor(selectedEntry.ShellType);
            var removeBackground = highContrast
                ? Theme.Ref("SystemColorHighlightColorBrush")
                : Theme.SystemCritical;
            var removeForeground = highContrast
                ? Theme.Ref("SystemColorHighlightTextColorBrush")
                : Theme.Ref("TextOnAccentFillColorPrimaryBrush");

            return FlexColumn(
                Grid(
                    [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                    [GridSize.Auto],
                    [
                        FlexColumn(
                                Subtitle($"Configuration: {selectedName}")
                                    .HeadingLevel(AutomationHeadingLevel.Level2),
                                Caption($"Shell #{selectedEntry.Id}")
                                    .Foreground(Theme.SecondaryText)
                                    .Margin(0, 4, 0, 0))
                            .Grid(row: 0, column: 0),
                        Button("Remove", () => RemoveInstance(selectedEntry.Id))
                            .Resources(resources => resources
                                .Set("ButtonBackground", removeBackground)
                                .Set("ButtonBackgroundPointerOver", removeBackground)
                                .Set("ButtonBackgroundPressed", removeBackground)
                                .Set("ButtonBorderBrush", removeBackground)
                                .Set("ButtonBorderBrushPointerOver", removeBackground)
                                .Set("ButtonBorderBrushPressed", removeBackground)
                                .Set("ButtonForeground", removeForeground)
                                .Set("ButtonForegroundPointerOver", removeForeground)
                                .Set("ButtonForegroundPressed", removeForeground))
                            .Grid(row: 0, column: 1),
                        Button("Back to modules", BackToModules)
                            .Margin(8, 0, 0, 0)
                            .Grid(row: 0, column: 2),
                    ]),
                ShellConfigurationPanel(
                        selectedEntry,
                        configurationTheme,
                        highContrast,
                        settings => UpdateEntrySettings(selectedEntry.Id, settings))
                    .Margin(0, 16, 0, 0));
        }

        Element PanelForRoute(ShellEditorRoute route)
        {
            var content = route switch
            {
                ShellEditorRoute.Configuration configuration =>
                    ConfigurationPanel(configuration.InstanceId),
                _ => ModulesPanel(),
            };
            var displayedRoute = route is ShellEditorRoute.Configuration selected &&
                draft.SelectMany(t => t.Entries).All(entry => entry.Id != selected.InstanceId)
                ? "Modules"
                : DiagnosticRoute(route);
            return content
                .OnMountAdd(element => _navigationDiagnostics.MountPage(element, displayedRoute))
                .OnUnmountAdd(_navigationDiagnostics.UnmountCallback);
        }

        var panelBody = (NavigationHost(shellNavigation, PanelForRoute) with
        {
            CacheMode = NavigationCacheMode.Disabled,
            Transition = reduceMotion
                ? NavigationTransition.None
                : NavigationTransition.Spring(),
        })
            .OnMountAdd(_navigationDiagnostics.MountHostCallback)
            .OnUnmountAdd(_navigationDiagnostics.UnmountCallback);

        var scrollBody = ScrollView(Border(panelBody).Padding(24))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch);

        var header = Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                [
                    FlexColumn(
                            Title("Edit Shells")
                                .HeadingLevel(AutomationHeadingLevel.Level1),
                            Caption("Choose modules and arrange the Tray below, then save your changes.")
                                .Foreground(Theme.SecondaryText)
                                .Margin(0, 4, 0, 0))
                        .Grid(row: 0, column: 0),
                    Caption(dirty ? "Unsaved changes" : "No changes")
                        .Foreground(dirty && !highContrast
                            ? Theme.SystemCaution
                            : Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1),
                    Button("Revert", RevertDraft)
                        .AccessKey("R")
                        .IsEnabled(dirty)
                        .Margin(16, 0, 0, 0)
                        .Grid(row: 0, column: 2),
                    Button("Save", SaveDraft)
                        .AccessKey("S")
                        .IsEnabled(dirty)
                        .ApplyStyle("AccentButtonStyle")
                        .Margin(8, 0, 0, 0)
                        .Grid(row: 0, column: 3),
                ])
            .Margin(24, 20, 24, 16);

        var removeHot = dragSession?.Zone == ShellDropZone.Remove;
        var panelBackground = removeHot
            ? highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.SystemCriticalBackground
            : cardFill;
        var panelStroke = removeHot
            ? highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.SystemCritical
            : cardStroke;

        var panel = (Border(scrollBody) with { CornerRadius = 8 })
            .Background(panelBackground)
            .WithBorder(panelStroke, removeHot || highContrast ? 2 : 1)
            .AutomationName("Shell editor")
            .Landmark(AutomationLandmarkType.Main)
            .Ref(removalPanelRef)
            .Margin(24, 0, 24, 16);

        var trayScroller = ScrollView(leftTarget)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Ref(trayScrollerRef)
            .Margin(0, 12, 0, 0);
        var rightTrayScroller = ScrollView(rightTarget)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Ref(rightTrayScrollerRef)
            .Margin(8, 12, 0, 0);
        var trayColumns = Grid(
            [GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                trayScroller.Grid(row: 0, column: 0),
                rightTrayScroller.Grid(row: 0, column: 1),
            ]);
        var zoneCaptions = Grid(
            [GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                Caption("Left tray")
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 0),
                Caption("Right tray")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right)
                    .Grid(row: 0, column: 1),
            ]);
        var trayFooter = Border(
                FlexColumn(
                    Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        [
                            Subtitle("Tray")
                                .HeadingLevel(AutomationHeadingLevel.Level2)
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 0),
                            Button(TrayLabel(selectedTrayId, out _), OpenMonitorPicker)
                                .Ref(monitorButtonRef)
                                .AutomationName("Choose display")
                                .HelpText("Choose which display's trays to edit.")
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 1),
                        ]),
                    zoneCaptions.Margin(0, 8, 0, 0),
                    Body("Drag module icons into either tray to add them. The right tray sits left of the system clock and notification area. An insertion line shows the pending position; drop a Tray icon onto the Modules panel to remove it.")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 4, 0, 0),
                    trayColumns))
            .Padding(24, 12, 24, 16)
            .CornerRadius(8)
            .Margin(24, 0, 24, 24)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.CardBackground)
            .WithBorder(highContrast
                ? Theme.Ref("SystemColorWindowTextColorBrush")
                : Theme.CardStroke, highContrast ? 2 : 1);

        // The monitor picker: a scaled preview of the physical display layout
        // (friendly names, never display numbers) plus rows for configured
        // trays whose display is currently disconnected or otherwise not
        // reachable through a tile. Picking one switches the tray being
        // edited; selection alone never dirties the draft.
        Element BuildMonitorPicker()
        {
            var cardFill = highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.CardBackground;
            var cardStroke = highContrast ? Theme.Ref("SystemColorWindowTextColorBrush") : Theme.CardStroke;
            var tileStroke = highContrast ? Theme.Ref("SystemColorWindowTextColorBrush") : Theme.ControlStroke;
            var selectedStroke = highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.Accent;

            var displays = DisplayTopology.Displays
                .OrderBy(d => d.Rect.Left)
                .ThenBy(d => d.Rect.Top)
                .ToArray();
            var content = new List<Element>();

            if (displays.Length == 0)
            {
                content.Add(Body("No displays detected.")
                    .Foreground(Theme.SecondaryText));
            }
            else
            {
                var originX = displays.Min(d => d.Rect.Left);
                var originY = displays.Min(d => d.Rect.Top);
                var virtualWidth = displays.Max(d => d.Rect.Right) - originX;
                var virtualHeight = displays.Max(d => d.Rect.Bottom) - originY;
                const double maxWidth = 320, maxHeight = 180;
                var scale = Math.Min(
                    maxWidth / Math.Max(1, virtualWidth),
                    maxHeight / Math.Max(1, virtualHeight));

                var tiles = new List<Element>();
                foreach (var display in displays)
                {
                    var trayId = TrayIdForDisplay(display);
                    var selected = trayId == selectedTrayId;
                    var lines = new List<Element>
                    {
                        BodyStrong(display.FriendlyName)
                            .FontSize(11)
                            .MaxLines(1)
                            .TextTrimming(Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis)
                            .HAlign(HorizontalAlignment.Center),
                        Caption($"{display.Rect.Width}×{display.Rect.Height}")
                            .HAlign(HorizontalAlignment.Center),
                    };
                    if (display.IsPrimary)
                        lines.Add(Caption("Primary")
                            .Foreground(selectedStroke)
                            .HAlign(HorizontalAlignment.Center));
                    else if (!display.HasTaskbar)
                        lines.Add(Caption("No taskbar")
                            .Foreground(highContrast ? Theme.PrimaryText : Theme.SystemCaution)
                            .HAlign(HorizontalAlignment.Center));

                    var tooltip = $"{display.FriendlyName} — {display.Rect.Width}×{display.Rect.Height}" +
                        (display.IsPrimary ? " (Primary)" : "") +
                        (display.HasTaskbar
                            ? ""
                            : "; its taskbar is hidden, so shells fall back to another display");
                    tiles.Add(Border(
                            VStack(2, lines.ToArray())
                                .HAlign(HorizontalAlignment.Center)
                                .VAlign(VerticalAlignment.Center)
                                .Margin(4, 0, 4, 0))
                        .Width(Math.Max(44, display.Rect.Width * scale))
                        .Height(Math.Max(32, display.Rect.Height * scale))
                        .CornerRadius(4)
                        .Background(selected ? Theme.SubtleFill : Theme.LayerFill)
                        .WithBorder(selected ? selectedStroke : tileStroke, selected ? 2 : 1)
                        .Canvas(
                            (display.Rect.Left - originX) * scale,
                            (display.Rect.Top - originY) * scale)
                        .AutomationName($"Edit the tray on {display.FriendlyName}")
                        .HelpText(tooltip)
                        .IsTabStop(true)
                        .OnTapped((_, _) => SelectTray(trayId))
                        .OnKeyDown((_, args) =>
                        {
                            if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                            {
                                SelectTray(trayId);
                                args.Handled = true;
                            }
                        }));
                }
                content.Add(Canvas(tiles.ToArray())
                    .Width(virtualWidth * scale)
                    .Height(virtualHeight * scale)
                    .HAlign(HorizontalAlignment.Left));
            }

            // Configured trays not reachable through a tile: disconnected
            // displays (still editable; they fall back at runtime) and
            // identity/edge overlaps shadowed by the primary tile. Rows are
            // per display: a display's left and right trays edit together.
            var tileKeys = displays.Select(d => TrayIdForDisplay(d).MonitorKey).ToHashSet();
            var extras = draft
                .Where(tray => !tileKeys.Contains(tray.Id.MonitorKey))
                .GroupBy(tray => tray.Id.MonitorKey)
                .Select(group => group.ToArray())
                .ToArray();
            if (extras.Length > 0)
            {
                content.Add(Caption("Other configured trays")
                    .Foreground(Theme.SecondaryText));
                foreach (var group in extras)
                {
                    var trayId = group[0].Id with { Edge = TrayEdge.Left };
                    var label = TrayLabel(trayId, out var connected);
                    var hasRight = group.Any(tray => tray.Id.Edge == TrayEdge.Right);
                    var status = !connected
                        ? "Disconnected"
                        : hasRight
                            ? "Left + right tray"
                            : "Connected";
                    var selected = trayId.MonitorKey == selectedTrayId.MonitorKey;
                    var rowContent = new List<Element>
                    {
                        VStack(0,
                                BodyStrong(label),
                                Caption(status).Foreground(connected
                                    ? Theme.SecondaryText
                                    : highContrast ? Theme.PrimaryText : Theme.SystemCaution))
                            .VAlign(VerticalAlignment.Center),
                    };
                    if (!connected)
                        rowContent.Insert(0, BodyStrong("⚠")
                            .Foreground(highContrast ? Theme.PrimaryText : Theme.SystemCaution)
                            .VAlign(VerticalAlignment.Center));
                    content.Add(Border(HStack(10, rowContent.ToArray()))
                        .Padding(10, 6, 10, 6)
                        .CornerRadius(4)
                        .Background(selected ? Theme.SubtleFill : Theme.LayerFill)
                        .WithBorder(selected ? selectedStroke : tileStroke, selected ? 2 : 1)
                        .HAlign(HorizontalAlignment.Stretch)
                        .AutomationName($"Edit the trays on {label} ({status})")
                        .IsTabStop(true)
                        .OnTapped((_, _) => SelectTray(trayId))
                        .OnKeyDown((_, args) =>
                        {
                            if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                            {
                                SelectTray(trayId);
                                args.Handled = true;
                            }
                        }));
                }
            }

            var picker = Border(FlexColumn(content.ToArray()))
                .Padding(16)
                .CornerRadius(8)
                .Background(cardFill)
                .WithBorder(cardStroke, highContrast ? 2 : 1);
            return Popup(picker, monitorPickerOpen, () => setMonitorPickerOpen(false))
                .Offset(monitorPickerOffset.X, monitorPickerOffset.Y)
                .IsLightDismissEnabled(true);
        }


        var trimmedDir = dirText.Trim();
        var dirChanged = trimmedDir.Length > 0 &&
            !string.Equals(trimmedDir, appliedDir, StringComparison.OrdinalIgnoreCase);
        var windowFill = Theme.Ref("SystemColorWindowColorBrush");
        var windowText = Theme.Ref("SystemColorWindowTextColorBrush");

        var configRow = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(dirText, value => setDirText(value),
                        placeholderText: "Folder containing config.json")
                    .AutomationName("Configuration folder")
                    .HelpText("Folder that contains Pagurian's config.json file")
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                Button("Browse…", Browse)
                    .Margin(8, 0, 0, 0)
                    .Grid(row: 0, column: 1),
            ]);

        var configMeta = Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    Caption($"Active file: {Path.Combine(appliedDir, "config.json")}")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    HStack(8,
                            Button("Reset", () => setDirText(HostSettings.DefaultConfigDir)),
                            Button("Apply", ApplyDir)
                                .IsEnabled(dirChanged)
                                .ApplyStyle("AccentButtonStyle"))
                        .Grid(row: 0, column: 1),
                ])
            .Margin(0, 12, 0, 0);

        var configCard = Border(
                FlexColumn(
                    Subtitle("Configuration folder")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Body("Choose where Pagurian stores its tray configuration.")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 4, 0, 0),
                    configRow.Margin(0, 12, 0, 0),
                    configMeta))
            .Padding(16)
            .CornerRadius(8)
            .Background(highContrast ? windowFill : Theme.CardBackground)
            .WithBorder(highContrast ? windowText : Theme.CardStroke, highContrast ? 2 : 1)
            .Landmark(AutomationLandmarkType.Form);

        var generalPage = ScrollView(
                Border(
                    FlexColumn(
                        Title("Settings")
                            .HeadingLevel(AutomationHeadingLevel.Level1),
                        Body("Configure Pagurian host settings.")
                            .TextWrapping(TextWrapping.WrapWholeWords)
                            .Foreground(Theme.SecondaryText)
                            .Margin(0, 8, 0, 0),
                        configCard.Margin(0, 24, 0, 0)))
                .Padding(24))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Landmark(AutomationLandmarkType.Main);

        var shellPage = Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [
                    header.Grid(row: 0, column: 0),
                    panel.Grid(row: 1, column: 0),
                    trayFooter.Grid(row: 2, column: 0),
                ])
            // A transparent hit-test surface lets the whole Shells page act
            // as one drop target without painting over the window backdrop.
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .Ref(shellPageRef)
            .OnDragEnter(UpdateDragTarget)
            .OnDragOver(UpdateDragTarget)
            .OnDragLeave(_ => ClearDragSession())
            .OnDrop(args =>
            {
                if (TryResolveDrag(
                        args.Data,
                        args.Position,
                        out var resolved,
                        out _,
                        out _))
                {
                    CommitDrop(resolved);
                }
                else
                {
                    EndTrayDrag();
                    ClearDragSession();
                }
            });

        var discardDialog = ContentDialog(
            "Discard changes?",
            Body("Your Shell changes have not been saved."),
            "Discard changes") with
        {
            IsOpen = discardDialogOpen,
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                setDiscardDialogOpen(false);
                if (result == ContentDialogResult.Primary)
                    SettingsWindow.CloseWithoutPrompt();
            },
        };

        var selectedTag = page == SettingsPage.Shells ? "shells" : "settings";
        var navigation = NavigationView(
            [
                NavItem("Shells", icon: "", tag: "shells"),
                NavItem("Settings", icon: "", tag: "settings"),
            ],
            page == SettingsPage.Shells ? shellPage : generalPage) with
        {
            SelectedTag = selectedTag,
            OnSelectedTagChanged = tag =>
            {
                var nextPage = string.Equals(
                    tag,
                    "shells",
                    StringComparison.OrdinalIgnoreCase)
                    ? SettingsPage.Shells
                    : SettingsPage.General;
                if (nextPage != SettingsPage.Shells)
                {
                    EndTrayDrag();
                    ClearDragSession();
                    setMonitorPickerOpen(false);
                }
                _navigationDiagnostics.Record($"page-request source=navigation-view target={nextPage}");
                setPage(nextPage);
            },
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            PaneTitle = "Pagurian",
            IsPaneOpen = true,
            IsSettingsVisible = false,
            OpenPaneLength = 240,
        };

        Element root = Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    TitleBar("Pagurian")
                        .Grid(row: 0, column: 0),
                    navigation
                        .Landmark(AutomationLandmarkType.Navigation)
                        .Grid(row: 1, column: 0),
                    discardDialog.Grid(row: 1, column: 0),
                    BuildMonitorPicker().Grid(row: 1, column: 0),
                ])
            .Ref(windowRootRef)
            .OnKeyDown((_, args) =>
            {
                if (page != SettingsPage.Shells ||
                    args.Key != VirtualKey.Escape ||
                    discardDialogOpen)
                    return;

                args.Handled = true;
                if (dragSessionRef.Current != null || activeTrayDragRef.Current != null)
                {
                    EndTrayDrag();
                    ClearDragSession();
                    return;
                }

                SettingsWindow.RequestClose();
            })
            .Backdrop(BackdropKind.MicaAlt);

        return root;
    }

    private sealed class DraftTray
    {
        internal required TrayId Id { get; init; }
        internal required List<TrayConfig.Entry> Entries { get; set; }
    }

    private static List<DraftTray> LoadDraftTrays() =>
        TrayConfig.Load()
            .Select(group => new DraftTray { Id = group.Id, Entries = group.Entries.ToList() })
            .ToList();

    private static Element TargetChip(
        TrayConfig.Entry entry,
        bool selected,
        Action onSelected,
        Action onRemove,
        Action onDragStarted,
        Action onDragEnd,
        int position,
        int setSize,
        bool highContrast,
        double scale,
        double opacity)
    {
        var known = ModuleLoader.TryGetKind(entry.ShellType, out _);
        var name = NameFor(entry.ShellType);
        var tip = known
            ? $"{name}  ·  #{entry.Id}"
            : $"{name}  ·  #{entry.Id} (module not loaded)";
        var windowFill = Theme.Ref("SystemColorWindowColorBrush");
        var windowText = Theme.Ref("SystemColorWindowTextColorBrush");
        var highlight = Theme.Ref("SystemColorHighlightColorBrush");
        var fill = highContrast
            ? windowFill
            : !known
                ? Theme.SystemCriticalBackground
                : selected ? Theme.SubtleFill : Theme.ControlFill;
        var stroke = highContrast
            ? selected ? highlight : windowText
            : selected ? Theme.Accent : Theme.ControlStroke;

        var chipPanelBase = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [ShellIconVisual(entry.ShellType, highContrast, scale)])
            .Background(fill);

        var chipBase = WithThresholdDrag((Border(chipPanelBase) with { CornerRadius = 4 * scale })
            .Width(ChipSize * scale)
            .Height(ChipSize * scale)
            .WithBorder(stroke, highContrast || selected ? 2 : known ? 1 : 0)
            .HelpText($"{tip}. Drag to reorder or remove. Press Delete to remove.")
            .AutomationName($"Configure {name}, shell {entry.Id}")
            .PositionInSet(position, setSize)
            .IsTabStop(true)
            .OnTapped((_, args) =>
            {
                args.Handled = true;
                onSelected();
            })
            .OnKeyDown((_, args) =>
            {
                if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    onSelected();
                    args.Handled = true;
                }
                else if (args.Key is VirtualKey.Delete or VirtualKey.Back)
                {
                    onRemove();
                    args.Handled = true;
                }
            })
            .OnDragStart(
                () =>
                {
                    onDragStarted();
                    return ShellDragPayload.ForInstance(entry.Id);
                },
                DragOperations.Move,
                _ => onDragEnd()));

        var tooltip = $"{tip}\nDrag to reorder, or drop onto the Modules panel to remove.";
        Element chip = WithInstantTooltip(chipBase, tooltip);
        if (opacity <= 0)
            chip = chip.Opacity(0).AccessibilityHidden();
        else if (opacity < 1)
            chip = chip.Opacity(opacity);
        return chip.WithKey(entry.Id);
    }

    private static Element ShellConfigurationPanel(
        TrayConfig.Entry entry,
        IThemeService configurationTheme,
        bool highContrast,
        Action<JsonElement?> setSettings)
    {
        Element content;
        if (!ModuleLoader.TryGetKind(entry.ShellType, out var kind))
        {
            content = FlexColumn(
                BodyStrong("Configuration unavailable")
                    .Foreground(highContrast ? Theme.PrimaryText : Theme.SystemCritical),
                Body("The module that owns this shell is not loaded. Its existing settings will be preserved.")
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 8, 0, 0));
        }
        else if (kind.ConfigurationView == null)
        {
            content = Body("No configurable settings.")
                .Foreground(Theme.SecondaryText);
        }
        else
        {
            var props = new ShellConfigurationProps(
                entry.Id,
                entry.Settings,
                setSettings,
                configurationTheme,
                Logger.For($"{entry.ShellType}#{entry.Id}"));
            content = new ComponentElement(kind.ConfigurationView, props)
                .WithKey($"configuration:{entry.ShellType}:{entry.Id}");
        }

        return Border(content)
            .Padding(16)
            .CornerRadius(8)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.LayerFill)
            .WithBorder(
                highContrast
                    ? Theme.Ref("SystemColorWindowTextColorBrush")
                    : Theme.SurfaceStroke,
                highContrast ? 2 : 1);
    }

    private static Element ModuleCard(
        PagurianModule module,
        List<ShellAttribute> kinds,
        Action onDragEnd,
        bool highContrast)
    {
        var tiles = kinds
            .Select((kind, index) =>
            {
                var kindId = kind.ShellType.FullName!;
                var name = kind.DisplayName.Length > 0
                    ? kind.DisplayName
                    : ShortName(kindId);
                var dragIcon = WithThresholdDrag(WithInstantTooltip(Border(
                        Image(kind.PreviewIconPath ?? AppAssets.ModuleFallbackIconPath)
                            .Width(24)
                            .Height(24)
                            .AccessibilityHidden()
                            .HAlign(HorizontalAlignment.Center)
                            .VAlign(VerticalAlignment.Center))
                    .Width(44)
                    .Height(44)
                    .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                    .AccessibilityHidden()
                    .OnDragStart(
                        () => ShellDragPayload.ForKind(kindId),
                        DragOperations.Copy,
                        _ => onDragEnd()),
                    $"Drag {name} to the Tray to add it."));
                return (Element)Border(
                        Grid(
                            [GridSize.Auto, GridSize.Auto],
                            [GridSize.Auto],
                            [
                                dragIcon.Grid(row: 0, column: 0),
                                FlexColumn(
                                        BodyStrong(name)
                                            .TextWrapping(TextWrapping.WrapWholeWords),
                                        Caption("Drag to add")
                                            .Foreground(Theme.SecondaryText)
                                            .Margin(0, 4, 0, 0))
                                    .Margin(12, 0, 0, 0)
                                    .Grid(row: 0, column: 1),
                            ]))
                    .Padding(12)
                    .CornerRadius(4)
                    .HAlign(HorizontalAlignment.Left)
                    .Background(highContrast
                        ? Theme.Ref("SystemColorWindowColorBrush")
                        : Theme.ControlFill)
                    .WithBorder(
                        highContrast
                            ? Theme.Ref("SystemColorWindowTextColorBrush")
                            : Theme.ControlStroke,
                        highContrast ? 2 : 1)
                    .AutomationName($"Drag {name} to tray")
                    .HelpText("Drag the icon to the Tray to add this Shell.")
                    .PositionInSet(index + 1, kinds.Count)
                    .WithKey(module.Id + ":" + kindId);
            })
            .ToArray();

        var shellTiles = HStack(8, tiles)
            .HAlign(HorizontalAlignment.Left);

        var moduleContent = VStack(
                BodyStrong(module.DisplayName.Length > 0
                        ? module.DisplayName
                        : ShortName(module.Id))
                    .HAlign(HorizontalAlignment.Left),
                shellTiles.Margin(0, 12, 0, 0))
            .HAlign(HorizontalAlignment.Left);

        return Border(
                moduleContent)
            .Padding(16)
            .CornerRadius(8)
            .HAlign(HorizontalAlignment.Left)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.SubtleFill)
            .WithBorder(
                highContrast
                    ? Theme.Ref("SystemColorWindowTextColorBrush")
                    : Theme.CardStroke,
                highContrast ? 2 : 1)
            .Flex(grow: 0, shrink: 1, alignSelf: FlexAlign.FlexStart)
            .WithKey(module.Id);
    }

    private static Element ShellIconVisual(
        string kindId,
        bool highContrast,
        double scale)
    {
        var known = ModuleLoader.TryGetKind(kindId, out _);
        Element baseIcon = Image(IconFor(kindId))
            .Width(24 * scale)
            .Height(24 * scale)
            .AccessibilityHidden()
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        Element icon = baseIcon;
        if (!known)
        {
            Element triangle = BodyStrong("\u25B2")
                .FontSize(26 * scale)
                .Foreground(highContrast
                    ? Theme.Ref("SystemColorHighlightColorBrush")
                    : Theme.SystemCaution)
                .AccessibilityHidden()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
            Element exclamation = BodyStrong("!")
                .FontSize(12 * scale)
                .Foreground(highContrast
                    ? Theme.Ref("SystemColorWindowTextColorBrush")
                    : Theme.PrimaryText)
                .Margin(0, 4 * scale, 0, 0)
                .AccessibilityHidden()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
            if (!highContrast)
            {
                triangle = triangle.Opacity(0.76);
                exclamation = exclamation.Opacity(0.76);
            }

            icon = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [baseIcon, triangle, exclamation]);
        }

        return icon;
    }

    private static Element WithInstantTooltip(Element element, string text) =>
        element
            .OnMountAdd(frameworkElement => AttachInstantTooltip(frameworkElement, text))
            .OnUnmountAdd(DetachInstantTooltip);

    private static Element WithThresholdDrag(Element element) =>
        element
            .OnMountAdd(AttachThresholdDrag)
            .OnUnmountAdd(DetachThresholdDrag);

    private static void AttachThresholdDrag(FrameworkElement element)
    {
        DetachThresholdDrag(element);
        var binding = new ThresholdDragBinding(element);
        ThresholdDragBindings.Add(element, binding);
        binding.Attach();
    }

    private static void DetachThresholdDrag(FrameworkElement element)
    {
        if (!ThresholdDragBindings.TryGetValue(element, out var binding))
            return;

        binding.Detach();
        ThresholdDragBindings.Remove(element);
    }

    private static void AttachInstantTooltip(FrameworkElement element, string text)
    {
        DetachInstantTooltip(element);
        var binding = new InstantTooltipBinding(text);
        InstantTooltipBindings.Add(element, binding);
        MountedInstantTooltipBindings.Add(binding);
        ToolTipService.SetToolTip(element, binding.ToolTip);
        binding.IsActive = true;
        element.PointerEntered += binding.PointerEntered;
        element.PointerExited += binding.PointerExited;
        element.DragStarting += binding.DragStarting;
        binding.ToolTip.Opened += binding.Opened;
        if (InstantTooltipsSuppressed)
            binding.Close();
    }

    private static void DetachInstantTooltip(FrameworkElement element)
    {
        if (!InstantTooltipBindings.TryGetValue(element, out var binding))
            return;

        binding.IsActive = false;
        element.PointerEntered -= binding.PointerEntered;
        element.PointerExited -= binding.PointerExited;
        element.DragStarting -= binding.DragStarting;
        binding.ToolTip.Opened -= binding.Opened;
        binding.Close();
        ToolTipService.SetToolTip(element, null);
        MountedInstantTooltipBindings.Remove(binding);
        InstantTooltipBindings.Remove(element);
    }

    private static bool InstantTooltipsSuppressed =>
        _instantTooltipSuppressionDepth > 0;

    private static void SuspendInstantTooltips()
    {
        _instantTooltipSuppressionDepth++;
        CloseInstantTooltips();
    }

    private static void ResumeInstantTooltips()
    {
        if (_instantTooltipSuppressionDepth > 0)
            _instantTooltipSuppressionDepth--;

        // PointerExited is not guaranteed while the system drag owns pointer
        // routing, so close every mounted tooltip once more at completion.
        CloseInstantTooltips();
    }

    private static void CloseInstantTooltips()
    {
        foreach (var binding in MountedInstantTooltipBindings)
            binding.Close();
    }

    private static bool TryGetPayload(DragData data, out ShellDragPayload payload)
    {
        if (data.TryGetTypedPayload<ShellDragPayload>(out var typed) && typed != null)
        {
            payload = typed;
            return true;
        }

        payload = null!;
        return false;
    }

    private static string IconFor(string kindId) =>
        ModuleLoader.TryGetKind(kindId, out var kind) && kind.PreviewIconPath is { } path
            ? path
            : AppAssets.ModuleFallbackIconPath;

    private static string NameFor(string kindId) =>
        ModuleLoader.TryGetKind(kindId, out var kind) && kind.DisplayName.Length > 0
            ? kind.DisplayName
            : ShortName(kindId);

    private static string ShortName(string fullName)
    {
        var index = fullName.LastIndexOf('.');
        return index >= 0 ? fullName[(index + 1)..] : fullName;
    }

    private static async Task<string?> PickFolderAsync()
    {
        var windowId = SettingsWindow.AppWindowId;
        if (windowId == null)
            return null;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "Select",
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(
            picker,
            Win32Interop.GetWindowFromWindowId(windowId.Value));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
