using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Hello;

// Replica of the native Windows 11 datetime widget: two centered 12-DIP
// lines (time over date, current-culture short formats), updated every
// second. The cell sizes to the wider of the two lines (Yoga stretch keeps
// both TextBlocks cell-wide, so TextAlignment.Center centers the text); the
// host reads the rendered width back through its layout pass.
class ClockCell : ShellCell
{
    private const double FontSizeDip = 12;
    private const double PaddingXDip = 10;

    private static string NowTime() => DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
    private static string NowDate() => DateTime.Now.ToString("d", CultureInfo.CurrentCulture);

    public override Element Render()
    {
        var (time, setTime) = UseState(NowTime());
        var (date, setDate) = UseState(NowDate());
        var lastText = UseRef(NowTime() + "|" + NowDate());

        // 1s clock updates. Both formats change at most once per minute; the
        // guard keeps the timer from re-rendering when nothing changed.
        UseEffect(() =>
        {
            var timer = ReactorApp.UIDispatcher!.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                string t = NowTime(), d = NowDate();
                var key = t + "|" + d;
                if (key == lastText.Current)
                    return;
                lastText.Current = key;
                setTime(t);
                setDate(d);
            };
            timer.Start();
            return () => timer.Stop();
        }, Array.Empty<object>());

        return Border(
                FlexColumn(
                    TextBlock(time)
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Foreground(Theme.TextBrush),
                    TextBlock(date)
                        .FontSize(FontSizeDip)
                        .TextAlignment(TextAlignment.Center)
                        .Foreground(Theme.TextBrush))
                    .VerticalAlignment(VerticalAlignment.Center))
            .Padding(PaddingXDip, 0, PaddingXDip, 0);
    }
}
