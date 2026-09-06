using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Hello;

// The Hello billboard: a "Hello World" line and a button that shows a Win32
// message box. Window chrome and placement are host-managed; only the
// content and the design size live here.
class HelloBillboard : Billboard
{
    public override double WidthDip => 320;

    public override double HeightDip => 192;

    public override string Title => "Pagurian";

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            Theme.Changed += OnChanged;
            return () => Theme.Changed -= OnChanged;
        }, Array.Empty<object>());

        var highContrast = colorScheme == ColorScheme.HighContrast;
        var requestedTheme = highContrast
            ? ElementTheme.Default
            : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        var greetingCard = MaterialCard(
            VStack(8,
                BodyLarge("Hello World"),
                Caption(HelloSettings.Message(Shell.Settings))
                    .Foreground(ReactorTheme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)),
            highContrast);

        var content = FlexColumn(
            Subtitle("Hello")
                .HeadingLevel(AutomationHeadingLevel.Level1),
            (ScrollViewer(greetingCard) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
                .Margin(0, 8, 0, 0)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Flex(1),
            Button("Say Hello Again",
                    () => MessageBoxes.Show(
                        HelloSettings.Message(Shell.Settings),
                        "Pagurian"))
                .ApplyStyle("AccentButtonStyle")
                .Margin(0, 8, 0, 0));

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private static BorderElement Page(Element content, bool highContrast)
    {
        var page = Border(content)
            .Padding(8)
            .CornerRadius(8);

        return highContrast
            ? page
                .Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
                .WithBorder(ReactorTheme.Ref("SystemColorWindowTextColorBrush"), 2)
                .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge)
            : page;
    }

    private static BorderElement MaterialCard(Element content, bool highContrast) =>
        Border(content)
            .Padding(12)
            .CornerRadius(8)
            .Background(highContrast
                ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.SurfaceStroke,
                highContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
}
