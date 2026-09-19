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
using Pagurian.Sdk;
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

    private const double StripPadX = 12;
    private const double ChipSize = 40;
    private const double ChipGap = 4;
    private const double ChipPitch = ChipSize + ChipGap;
    private const double EmptyTargetWidth = 160;

    private sealed class InstantTooltipBinding
    {
        public InstantTooltipBinding(string text)
        {
            ToolTip = new ToolTip { Content = text };
            PointerEntered = OnPointerEntered;
            PointerExited = OnPointerExited;
            DragStarting = OnDragStarting;
        }

        public ToolTip ToolTip { get; }
        public bool IsActive { get; set; }
        public Microsoft.UI.Xaml.Input.PointerEventHandler PointerEntered { get; }
        public Microsoft.UI.Xaml.Input.PointerEventHandler PointerExited { get; }
        public Windows.Foundation.TypedEventHandler<UIElement, DragStartingEventArgs> DragStarting { get; }

        private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
            SetOpen(sender, true);

        private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
            SetOpen(sender, false);

        private void OnDragStarting(UIElement sender, DragStartingEventArgs args) =>
            SetOpen(sender, false);

        private void SetOpen(object sender, bool isOpen)
        {
            if (!IsActive ||
                sender is not FrameworkElement element ||
                !InstantTooltipBindings.TryGetValue(element, out var current) ||
                !ReferenceEquals(current, this))
            {
                return;
            }

            ToolTip.IsOpen = isOpen;
        }
    }

    private sealed record ShellDragPayload(string? KindId, string? InstanceId)
    {
        public static ShellDragPayload ForKind(string kindId) => new(kindId, null);
        public static ShellDragPayload ForInstance(string instanceId) => new(null, instanceId);
    }

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
        var (stripHot, setStripHot) = UseState(false);
        var (panelHot, setPanelHot) = UseState(false);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        var (preview, setPreview) = UseState<(int Index, string? InstanceId)?>(null);
        var (discardDialogOpen, setDiscardDialogOpen) = UseState(false);
        var initialDir = UseMemo(() => HostSettings.ConfigDir, Array.Empty<object>());
        var (dirText, setDirText) = UseState(initialDir);
        var (appliedDir, setAppliedDir) = UseState(initialDir);

        UseClosingGuard(() =>
        {
            if (SettingsWindow.AllowClose || !dirty)
                return true;

            setDiscardDialogOpen(true);
            return false;
        });

        void ClearPreview()
        {
            if (preview != null)
                setPreview(null);
        }

        const double targetVisualScale = 1;

        int InsertIndexFor(double x) =>
            Math.Clamp(
                (int)Math.Floor(
                    (x / targetVisualScale - StripPadX) / ChipPitch),
                0,
                draft.Count);

        void DropOnTarget(ShellDragPayload payload, double x)
        {
            var index = InsertIndexFor(x);
            if (payload.InstanceId != null)
            {
                var from = draft.FindIndex(entry => entry.Id == payload.InstanceId);
                if (from < 0)
                    return;

                var insertAt = from < index ? index - 1 : index;
                if (insertAt == from)
                    return;

                var next = new List<TrayConfig.Entry>(draft);
                var item = next[from];
                next.RemoveAt(from);
                next.Insert(Math.Clamp(insertAt, 0, next.Count), item);
                setDraft(next);
                setDirty(true);
            }
            else if (payload.KindId != null)
            {
                var next = new List<TrayConfig.Entry>(draft);
                next.Insert(index, new TrayConfig.Entry(
                    payload.KindId,
                    TrayConfig.NextId(draft.Select(entry => entry.Id)),
                    null));
                setDraft(next);
                setDirty(true);
            }
        }

        void AddKind(string kindId)
        {
            var next = new List<TrayConfig.Entry>(draft)
            {
                new(
                    kindId,
                    TrayConfig.NextId(draft.Select(entry => entry.Id)),
                    null),
            };
            setDraft(next);
            setDirty(true);
        }

        void RemoveInstance(string instanceId)
        {
            var next = draft.Where(entry => entry.Id != instanceId).ToList();
            if (next.Count == draft.Count)
                return;

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

            var index = draft.FindIndex(entry => entry.Id == instanceId);
            if (index < 0)
                return;

            var next = new List<TrayConfig.Entry>(draft);
            next[index] = next[index] with
            {
                Settings = settings is { } replacement ? replacement.Clone() : null,
            };
            setDraft(next);
            setDirty(true);
        }

        void SaveDraft()
        {
            if (!dirty)
                return;

            try
            {
                TrayConfig.Save(draft);
                TrayShells.ApplyConfig(draft);
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
                $"settings: applied tray configuration ({draft.Count} shells)");
        }

        void RevertDraft()
        {
            var entries = TrayConfig.Load().ToList();
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

        // The draft tray uses ordinary window DIPs. It no longer mirrors the
        // physical taskbar's position or per-monitor scale.
        var chips = new List<Element>();
        for (var i = 0; i <= draft.Count; i++)
        {
            if (preview is { } currentPreview && currentPreview.Index == i)
                chips.Add(GhostSlot(highContrast, targetVisualScale));
            if (i >= draft.Count)
                continue;

            var entry = draft[i];
            chips.Add(TargetChip(
                entry,
                selectedId == entry.Id,
                () => setSelectedId(entry.Id),
                () => RemoveInstance(entry.Id),
                ClearPreview,
                i + 1,
                draft.Count,
                highContrast,
                reduceMotion,
                targetVisualScale,
                preview?.InstanceId == entry.Id));
        }

        Element targetContent = chips.Count == 0
            ? Caption("Drop shells here")
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
            : HStack(ChipGap * targetVisualScale, chips.ToArray())
                .VAlign(VerticalAlignment.Center);
        if (!reduceMotion && chips.Count > 0)
            targetContent = targetContent.SpringLayoutAnimation();

        var targetSlots = draft.Count + (preview == null ? 0 : 1);
        var targetWidthDesignDip = Math.Max(
            EmptyTargetWidth,
            2 * StripPadX + targetSlots * ChipSize + Math.Max(0, targetSlots - 1) * ChipGap);
        var targetWidthDip = targetWidthDesignDip * targetVisualScale;
        var targetHeightDip = TaskbarTrayWindow.ContentHeightDip * targetVisualScale;

        var targetFill = stripHot
            ? highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.SystemAttentionBackground
            : highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.LayerFill;
        var targetStroke = stripHot
            ? highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.Accent
            : highContrast ? Theme.Ref("SystemColorWindowTextColorBrush") : Theme.ControlStroke;
        var targetBorderThickness = stripHot || highContrast ? 2d : 1d;
        // Width/Height include BorderThickness and Padding. Reserve the
        // border inside the fixed taskbar-sized target so its 40-DIP chips do
        // not overflow and get clipped along the right or bottom edge.
        var targetPaddingX = Math.Max(
            0,
            StripPadX * targetVisualScale - targetBorderThickness);
        var targetPaddingY = Math.Max(
            0,
            2 * targetVisualScale - targetBorderThickness);

        var target = (Border(targetContent) with { CornerRadius = 4 * targetVisualScale })
            .Width(targetWidthDip)
            .Height(targetHeightDip)
            .Padding(targetPaddingX, targetPaddingY,
                targetPaddingX, targetPaddingY)
            .Background(targetFill)
            .WithBorder(targetStroke, targetBorderThickness)
            .AutomationName("Target taskbar tray")
            .HelpText("Drop shell icons here. Drag existing icons to reorder them.")
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
            })
            .OnDragEnter(_ => setStripHot(true))
            .OnDragLeave(_ =>
            {
                setStripHot(false);
                ClearPreview();
            })
            .OnDragOver(args =>
            {
                if (!TryGetPayload(args.Data, out var payload))
                    return;

                args.AcceptedOperation = payload.InstanceId != null
                    ? DragOperations.Move
                    : DragOperations.Copy;
                var index = InsertIndexFor(args.Position.X);
                if (preview == null || preview.Value.Index != index ||
                    preview.Value.InstanceId != payload.InstanceId)
                {
                    setPreview((index, payload.InstanceId));
                }
            })
            .OnDrop(args =>
            {
                setStripHot(false);
                ClearPreview();
                if (TryGetPayload(args.Data, out var payload))
                    DropOnTarget(payload, args.Position.X);
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
                AddKind,
                ClearPreview,
                highContrast))
            .ToArray();

        Element catalogContent = moduleCards.Length == 0
            ? Body("No modules loaded")
                .Foreground(Theme.SecondaryText)
            : (FlexRow(moduleCards) with
            {
                Wrap = FlexWrap.Wrap,
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
                Body("Drag a shell to the Tray below for exact placement, or click it to append.")
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
                            Caption("Choose modules and arrange the tray below, then save and exit.")
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

        var panelBackground = panelHot
            ? highContrast ? Theme.Ref("SystemColorWindowColorBrush") : Theme.SystemCriticalBackground
            : cardFill;
        var panelStroke = panelHot
            ? highContrast ? Theme.Ref("SystemColorHighlightColorBrush") : Theme.SystemCritical
            : cardStroke;

        var panel = (Border(scrollBody) with { CornerRadius = 8 })
            .Background(panelBackground)
            .WithBorder(panelStroke, panelHot || highContrast ? 2 : 1)
            .AutomationName("Shell editor")
            .Landmark(AutomationLandmarkType.Main)
            .Margin(24, 0, 24, 16)
            .OnDragEnter(args =>
            {
                if (TryGetPayload(args.Data, out var payload) && payload.InstanceId != null)
                    setPanelHot(true);
                ClearPreview();
            })
            .OnDragLeave(_ => setPanelHot(false))
            .OnDragOver(args =>
            {
                if (TryGetPayload(args.Data, out var payload) && payload.InstanceId != null)
                    args.AcceptedOperation = DragOperations.Move;
            })
            .OnDrop(args =>
            {
                setPanelHot(false);
                ClearPreview();
                if (TryGetPayload(args.Data, out var payload) && payload.InstanceId != null)
                    RemoveInstance(payload.InstanceId);
            });

        var trayScroller = ScrollView(target)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Margin(0, 12, 0, 0);
        var trayFooter = Border(
                FlexColumn(
                    Subtitle("Tray")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Body("Drag shells here to add or reorder them. Drag an existing shell back to Modules to remove it.")
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
            ]);

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
            OnSelectedTagChanged = tag => setPage(
                string.Equals(tag, "shells", StringComparison.OrdinalIgnoreCase)
                    ? SettingsPage.Shells
                    : SettingsPage.General),
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
        Action onDragEnd,
        int position,
        int setSize,
        bool highContrast,
        bool reduceMotion,
        double scale,
        bool dimmed)
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

        Element[] iconLayers = known
            ?
            [
                Image(IconFor(entry.ShellType))
                    .Width(24 * scale)
                    .Height(24 * scale)
                    .AccessibilityHidden()
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
            ]
            :
            [
                Image(IconFor(entry.ShellType))
                    .Width(24 * scale)
                    .Height(24 * scale)
                    .AccessibilityHidden()
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                BodyStrong("\u25B2")
                    .FontSize(26 * scale)
                    .Foreground(highContrast ? highlight : Theme.SystemCaution)
                    .Opacity(0.76)
                    .AccessibilityHidden()
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                BodyStrong("!")
                    .FontSize(12 * scale)
                    .Foreground(Theme.Ref("SystemColorWindowTextColorBrush"))
                    .Opacity(0.76)
                    .Margin(0, 4 * scale, 0, 0)
                    .AccessibilityHidden()
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
            ];

        var chipPanelBase = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                iconLayers)
            .Background(fill);
        Element chipPanel = !reduceMotion && !highContrast
            ? chipPanelBase.BackgroundTransition()
            : chipPanelBase;

        var chipBase = (Border(chipPanel) with { CornerRadius = 4 * scale })
            .Width(ChipSize * scale)
            .Height(ChipSize * scale)
            .WithBorder(stroke, highContrast || selected ? 2 : known ? 1 : 0)
            .HelpText($"{tip}. Press Delete to remove.")
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
                () => ShellDragPayload.ForInstance(entry.Id),
                DragOperations.Move,
                _ => onDragEnd());

        Element chip = chipBase;
        if (dimmed && !highContrast)
            chip = chip.Opacity(0.32);
        return WithInstantTooltip(chip.WithKey(entry.Id), tip);
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
        Action<string> onAddKind,
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
                return (Element)Button(
                        Grid(
                            [GridSize.Auto, GridSize.Star()],
                            [GridSize.Auto],
                            [
                                Image(kind.PreviewIconPath ?? AppAssets.IconPath)
                                    .Width(24)
                                    .Height(24)
                                    .AccessibilityHidden()
                                    .VAlign(VerticalAlignment.Center)
                                    .Grid(row: 0, column: 0),
                                FlexColumn(
                                        BodyStrong(name)
                                            .TextWrapping(TextWrapping.WrapWholeWords),
                                        Caption("Drag or click to add")
                                            .Foreground(Theme.SecondaryText)
                                            .Margin(0, 4, 0, 0))
                                    .Margin(12, 0, 0, 0)
                                    .Grid(row: 0, column: 1),
                            ]),
                        () => onAddKind(kindId))
                    .MinWidth(224)
                    .Padding(12)
                    .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                    .AutomationName($"Add {name} to tray")
                    .PositionInSet(index + 1, kinds.Count)
                    .OnDragStart(
                        () => ShellDragPayload.ForKind(kindId),
                        DragOperations.Copy,
                        _ => onDragEnd())
                    .WithKey(module.Id + ":" + kindId);
            })
            .ToArray();

        return Border(
                FlexColumn(
                    BodyStrong(module.DisplayName.Length > 0
                        ? module.DisplayName
                        : ShortName(module.Id)),
                    VStack(8, tiles)
                        .Margin(0, 12, 0, 0)))
            .MinWidth(256)
            .Padding(16)
            .CornerRadius(8)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.SubtleFill)
            .WithBorder(
                highContrast
                    ? Theme.Ref("SystemColorWindowTextColorBrush")
                    : Theme.CardStroke,
                highContrast ? 2 : 1)
            .WithKey(module.Id);
    }

    private static Element GhostSlot(bool highContrast, double scale) =>
        (Border(null!) with { CornerRadius = 4 * scale })
            .Width(ChipSize * scale)
            .Height(ChipSize * scale)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.SystemAttentionBackground)
            .WithBorder(
                highContrast
                    ? Theme.Ref("SystemColorHighlightColorBrush")
                    : Theme.Accent,
                2)
            .WithKey("ghost");

    private static Element WithInstantTooltip(Element element, string text) =>
        element
            .OnMountAdd(frameworkElement => AttachInstantTooltip(frameworkElement, text))
            .OnUnmountAdd(DetachInstantTooltip);

    private static void AttachInstantTooltip(FrameworkElement element, string text)
    {
        DetachInstantTooltip(element);
        var binding = new InstantTooltipBinding(text);
        InstantTooltipBindings.Add(element, binding);
        ToolTipService.SetToolTip(element, binding.ToolTip);
        binding.IsActive = true;
        element.PointerEntered += binding.PointerEntered;
        element.PointerExited += binding.PointerExited;
        element.DragStarting += binding.DragStarting;
    }

    private static void DetachInstantTooltip(FrameworkElement element)
    {
        if (!InstantTooltipBindings.TryGetValue(element, out var binding))
            return;

        binding.IsActive = false;
        element.PointerEntered -= binding.PointerEntered;
        element.PointerExited -= binding.PointerExited;
        element.DragStarting -= binding.DragStarting;
        ToolTipService.SetToolTip(element, null);
        InstantTooltipBindings.Remove(element);
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
