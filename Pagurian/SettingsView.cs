using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Pagurian.Sdk;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Host-level settings only. Tray membership and per-shell settings live in
// the dedicated ShellEditorView opened from the tray icon or its menu.
class SettingsView : Component
{
    public override Element Render()
    {
        var highContrast = UseColorScheme() == ColorScheme.HighContrast;
        var initialDir = UseMemo(() => HostSettings.ConfigDir, Array.Empty<object>());
        var (dirText, setDirText) = UseState(initialDir);
        var (appliedDir, setAppliedDir) = UseState(initialDir);

        void ApplyDir()
        {
            try
            {
                Directory.CreateDirectory(dirText.Trim());
                HostSettings.SetConfigDir(dirText.Trim());
                var entries = TrayConfig.Load();
                TrayShells.ApplyConfig(entries);
                setAppliedDir(HostSettings.ConfigDir);
                setDirText(HostSettings.ConfigDir);
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

        var trimmedDir = dirText.Trim();
        var dirChanged = trimmedDir.Length > 0 &&
            !string.Equals(trimmedDir, appliedDir, StringComparison.OrdinalIgnoreCase);
        var windowFill = Theme.Ref("SystemColorWindowColorBrush");
        var windowText = Theme.Ref("SystemColorWindowTextColorBrush");

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

        var page = ScrollView(
                Border(
                    FlexColumn(
                        Title("Settings")
                            .HeadingLevel(AutomationHeadingLevel.Level1),
                        Body("Configure Pagurian host settings. Use Edit Shells to change the taskbar tray.")
                            .TextWrapping(TextWrapping.WrapWholeWords)
                            .Foreground(Theme.SecondaryText)
                            .Margin(0, 8, 0, 0),
                        configCard.Margin(0, 24, 0, 0)))
                .Padding(24))
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Landmark(AutomationLandmarkType.Main);

        return Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    TitleBar("Pagurian")
                        .Grid(row: 0, column: 0),
                    page.Grid(row: 1, column: 0),
                ])
            .Backdrop(BackdropKind.MicaAlt);
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
