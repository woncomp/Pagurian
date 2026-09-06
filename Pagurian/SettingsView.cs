using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
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

// Root view of the settings window: the configuration-folder picker on top,
// a drawn mock of the real tray in the middle, and the pool of all loaded
// modules below. Tray membership is edited as a draft — drag a shell icon
// from the pool onto the strip to add it, drag chips sideways to reorder,
// drag a chip back into the pool to remove — and only applied (written to
// config.json and reconciled with the live tray via TrayShells.ApplyConfig)
// when Save is pressed. Closing the window simply discards an unsaved draft.
class SettingsView : Component
{
    private static readonly ConditionalWeakTable<FrameworkElement, InstantTooltipBinding>
        InstantTooltipBindings = new();

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

    // Strip geometry; the drop handler maps DragTargetArgs.Position.X through
    // these constants back to an insertion index.
    private const double StripPadX = 12;
    private const double ChipSize = 40;
    private const double ChipGap = 4;
    private const double ChipPitch = ChipSize + ChipGap;

    // In-proc drag payload: either a shell kind from the module pool (add)
    // or an existing tray instance (reorder / remove).
    private sealed record ShellDragPayload(string? KindId, string? InstanceId)
    {
        public static ShellDragPayload ForKind(string kindId) => new(kindId, null);
        public static ShellDragPayload ForInstance(string instanceId) => new(null, instanceId);
    }

    public override Element Render()
    {
        // Settings follows the effective Windows theme, independently of the
        // sampled taskbar theme used by cells and billboards.
        var colorScheme = UseColorScheme();
        var highContrastScheme = UseHighContrastScheme();
        var reduceMotion = UseReducedMotion();
        var settingsTheme = UseMemo(
            () => new SettingsThemeService(colorScheme, highContrastScheme),
            Array.Empty<object>());
        UseEffect(
            () => settingsTheme.Apply(colorScheme, highContrastScheme),
            colorScheme,
            highContrastScheme ?? "");
        var highContrast = colorScheme == ColorScheme.HighContrast;

        // Draft tray membership, loaded once from the config file — entries
        // whose module is not loaded stay visible/editable and are never
        // silently dropped on save.
        var initialDraft = UseMemo(() => LoadDraft(), Array.Empty<object>());
        var (draft, setDraft) = UseState(initialDraft);
        var (dirty, setDirty) = UseState(false);
        var initialDir = UseMemo(() => HostSettings.ConfigDir, Array.Empty<object>());
        var (dirText, setDirText) = UseState(initialDir);
        var (appliedDir, setAppliedDir) = UseState(initialDir);
        var (stripHot, setStripHot) = UseState(false);
        var (poolHot, setPoolHot) = UseState(false);
        var (selectedId, setSelectedId) = UseState<string?>(null);
        // Live drop-target preview on the strip: the ghost-slot index plus the
        // instance being dragged (null InstanceId = a pool kind is being added).
        var (preview, setPreview) = UseState<(int Index, string? InstanceId)?>(null);

        // Insertion index over the rendered slots. The dragged chip stays in
        // the layout while dragging (only dimmed) — removing a live XAML drag
        // source from the visual tree crashes the app when the drop lands.
        int InsertIndexFor(double x) =>
            Math.Clamp((int)Math.Floor((x - StripPadX) / ChipPitch), 0, draft.Count);

        void ClearPreview()
        {
            if (preview != null)
                setPreview(null);
        }

        void DropOnStrip(ShellDragPayload payload, double x)
        {
            var index = InsertIndexFor(x);
            if (payload.InstanceId != null)
            {
                var from = draft.FindIndex(e => e.Id == payload.InstanceId);
                if (from < 0)
                    return;
                var insertAt = from < index ? index - 1 : index;
                if (insertAt == from)
                    return; // dropped back onto itself
                var next = new List<TrayConfig.Entry>(draft);
                var item = next[from];
                next.RemoveAt(from);
                next.Insert(Math.Clamp(insertAt, 0, next.Count), item);
                setDraft(next);
            }
            else if (payload.KindId != null)
            {
                var next = new List<TrayConfig.Entry>(draft);
                next.Insert(index, new TrayConfig.Entry(
                    payload.KindId, TrayConfig.NextId(draft.Select(e => e.Id)), null));
                setDraft(next);
            }
            setDirty(true);
        }

        void RemoveInstance(string instanceId)
        {
            var next = draft.Where(e => e.Id != instanceId).ToList();
            if (next.Count == draft.Count)
                return;
            setDraft(next);
            if (selectedId == instanceId)
                setSelectedId(null);
            setDirty(true);
        }

        void AddKind(string kindId)
        {
            var next = new List<TrayConfig.Entry>(draft)
            {
                new(
                    kindId,
                    TrayConfig.NextId(draft.Select(e => e.Id)),
                    null),
            };
            setDraft(next);
            setDirty(true);
        }

        void UpdateEntrySettings(string instanceId, JsonElement? settings)
        {
            if (settings is { } value && value.ValueKind != JsonValueKind.Object)
            {
                PagurianLog.HostError(
                    $"settings: configuration view for #{instanceId} returned non-object JSON");
                return;
            }

            var index = draft.FindIndex(e => e.Id == instanceId);
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
                return; // keep the draft so the edit isn't lost
            }
            setDirty(false);
            PagurianLog.Host($"settings: applied tray configuration ({draft.Count} shells)");
        }

        void RevertDraft()
        {
            var entries = LoadDraft();
            setDraft(entries);
            if (selectedId != null && entries.All(e => e.Id != selectedId))
                setSelectedId(null);
            setDirty(false);
        }

        void ApplyDir()
        {
            try
            {
                Directory.CreateDirectory(dirText.Trim());
                HostSettings.SetConfigDir(dirText.Trim());
                // Loads (and Hello-only seeds) config.json in the new folder,
                // then reconciles the live tray with it.
                var entries = TrayConfig.Load();
                TrayShells.ApplyConfig(entries);
                setAppliedDir(HostSettings.ConfigDir);
                setDirText(HostSettings.ConfigDir);
                setDraft(entries.ToList());
                if (selectedId != null && entries.All(e => e.Id != selectedId))
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

        // ── Configuration folder ────────────────────────────────────────
        var trimmedDir = dirText.Trim();
        var dirChanged = trimmedDir.Length > 0 &&
            !string.Equals(trimmedDir, appliedDir, StringComparison.OrdinalIgnoreCase);

        var highContrastWindow = Theme.Ref("SystemColorWindowColorBrush");
        var highContrastText = Theme.Ref("SystemColorWindowTextColorBrush");
        var highContrastHighlight = Theme.Ref("SystemColorHighlightColorBrush");
        var cardFill = highContrast ? highContrastWindow : Theme.CardBackground;
        var cardStroke = highContrast ? highContrastText : Theme.CardStroke;
        var cardStrokeThickness = highContrast ? 2 : 1;

        var configRow = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(dirText, v => setDirText(v),
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

        var configCard = SettingsCard(
                FlexColumn(
                    Subtitle("Configuration folder")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Body("Choose where Pagurian stores its tray configuration.")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 4, 0, 0),
                    configRow.Margin(0, 12, 0, 0),
                    configMeta),
                cardFill,
                cardStroke,
                cardStrokeThickness)
            .Landmark(AutomationLandmarkType.Form);

        // ── Tray mock ───────────────────────────────────────────────────
        // While a drag hovers the strip, a ghost slot marks the insertion
        // point and SpringLayoutAnimation slides the chips aside in real
        // time as the slot follows the cursor. The dragged chip stays in the
        // layout (dimmed) for the whole drag: unmounting a live XAML drag
        // source crashes the app when the drop lands.
        var chips = new List<Element>();
        for (var i = 0; i <= draft.Count; i++)
        {
            if (preview is { } pv && pv.Index == i)
                chips.Add(GhostSlot(highContrast));
            if (i < draft.Count)
            {
                var entry = draft[i];
                chips.Add(TrayChip(
                    entry,
                    selected: selectedId == entry.Id,
                    onSelected: () => setSelectedId(entry.Id),
                    onRemove: () => RemoveInstance(entry.Id),
                    onDragEnd: ClearPreview,
                    position: i + 1,
                    setSize: draft.Count,
                    highContrast,
                    reduceMotion,
                    dimmed: preview?.InstanceId == entry.Id));
            }
        }
        Element stripContent = chips.Count == 0
            ? Caption("Tray is empty — drag or click a shell below to add it")
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
            : HStack(ChipGap, chips.ToArray())
                .VAlign(VerticalAlignment.Center);
        if (!reduceMotion && chips.Count > 0)
            stripContent = stripContent.SpringLayoutAnimation();

        var stripFill = stripHot
            ? highContrast ? highContrastWindow : Theme.SystemAttentionBackground
            : highContrast ? highContrastWindow : Theme.LayerFill;
        var stripStroke = stripHot
            ? highContrast ? highContrastHighlight : Theme.Accent
            : highContrast ? highContrastText : Theme.ControlStroke;
        var stripPanelBase = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [Border(stripContent).Padding(StripPadX, 8, StripPadX, 8)])
            .Background(stripFill);
        Element stripPanel = !reduceMotion && !highContrast
            ? stripPanelBase.BackgroundTransition()
            : stripPanelBase;
        var stripBase = (Border(stripPanel) with { CornerRadius = 8 })
            .MinHeight(64)
            .WithBorder(stripStroke, highContrast ? 2 : 1)
            .AutomationName("Tray shell selector")
            .IsTabStop(true)
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
                if (TryGetPayload(args.Data, out var p))
                {
                    args.AcceptedOperation = p.InstanceId != null
                        ? DragOperations.Move
                        : DragOperations.Copy;
                    var index = InsertIndexFor(args.Position.X);
                    if (preview == null || preview.Value.Index != index || preview.Value.InstanceId != p.InstanceId)
                        setPreview((index, p.InstanceId));
                }
            })
            .OnDrop(args =>
            {
                setStripHot(false);
                ClearPreview();
                if (TryGetPayload(args.Data, out var p))
                    DropOnStrip(p, args.Position.X);
            });
        Element strip = stripBase;

        // ── Module pool ─────────────────────────────────────────────────
        var cards = ModuleLoader.Modules
            .Select(m => (
                Module: m,
                Kinds: ModuleLoader.Kinds
                    .Where(k => k.ShellType.Assembly == m.GetType().Assembly)
                    .ToList()))
            .Where(x => x.Kinds.Count > 0)
            .Select(x => ModuleCard(x.Module, x.Kinds, AddKind, ClearPreview, highContrast))
            .ToArray();
        Element poolContent = cards.Length == 0
            ? Body("No modules loaded")
                .Foreground(Theme.SecondaryText)
            : (FlexRow(cards) with { Wrap = FlexWrap.Wrap, RowGap = 12, ColumnGap = 12 });

        var poolFill = poolHot
            ? highContrast ? highContrastWindow : Theme.SystemCriticalBackground
            : cardFill;
        var poolStroke = poolHot
            ? highContrast ? highContrastHighlight : Theme.SystemCritical
            : cardStroke;
        var pool = SettingsCard(
                FlexColumn(
                    Subtitle("Modules")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Body("Drag a shell to choose its exact position, or click it to add it to the end of the tray.")
                        .TextWrapping(TextWrapping.WrapWholeWords)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 4, 0, 0),
                    poolContent.Margin(0, 16, 0, 0)),
                poolFill,
                poolStroke,
                poolHot || highContrast ? 2 : 1)
            .AutomationName("Available modules")
            .OnDragEnter(args =>
            {
                if (TryGetPayload(args.Data, out var p) && p.InstanceId != null)
                    setPoolHot(true);
                ClearPreview();
            })
            .OnDragLeave(_ => setPoolHot(false))
            .OnDragOver(args =>
            {
                // Only tray instances are accepted here: dropping one removes
                // it from the draft; pool icons dropped back are a no-op.
                if (TryGetPayload(args.Data, out var p) && p.InstanceId != null)
                    args.AcceptedOperation = DragOperations.Move;
            })
            .OnDrop(args =>
            {
                setPoolHot(false);
                ClearPreview();
                if (TryGetPayload(args.Data, out var p) && p.InstanceId != null)
                    RemoveInstance(p.InstanceId);
            });

        var selectedEntry = selectedId == null
            ? null
            : draft.FirstOrDefault(e => e.Id == selectedId);
        Element modulesCard;
        if (selectedEntry == null)
        {
            modulesCard = pool;
        }
        else
        {
            var selectedName = NameFor(selectedEntry.ShellType);
            var selectedHeader = Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    FlexColumn(
                            Subtitle($"{selectedName} configuration")
                                .HeadingLevel(AutomationHeadingLevel.Level2),
                            Caption($"Shell #{selectedEntry.Id}")
                                .Foreground(Theme.SecondaryText)
                                .Margin(0, 4, 0, 0))
                        .Grid(row: 0, column: 0),
                    Button("Back to modules", () => setSelectedId(null))
                        .Grid(row: 0, column: 1),
                ]);
            modulesCard = SettingsCard(
                FlexColumn(
                    selectedHeader,
                    ShellConfigurationPanel(
                            selectedEntry,
                            settingsTheme,
                            highContrast,
                            settings => UpdateEntrySettings(selectedEntry.Id, settings))
                        .Margin(0, 16, 0, 0)),
                cardFill,
                cardStroke,
                cardStrokeThickness);
        }

        var trayCard = SettingsCard(
            FlexColumn(
                Subtitle("Tray")
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                Body("Drag shells onto the strip to add them, drag sideways to reorder, or drag them back to Modules to remove them.")
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 4, 0, 0),
                strip.Margin(0, 16, 0, 0)),
            cardFill,
            cardStroke,
            cardStrokeThickness);

        // ── Footer ──────────────────────────────────────────────────────
        var footerStatusBrush = dirty && !highContrast
            ? Theme.SystemCaution
            : Theme.SecondaryText;
        var footer = Border(
                Grid(
                    [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                    [GridSize.Auto],
                    [
                        Caption(dirty ? "Unsaved changes" : "All changes saved")
                            .Foreground(footerStatusBrush)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        Button("Revert", RevertDraft)
                            .IsEnabled(dirty)
                            .AccessKey("R")
                            .Grid(row: 0, column: 1),
                        Button("Save", SaveDraft)
                            .IsEnabled(dirty)
                            .AccessKey("S")
                            .ApplyStyle("AccentButtonStyle")
                            .Margin(8, 0, 0, 0)
                            .Grid(row: 0, column: 2),
                    ]))
            .Padding(24, 12, 24, 12)
            .Background(highContrast ? highContrastWindow : Theme.LayerFill)
            .WithBorder(highContrast ? highContrastText : Theme.DividerStroke, highContrast ? 2 : 1);

        var page = ScrollView(
                Border(
                    FlexColumn(
                        Title("Settings")
                            .HeadingLevel(AutomationHeadingLevel.Level1),
                        Body("Configure where Pagurian stores its data and how shells appear in the taskbar tray.")
                            .TextWrapping(TextWrapping.WrapWholeWords)
                            .Foreground(Theme.SecondaryText)
                            .Margin(0, 8, 0, 0),
                        configCard.Margin(0, 24, 0, 0),
                        trayCard.Margin(0, 16, 0, 0),
                        modulesCard.Margin(0, 16, 0, 0)))
                .Padding(24))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Landmark(AutomationLandmarkType.Main);

        return Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [
                    TitleBar("Pagurian")
                        .Grid(row: 0, column: 0),
                    page.Grid(row: 1, column: 0),
                    footer.Grid(row: 2, column: 0),
                ])
            .Backdrop(BackdropKind.MicaAlt);
    }

    private static BorderElement SettingsCard(
        Element content,
        ThemeRef background,
        ThemeRef stroke,
        double strokeThickness) =>
        Border(content)
            .Padding(16)
            .CornerRadius(8)
            .Background(background)
            .WithBorder(stroke, strokeThickness);

    // One chip on the tray mock: the kind's preview icon (Pagurian icon as
    // fallback), draggable for reorder/removal. Entries whose module is not
    // loaded get a warning tint instead of being hidden. While the chip is
    // being dragged it stays mounted but dimmed (see the strip comment).
    private static Element TrayChip(
        TrayConfig.Entry entry,
        bool selected,
        Action onSelected,
        Action onRemove,
        Action onDragEnd,
        int position,
        int setSize,
        bool highContrast,
        bool reduceMotion,
        bool dimmed = false)
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
            ? selected || !known ? highlight : windowText
            : !known
                ? Theme.SystemCritical
                : selected ? Theme.Accent : Theme.ControlStroke;
        var strokeThickness = highContrast || selected || !known ? 2 : 1;

        var chipPanelBase = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [
                    Image(IconFor(entry.ShellType))
                        .Width(24)
                        .Height(24)
                        .AccessibilityHidden()
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center),
                ])
            .Background(fill);
        Element chipPanel = !reduceMotion && !highContrast
            ? chipPanelBase.BackgroundTransition()
            : chipPanelBase;
        var chipBase = (Border(chipPanel) with { CornerRadius = 4 })
            .Width(ChipSize)
            .Height(ChipSize)
            .WithBorder(stroke, strokeThickness)
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
        IThemeService settingsTheme,
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
                settingsTheme,
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

    // One module card with a visible, clickable and draggable tile per shell
    // kind. Click/keyboard appends; drag supports exact insertion.
    private static Element ModuleCard(
        PagurianModule module,
        List<ShellAttribute> kinds,
        Action<string> onAddKind,
        Action onDragEnd,
        bool highContrast)
    {
        var tiles = kinds
            .Select((k, index) =>
            {
                var kindId = k.ShellType.FullName!;
                var name = k.DisplayName.Length > 0 ? k.DisplayName : ShortName(kindId);
                return (Element)Button(
                        Grid(
                            [GridSize.Auto, GridSize.Star()],
                            [GridSize.Auto],
                            [
                                Image(k.PreviewIconPath ?? AppAssets.IconPath)
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

    private static List<TrayConfig.Entry> LoadDraft() => TrayConfig.Load().ToList();

    // Ghost insertion slot shown in the strip while a drag hovers over it.
    private static Element GhostSlot(bool highContrast) =>
        (Border(null!) with { CornerRadius = 4 })
            .Width(ChipSize)
            .Height(ChipSize)
            .Background(highContrast
                ? Theme.Ref("SystemColorWindowColorBrush")
                : Theme.SystemAttentionBackground)
            .WithBorder(
                highContrast
                    ? Theme.Ref("SystemColorHighlightColorBrush")
                    : Theme.Accent,
                2)
            .WithKey("ghost");

    // The settings window is a normal activated window, so XAML tooltips
    // work — but ToolTipService only opens them after a hover delay. Drive
    // the ToolTip manually so the name pops up the instant the pointer
    // lands, and close it the moment a drag starts.
    private static Element WithInstantTooltip(Element element, string text) =>
        element
            .OnMountAdd(fe => AttachInstantTooltip(fe, text))
            .OnUnmountAdd(DetachInstantTooltip);

    private static void AttachInstantTooltip(FrameworkElement element, string text)
    {
        // A pooled native element should have been detached before reuse, but
        // clean up defensively so a stale binding can never survive remount.
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

        // Invalidate callbacks first, then detach handlers before clearing the
        // native tooltip association. Any already-queued callback now no-ops.
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
        var i = fullName.LastIndexOf('.');
        return i >= 0 ? fullName[(i + 1)..] : fullName;
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
        InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(windowId.Value));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
