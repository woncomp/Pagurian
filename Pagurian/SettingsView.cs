using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using Windows.Storage.Pickers;
using Windows.UI;
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
    // Strip geometry; the drop handler maps DragTargetArgs.Position.X through
    // these constants back to an insertion index.
    private const double StripPadX = 8;
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
        // Re-render on taskbar theme flips (all colors are computed per render).
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            ThemeService.Instance.Changed += OnChanged;
            return () =>
            {
                ThemeService.Instance.Changed -= OnChanged;
            };
        }, Array.Empty<object>());

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

        void DropOnStrip(ShellDragPayload payload, double x)
        {
            var index = Math.Clamp((int)Math.Floor((x - StripPadX) / ChipPitch), 0, draft.Count);
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
            setDraft(LoadDraft());
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

        var isDark = ThemeService.Instance.IsDark;
        var textBrush = ThemeService.Instance.TextBrush;
        var subtleBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x99, 255, 255, 255)
            : Color.FromArgb(0x99, 0, 0, 0));
        var outlineBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x30, 255, 255, 255)
            : Color.FromArgb(0x30, 0, 0, 0));
        var chipBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x14, 255, 255, 255)
            : Color.FromArgb(0x10, 0, 0, 0));
        var missingBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xE8, 0x11, 0x11));
        var stripBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
        var stripHotBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x30, 0x30, 0x30)
            : Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE4));
        var cardBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x0C, 255, 255, 255)
            : Color.FromArgb(0x0C, 0, 0, 0));
        var poolBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x08, 255, 255, 255)
            : Color.FromArgb(0x08, 0, 0, 0));
        var poolHotBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0x3B, 0x3B));

        // ── Configuration folder ────────────────────────────────────────
        var trimmedDir = dirText.Trim();
        var dirChanged = trimmedDir.Length > 0 &&
            !string.Equals(trimmedDir, appliedDir, StringComparison.OrdinalIgnoreCase);

        var configRow = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(dirText, v => setDirText(v),
                        placeholderText: "Folder containing config.json")
                    .AutomationName("Configuration folder")
                    .VerticalAlignment(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                Button("Browse…", Browse)
                    .Margin(8, 0, 0, 0)
                    .Grid(row: 0, column: 1),
                Button("Apply", ApplyDir)
                    .IsEnabled(dirChanged)
                    .Margin(8, 0, 0, 0)
                    .Grid(row: 0, column: 2),
                Button("Reset", () => setDirText(HostSettings.DefaultConfigDir))
                    .Margin(8, 0, 0, 0)
                    .Grid(row: 0, column: 3),
            ]);

        // ── Tray mock ───────────────────────────────────────────────────
        var chips = draft
            .Select(e => TrayChip(e, chipBrush, missingBrush))
            .ToArray();
        Element stripContent = chips.Length == 0
            ? TextBlock("Tray is empty — drag shells here from the modules below")
                .FontSize(12)
                .Foreground(subtleBrush)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .VerticalAlignment(VerticalAlignment.Center)
            : HStack(ChipGap, chips)
                .VerticalAlignment(VerticalAlignment.Center);

        var strip = (Border(stripContent) with { CornerRadius = 8 })
            .Height(56)
            .Padding(StripPadX, 0, StripPadX, 0)
            .Background(stripHot ? stripHotBrush : stripBrush)
            .BorderBrush(outlineBrush)
            .BorderThickness(1)
            .OnDragEnter(_ => setStripHot(true))
            .OnDragLeave(_ => setStripHot(false))
            .OnDragOver(args =>
            {
                if (TryGetPayload(args.Data, out var p))
                    args.AcceptedOperation = p.InstanceId != null
                        ? DragOperations.Move
                        : DragOperations.Copy;
            })
            .OnDrop(args =>
            {
                setStripHot(false);
                if (TryGetPayload(args.Data, out var p))
                    DropOnStrip(p, args.Position.X);
            })
            .Margin(0, 6, 0, 0);

        // ── Module pool ─────────────────────────────────────────────────
        var cards = ModuleLoader.Modules
            .Select(m => (
                Module: m,
                Kinds: ModuleLoader.Kinds
                    .Where(k => k.ShellType.Assembly == m.GetType().Assembly)
                    .ToList()))
            .Where(x => x.Kinds.Count > 0)
            .Select(x => ModuleCard(x.Module, x.Kinds, textBrush, chipBrush, cardBrush, outlineBrush))
            .ToArray();
        Element poolContent = cards.Length == 0
            ? TextBlock("No modules loaded")
                .FontSize(12)
                .Foreground(subtleBrush)
            : (FlexRow(cards) with { Wrap = FlexWrap.Wrap, RowGap = 10, ColumnGap = 10 })
                .Padding(2);

        var pool = (Border(ScrollViewer(poolContent)) with { CornerRadius = 10 })
            .Padding(10)
            .Background(poolHot ? poolHotBrush : poolBrush)
            .BorderBrush(outlineBrush)
            .BorderThickness(1)
            .Flex(1)
            .OnDragEnter(args =>
            {
                if (TryGetPayload(args.Data, out var p) && p.InstanceId != null)
                    setPoolHot(true);
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
                if (TryGetPayload(args.Data, out var p) && p.InstanceId != null)
                    RemoveInstance(p.InstanceId);
            })
            .Margin(0, 6, 0, 0);

        // ── Footer ──────────────────────────────────────────────────────
        var footer = HStack(8,
                TextBlock("Unsaved changes")
                    .FontSize(12)
                    .Foreground(subtleBrush)
                    .IsVisible(dirty)
                    .VerticalAlignment(VerticalAlignment.Center),
                Button("Revert", RevertDraft)
                    .IsEnabled(dirty),
                Button("Save", SaveDraft)
                    .IsEnabled(dirty)
                    .ApplyStyle("AccentButtonStyle"))
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Margin(0, 12, 0, 0);

        return FlexColumn(
                TextBlock("Settings")
                    .FontSize(20)
                    .SemiBold()
                    .Foreground(textBrush),

                TextBlock("Configuration folder")
                    .FontSize(13)
                    .SemiBold()
                    .Foreground(textBrush)
                    .Margin(0, 14, 0, 0),
                configRow.Margin(0, 6, 0, 0),
                TextBlock($"Active config file: {Path.Combine(appliedDir, "config.json")}")
                    .FontSize(11)
                    .Foreground(subtleBrush)
                    .TextWrapping(TextWrapping.Wrap)
                    .Margin(0, 4, 0, 0),

                TextBlock("Tray")
                    .FontSize(13)
                    .SemiBold()
                    .Foreground(textBrush)
                    .Margin(0, 16, 0, 0),
                TextBlock("Drag shells onto the strip to add them · drag sideways to reorder · drag back into the module pool to remove. Save applies the changes to the real tray.")
                    .FontSize(11)
                    .Foreground(subtleBrush)
                    .TextWrapping(TextWrapping.Wrap)
                    .Margin(0, 2, 0, 0),
                strip,

                TextBlock("Modules")
                    .FontSize(13)
                    .SemiBold()
                    .Foreground(textBrush)
                    .Margin(0, 16, 0, 0),
                pool,

                footer)
            .Padding(16);
    }

    // One chip on the tray mock: the kind's preview icon (Pagurian icon as
    // fallback), draggable for reorder/removal. Entries whose module is not
    // loaded get a warning tint instead of being hidden.
    private static Element TrayChip(TrayConfig.Entry entry, Brush chipBrush, Brush missingBrush)
    {
        var known = ModuleLoader.TryGetKind(entry.ShellType, out var kind);
        var name = NameFor(entry.ShellType);
        return (Border(Image(IconFor(entry.ShellType))
                    .Width(22)
                    .Height(22)
                    .AccessibilityHidden())
                with { CornerRadius = 6 })
            .Width(ChipSize)
            .Height(ChipSize)
            .Padding(9)
            .Background(known ? chipBrush : missingBrush)
            .HelpText(known
                ? $"{name}  ·  #{entry.Id}"
                : $"{name}  ·  #{entry.Id} (module not loaded)")
            .OnDragStart(() => ShellDragPayload.ForInstance(entry.Id), DragOperations.Move)
            .WithKey(entry.Id);
    }

    // One pool card: module display name plus one draggable icon per shell
    // kind the module offers.
    private static Element ModuleCard(
        PagurianModule module,
        List<ShellAttribute> kinds,
        Brush textBrush,
        Brush chipBrush,
        Brush cardBrush,
        Brush outlineBrush)
    {
        var icons = kinds
            .Select(k =>
                (Element)(Border(Image(k.PreviewIconPath ?? AppAssets.IconPath)
                        .Width(20)
                        .Height(20)
                        .AccessibilityHidden())
                    with { CornerRadius = 6 })
                .Width(36)
                .Height(36)
                .Padding(8)
                .Background(chipBrush)
                .HelpText(k.DisplayName.Length > 0 ? k.DisplayName : ShortName(k.ShellType.FullName!))
                .OnDragStart(() => ShellDragPayload.ForKind(k.ShellType.FullName!), DragOperations.Copy)
                .WithKey(module.Id + ":" + k.ShellType.FullName))
            .ToArray();

        return (Border(FlexColumn(
                    TextBlock(module.DisplayName.Length > 0 ? module.DisplayName : ShortName(module.Id))
                        .FontSize(12)
                        .SemiBold()
                        .Foreground(textBrush),
                    HStack(6, icons).Margin(0, 6, 0, 0)))
                with { CornerRadius = 10 })
            .Padding(10)
            .Background(cardBrush)
            .BorderBrush(outlineBrush)
            .BorderThickness(1)
            .WithKey(module.Id);
    }

    private static List<TrayConfig.Entry> LoadDraft() => TrayConfig.Load().ToList();

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
