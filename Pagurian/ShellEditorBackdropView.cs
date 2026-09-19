using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using static Microsoft.UI.Reactor.Factories;
using XamlImage = Microsoft.UI.Xaml.Controls.Image;

namespace Pagurian;

// Shared visual policy for the interactive editor and the non-activating
// per-monitor blockers. A blurred snapshot is fully opaque; the solid root is
// both its capture-failure fallback and the hit-test surface for each window.
static class ShellEditorBackdrop
{
    private static readonly SolidColorBrush DarkScrimBrush = new(
        Windows.UI.Color.FromArgb(0x33, 0, 0, 0));
    private static readonly SolidColorBrush LightScrimBrush = new(
        Windows.UI.Color.FromArgb(0x29, 255, 255, 255));

    public static Element Apply(
        Element foreground,
        Element? snapshotImage,
        ColorScheme colorScheme)
    {
        var layers = new List<Element>();
        if (snapshotImage != null)
            layers.Add(snapshotImage);
        layers.Add(Border(null!).Background(ScrimBrush(colorScheme)));
        layers.Add(foreground);

        return Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                layers.ToArray())
            .Background(Theme.Ref("SystemColorWindowColorBrush"));
    }

    public static Element CreateSnapshotImage(ShellEditorSnapshot snapshot)
    {
        return (new XamlHostElement(
                () => new XamlImage
                {
                    Source = CreateBitmap(snapshot),
                    Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    IsHitTestVisible = false,
                })
            {
                TypeKey = $"shell-editor-snapshot:{snapshot.Version}",
            })
            .AccessibilityHidden();
    }

    private static WriteableBitmap CreateBitmap(ShellEditorSnapshot snapshot)
    {
        var bitmap = new WriteableBitmap(snapshot.PixelWidth, snapshot.PixelHeight);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(snapshot.Bgra, 0, snapshot.Bgra.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static SolidColorBrush ScrimBrush(ColorScheme colorScheme)
    {
        if (colorScheme == ColorScheme.Dark)
            return DarkScrimBrush;
        if (colorScheme == ColorScheme.Light)
            return LightScrimBrush;

        try
        {
            var color = new UISettings().GetColorValue(UIColorType.Background);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                0x33,
                color.R,
                color.G,
                color.B));
        }
        catch
        {
            return DarkScrimBrush;
        }
    }
}

// Secondary displays contain no editor controls. Snapshot updates are rare
// (only monitor hot-plug), so a small version state is enough to remount the
// in-memory image when its background becomes ready.
sealed class ShellEditorBackdropView : Component
{
    private readonly string _deviceName;

    public ShellEditorBackdropView(string deviceName)
    {
        _deviceName = deviceName;
    }

    public override Element Render()
    {
        var colorScheme = UseColorScheme();
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        UseEffect(() =>
        {
            void OnSnapshotChanged() => setVersion(++tick.Current);
            ShellEditorWindow.SnapshotChanged += OnSnapshotChanged;
            return () => ShellEditorWindow.SnapshotChanged -= OnSnapshotChanged;
        }, Array.Empty<object>());

        var snapshot = ShellEditorWindow.SnapshotFor(_deviceName);
        var snapshotImage = UseMemo(
            () => snapshot == null
                ? null
                : ShellEditorBackdrop.CreateSnapshotImage(snapshot),
            snapshot?.Version ?? 0L);

        return ShellEditorBackdrop.Apply(
            Border(null!)
                .AutomationName("Shell editor backdrop"),
            snapshotImage,
            colorScheme);
    }
}
