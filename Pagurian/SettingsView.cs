using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
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
        int CandidateIndex);

    public override Element Render()
    {
        var (page, setPage) = UseState(SettingsWindow.RequestedPage);
        UseEffect(() =>
        {
            void OnPageRequested(SettingsPage requested) => setPage(requested);
            SettingsWindow.PageRequested += OnPageRequested;
            return () => SettingsWindow.PageRequested -= OnPageRequested;
        }, Array.Empty<object>());

        var colorScheme = UseColorScheme();
        var highContrastScheme = UseHighContrastScheme();
        var reduceMotion = UseReducedMotion();
        var (initialFocusRef, requestInitialFocus) = this.UseElementFocus();
        UseEffect(() =>
        {
            if (page == SettingsPage.Shells)
                requestInitialFocus();
        }, page);
        var highContrast = colorScheme == ColorScheme.HighContrast;

        var configurationTheme = UseMemo(
            () => new ConfigurationThemeService(colorScheme, highContrastScheme),
            Array.Empty<object>());
        UseEffect(
            () => configurationTheme.Apply(colorScheme, highContrastScheme),
            colorScheme,
            highContrastScheme ?? "");

        var initialDraft = UseMemo(() => TrayConfig.Load().ToList(), Array.Empty<object>());
        var (draft, setDraft) = UseState(initialDraft);
        var (dirty, setDirty) = UseState(false);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var (dragSession, setDragSession) = UseState<ShellDragSession?>(null);
        var (activeTrayDragId, setActiveTrayDragId) = UseState<string?>(null);
        var (discardDialogOpen, setDiscardDialogOpen) = UseState(false);
        var initialDir = UseMemo(() => HostSettings.ConfigDir, Array.Empty<object>());
        var (dirText, setDirText) = UseState(initialDir);
        var (appliedDir, setAppliedDir) = UseState(initialDir);

        var shellPageRef = this.UseElementRef<Grid>();
        var removalPanelRef = this.UseElementRef<Border>();
        var trayScrollerRef = this.UseElementRef<ScrollView>();
        var activeTrayDragRef = UseRef<string?>(null);
        var draftRef = UseRef(draft);
        var dragSessionRef = UseRef<ShellDragSession?>(dragSession);
        var autoScrollDirectionRef = UseRef(0);
        var lastTrayPointerXRef = UseRef(0d);
        var autoScrollTickRef = UseRef<Action>(() => { });
        draftRef.Current = draft;
        dragSessionRef.Current = dragSession;

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

        void BeginTrayDrag(string instanceId)
        {
            activeTrayDragRef.Current = instanceId;
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

            var entries = draftRef.Current;
            var originalIndex = payload.InstanceId == null
                ? -1
                : entries.FindIndex(entry => entry.Id == payload.InstanceId);
            if (payload.InstanceId != null && originalIndex < 0)
                return false;
            if (payload.KindId != null && !ModuleLoader.TryGetKind(payload.KindId, out _))
                return false;

            var root = shellPageRef.Current;
            var removalPanel = removalPanelRef.Current;
            var scroll = trayScrollerRef.Current;
            if (root == null || removalPanel == null || scroll == null)
                return false;

            try
            {
                var origin = scroll.TransformToVisual(root)
                    .TransformPoint(new Point(0, 0));
                trayPointerX = rootPoint.X - origin.X;
                var inTray = trayPointerX >= 0 &&
                    trayPointerX <= scroll.ActualWidth &&
                    rootPoint.Y >= origin.Y &&
                    rootPoint.Y <= origin.Y + scroll.ActualHeight;
                if (inTray)
                {
                    resolved = new ShellDragSession(
                        payload,
                        ShellDropZone.Tray,
                        CandidateIndexFor(
                            payload,
                            trayPointerX + scroll.HorizontalOffset,
                            entries));
                    operation = payload.InstanceId == null
                        ? DragOperations.Copy
                        : DragOperations.Move;
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
                        -1);
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
            var scroll = trayScrollerRef.Current;
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
            args.UIOverride.Caption = resolved.Zone == ShellDropZone.Remove
                ? "Remove from Tray"
                : resolved.Payload.InstanceId == null
                    ? "Add to Tray"
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
            var entries = draftRef.Current;
            var next = new List<TrayConfig.Entry>(entries);
            var changed = false;
            string? removedId = null;

            if (resolved.Zone == ShellDropZone.Remove &&
                resolved.Payload.InstanceId is { } removeId)
            {
                changed = next.RemoveAll(entry => entry.Id == removeId) > 0;
                removedId = changed ? removeId : null;
            }
            else if (resolved.Zone == ShellDropZone.Tray &&
                resolved.Payload.KindId is { } kindId)
            {
                next.Insert(
                    Math.Clamp(resolved.CandidateIndex, 0, next.Count),
                    new TrayConfig.Entry(
                        kindId,
                        TrayConfig.NextId(entries.Select(entry => entry.Id)),
                        null));
                changed = true;
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
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                ClearDragSession();
                return;
            }

            draftRef.Current = next;
            dragSessionRef.Current = null;
            void Commit()
            {
                setDraft(next);
                setDragSession(null);
                setDirty(true);
                if (removedId != null && selectedId == removedId)
                    setSelectedId(null);
            }

            if (reduceMotion)
                Commit();
            else
                Animations.Animate(AnimationKind.Spring, Commit);
        }

        autoScrollTickRef.Current = () =>
        {
            var resolved = dragSessionRef.Current;
            var scroll = trayScrollerRef.Current;
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
            SetDragSessionState(resolved with
            {
                CandidateIndex = CandidateIndexFor(
                    resolved.Payload,
                    lastTrayPointerXRef.Current + nextOffset,
                    draftRef.Current),
            });
        };

        void RemoveInstance(string instanceId)
        {
            var entries = draftRef.Current;
            var next = entries.Where(entry => entry.Id != instanceId).ToList();
            if (next.Count == entries.Count)
                return;

            draftRef.Current = next;
            setDraft(next);
            if (selectedId == instanceId)
                setSelectedId(null);
            setDirty(true);
        }

        void UpdateEntrySettings(string instanceId, JsonElement? settings)
        {
            if (settings is { } value && value.ValueKind != JsonValueKind.Object)
            {
                PagurianLog.HostError(
                    $"shell editor: configuration view for #{instanceId} returned non-object JSON");
                return;
            }

            var entries = draftRef.Current;
            var index = entries.FindIndex(entry => entry.Id == instanceId);
            if (index < 0)
                return;

            var next = new List<TrayConfig.Entry>(entries);
            next[index] = next[index] with
            {
                Settings = settings is { } replacement ? replacement.Clone() : null,
            };
            draftRef.Current = next;
            setDraft(next);
            setDirty(true);
        }

        void SaveDraft()
        {
            if (!dirty)
                return;

            var entries = draftRef.Current;
            try
            {
                TrayConfig.Save(entries);
                TrayShells.ApplyConfig(entries);
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
                $"settings: applied tray configuration ({entries.Count} shells)");
        }

        void RevertDraft()
        {
            var entries = TrayConfig.Load().ToList();
            draftRef.Current = entries;
            setDraft(entries);
            if (selectedId != null && entries.All(entry => entry.Id != selectedId))
                setSelectedId(null);
            setDirty(false);
        }

        void ApplyDir()
        {
            try
            {
                Directory.CreateDirectory(dirText.Trim());
                HostSettings.SetConfigDir(dirText.Trim());
                var entries = TrayConfig.Load().ToList();
                TrayShells.ApplyConfig(entries);
                setAppliedDir(HostSettings.ConfigDir);
                setDirText(HostSettings.ConfigDir);
                draftRef.Current = entries;
                setDraft(entries);
                if (selectedId != null && entries.All(entry => entry.Id != selectedId))
                    setSelectedId(null);
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

        // Drag hover never projects a new ordering. The original draft layout
        // remains stable; an active Tray source becomes transparent while a
        // non-layout insertion marker is overlaid between the remaining icons.
        var chips = draft
            .Select((entry, index) => TargetChip(
                entry,
                selectedId == entry.Id,
                () => setSelectedId(entry.Id),
                () => RemoveInstance(entry.Id),
                () => BeginTrayDrag(entry.Id),
                () =>
                {
                    EndTrayDrag();
                    ClearDragSession();
                },
                index + 1,
                draft.Count,
                highContrast,
                reduceMotion,
                targetVisualScale,
                activeTrayDragId == entry.Id))
            .ToArray();

        Element trayIcons = chips.Length == 0
            ? Caption("Drop shells here")
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
            : HStack(ChipGap * targetVisualScale, chips)
                .VAlign(VerticalAlignment.Center);

        Element targetContent = trayIcons;
        if (dragSession is { Zone: ShellDropZone.Tray } trayDrag)
        {
            var insertionIndex = Math.Clamp(
                trayDrag.CandidateIndex,
                0,
                draft.Count - (trayDrag.Payload.InstanceId == null ? 0 : 1));
            var insertionOffset = InsertionOffsetFor(
                trayDrag.Payload,
                insertionIndex,
                draft);
            var indicator = (Border(null) with
                {
                    CornerRadius = InsertionIndicatorWidth * targetVisualScale / 2,
                })
                .Width(InsertionIndicatorWidth * targetVisualScale)
                .Height(32 * targetVisualScale)
                .Margin(insertionOffset * targetVisualScale, 0, 0, 0)
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Center)
                .Background(highContrast
                    ? Theme.Ref("SystemColorHighlightColorBrush")
                    : Theme.Accent)
                .AccessibilityHidden()
                .WithKey("shell-insertion-indicator");
            targetContent = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [trayIcons, indicator]);
        }

        var targetHeightDip = (ChipSize + 8) * targetVisualScale;
        var stripHot = dragSession?.Zone == ShellDropZone.Tray;

        var targetFill = stripHot
            ? highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.SystemAttentionBackground
            : highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.LayerFill;
        var targetStroke = stripHot
            ? highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.Accent
            : highContrast ? Theme.Ref("SystemColorWindowTextColorBrush") : Theme.ControlStroke;
        var targetBorderThickness = stripHot || highContrast ? 2d : 1d;
        // Width/Height include BorderThickness and Padding. Reserve the
        // border inside the target so its 44-DIP drag surfaces do not clip.
        var targetPaddingX = Math.Max(
            0,
            StripPadX * targetVisualScale - targetBorderThickness);
        var targetPaddingY = Math.Max(
            0,
            4 * targetVisualScale - targetBorderThickness);

        var target = (Border(targetContent) with { CornerRadius = 4 * targetVisualScale })
            .MinWidth(EmptyTargetWidth * targetVisualScale)
            .Height(targetHeightDip)
            .Padding(targetPaddingX, targetPaddingY,
                targetPaddingX, targetPaddingY)
            .Background(targetFill)
            .WithBorder(targetStroke, targetBorderThickness)
            .AutomationName("Draft Tray drop target")
            .HelpText("Drop module icons here to add them. The insertion line shows the pending position. Drag existing icons to reorder, or onto the Modules panel to remove.")
            .IsTabStop(true)
            .Ref(initialFocusRef)
            .OnTapped((_, _) => setSelectedId(null))
            .OnKeyDown((_, args) =>
            {
                if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    setSelectedId(null);
                    args.Handled = true;
                }
            });

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

        var selectedEntry = selectedId == null
            ? null
            : draft.FirstOrDefault(entry => entry.Id == selectedId);

        Element panelBody;
        if (selectedEntry == null)
        {
            panelBody = FlexColumn(
                Subtitle("Modules")
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                Body("Drag a Shell icon to the Tray below to add it.")
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 4, 0, 0),
                catalogContent.Margin(0, 16, 0, 0));
        }
        else
        {
            var selectedName = NameFor(selectedEntry.ShellType);
            panelBody = FlexColumn(
                Grid(
                    [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                    [GridSize.Auto],
                    [
                        FlexColumn(
                                Subtitle($"{selectedName} configuration")
                                    .HeadingLevel(AutomationHeadingLevel.Level2),
                                Caption($"Shell #{selectedEntry.Id}")
                                    .Foreground(Theme.SecondaryText)
                                    .Margin(0, 4, 0, 0))
                            .Grid(row: 0, column: 0),
                        Button("Remove", () => RemoveInstance(selectedEntry.Id))
                            .Grid(row: 0, column: 1),
                        Button("Back to modules", () => setSelectedId(null))
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

        var trayScroller = ScrollView(target)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Ref(trayScrollerRef)
            .Margin(0, 12, 0, 0);
        var trayFooter = Border(
                FlexColumn(
                    Subtitle("Tray")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Body("Drag module icons here to add them. An insertion line shows the pending position without moving existing icons. Drop a Tray icon onto the Modules panel to remove it.")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 4, 0, 0),
                    trayScroller))
            .Padding(24, 12, 24, 16)
            .CornerRadius(8)
            .Margin(24, 0, 24, 24)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.CardBackground)
            .WithBorder(highContrast
                ? Theme.Ref("SystemColorWindowTextColorBrush")
                : Theme.CardStroke, highContrast ? 2 : 1);

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
                }
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
                ])
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
        bool reduceMotion,
        double scale,
        bool hidden)
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
        Element chipPanel = !reduceMotion && !highContrast
            ? chipPanelBase.BackgroundTransition()
            : chipPanelBase;

        var chipBase = WithThresholdDrag((Border(chipPanel) with { CornerRadius = 4 * scale })
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
        if (hidden)
            chip = chip.Opacity(0).AccessibilityHidden();
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
                        Image(kind.PreviewIconPath ?? AppAssets.IconPath)
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
            : AppAssets.IconPath;

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
