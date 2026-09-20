using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pagurian;
using Pagurian.Modules.Copilot;
using Pagurian.Sdk;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using static Microsoft.UI.Reactor.Factories;
using XamlText = Microsoft.UI.Xaml.Controls.TextBlock;

// No production Startup, auth, settings IO, hook installation, taskbar injection,
// or SDK calls. Source snapshots and the host-style settings draft are in memory.
ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
    var harness = new Harness();
    Application.Current.UnhandledException += (_, e) => harness.Fail(e.Exception.ToString());
    harness.Start();
});

sealed class FakeSource : ICopilotUsageSource
{
    public CopilotUsageState State { get; private set; }
    private Action? _changed;
    public int Subscribers { get; private set; }
    public int Refreshes { get; private set; }
    public event Action? Changed
    {
        add { _changed += value; Subscribers++; }
        remove { _changed -= value; Subscribers--; }
    }
    public FakeSource(CopilotUsageState state) => State = state;
    public void Refresh() => Refreshes++;
    public void Publish(CopilotUsageState state) { State = state; _changed?.Invoke(); }
}

sealed class FixtureTheme : IThemeService
{
    public bool IsDark { get; private set; } = true;
    public Brush TextBrush { get; } = new SolidColorBrush(Microsoft.UI.Colors.White);
    private Action? _changed;
    public int Subscribers { get; private set; }
    public event Action? Changed
    {
        add { _changed += value; Subscribers++; }
        remove { _changed -= value; Subscribers--; }
    }
    public void Set(bool dark)
    {
        IsDark = dark;
        ((SolidColorBrush)TextBrush).Color = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _changed?.Invoke();
    }
}
sealed class FixtureShell : Shell;

sealed class DraftHost : Component
{
    internal static Harness Owner = null!;
    public override Element Render()
    {
        var h = Owner;
        var (_, setVersion) = UseState(0);
        var version = UseRef(0);
        UseEffect(() =>
        {
            void Changed() => setVersion(++version.Current);
            h.Changed += Changed;
            return () => h.Changed -= Changed;
        }, h);
        if (h.Terminal) return TextBlock("Completing disposal checks");
        return Border(Grid([GridSize.Star(2), GridSize.Star()], [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            Subtitle("Isolated Copilot Usage fixture — fake account / in-memory draft")
                .Grid(row: 0, columnSpan: 2),
            ScrollViewer(h.Mounted
                ? new ComponentElement(typeof(UsageConfiguration),
                    new ShellConfigurationProps("fixture-a", h.Draft, h.SetDraft, h.Theme, h.Log))
                    .Provide(UsageConfigurationContext.Source, h.ConfigurationSource)
                : Caption("Draft discarded; editor unmounted"))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
                .Grid(row: 1, column: 0),
            ScrollViewer(VStack(8,
                    Component<UsagePaceCard, UsagePaceCardProps>(new(h.SavedA.Pace, h.Dark, h.HighContrast)),
                    HStack(8,
                        Border(new ComponentElement(typeof(UsageCell), h.CellProps))
                            .Height(48).OnMount(root => h.CellRoot = root),
                        // Exact Metrics typography/layout baseline, no sampler start.
                        Border(FlexColumn(
                            Progress(50).Height(3).Margin(0, 0, 0, 2),
                            TextBlock("100%").FontSize(12),
                            TextBlock("CPU").FontSize(12))
                            .VerticalAlignment(VerticalAlignment.Center))
                            .Padding(6, 0, 6, 0).Height(48)
                            .OnMount(root => h.MetricsRoot = root)),
                    Caption("Top: Credits · Pulse: workday balance · Bottom: used %"),
                    h.HighContrast
                        ? UsageCalendarView.BalanceRow(h.SavedA.Pace, h.Dark, true)
                            .AutomationId("FixtureHighContrastBalance")
                        : null,
                    Caption("No SDK start or credential/configuration IO.")))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
                .Grid(row: 1, column: 1),
            HStack(8,
                Button("Save draft", h.Save),
                Button("Revert draft", h.Revert),
                Button("Discard draft", h.Discard),
                Button("Reopen editor", h.Reopen),
                Caption(h.Dirty ? "Unsaved changes" : "Saved"))
                .Grid(row: 2, columnSpan: 2)))
            .Padding(16)
            .Background(h.HighContrast ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
                : UsageBalanceTooltip.ThemeBrush("SolidBackgroundFillColorBaseBrush", h.Dark))
            .RequestedTheme(h.Dark ? ElementTheme.Dark : ElementTheme.Light);
    }
}

sealed class Harness
{
    internal DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(8));
    internal FixtureTheme Theme = new();
    internal Logger Log = (Logger)typeof(Logger).GetMethod("For", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, ["usage-ui-fixture"])!;
    internal CopilotUsageModel SavedA = null!, SavedB = null!;
    internal UsageConfigurationSource ConfigurationSource = null!;
    internal ShellCellProps CellProps = null!;
    internal FrameworkElement? CellRoot, MetricsRoot;
    internal JsonElement? Saved, Draft;
    internal bool Mounted = true, Dark = true, HighContrast, Terminal;
    internal bool Dirty => Saved?.GetRawText() != Draft?.GetRawText();
    internal event Action? Changed;
    private FakeSource _source = null!;
    private CopilotUsageModel? _draftModel;
    private ReactorWindow _window = null!;
    private BillboardSession? _billboard;
    private ReactorWindow _billboardWindow => _billboard!.Window!;
    private DispatcherQueueTimer? _timer;
    private readonly Queue<Func<Task>> _steps = new();
    private int _checks;
    private bool _finished, _busy;
    private double _calendarWidth;
    private ToolTip? _openTip;
    private FrameworkElement? _tipOwner;
    private object? _tipValue;
    private FrameworkElement? _oldBalance;
    private UsageBalanceTooltip? _balanceController;
    private ToolTip? _observedBalanceTip;
    private int _balanceOpenEvents, _balanceCloseEvents;
    private ResourceDictionary? _originalResources;
    private readonly string _output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "..", "..", "artifacts", "copilot-usage-ui"));
    private FrameworkElement Root => (FrameworkElement)_window.NativeWindow.Content;
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    internal void SetDraft(JsonElement? value) { Draft = value; Changed?.Invoke(); }
    internal void Save() { Saved = Draft; SavedA.ApplySettings(Saved); Changed?.Invoke(); }
    internal void Revert() { Draft = Saved; Changed?.Invoke(); }
    internal void Discard() { Draft = Saved; Mounted = false; Changed?.Invoke(); }
    internal void Reopen() { Mounted = true; Changed?.Invoke(); }
    private CopilotUsageModel Model(JsonElement? settings) => new(_source, settings,
        message => throw new InvalidOperationException(message), action => action(),
        () => Now, () => _zone, observeClock: false);
    public void Start()
    {
        try
        {
            var reset = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            _source = new(new(CopilotUsageStatus.Ready, new("fixture-account", "github.com", "fake"),
                new(new(30, 100, false, 30.25, reset, reset, false, Now)), null, false, false, Now));
            Saved = Draft = JsonSerializer.SerializeToElement(new { unrelated = "preserved" });
            SavedA = Model(Saved); SavedB = Model(null);
            ConfigurationSource = new((settings, _) => _draftModel = Model(settings), () => { });
            var shell = new FixtureShell();
            typeof(Shell).GetProperty(nameof(Shell.Theme))!.SetValue(shell, Theme);
            CellProps = new(shell, SavedA, null, null, null);
            DraftHost.Owner = this;
            _window = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "Isolated Copilot Usage fixture", Width = 1180, Height = 900,
                NoActivate = true, ActivateOnOpen = false, ShowInTaskbar = false,
                ShowInSwitcher = false, Style = WindowStyle.None,
            }, () => new DraftHost());
            _window.Show();
            var billboard = new UsageBillboard(SavedA);
            typeof(Billboard).GetProperty("OwnerCell", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(billboard, new ShellCellHandle("fixture-usage", typeof(UsageCell), CellProps));
            var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            var anchor = new BillboardAnchor(new Windows.Graphics.RectInt32(
                work.X + 32, work.Y + work.Height - 48, 100, 40), work);
            _billboard = new(BillboardSession.CreateSpec(billboard.Title, billboard.WidthDip, billboard.HeightDip),
                billboard, Theme, () => anchor, billboard.OnOpened, billboard.OnClosed, Console.WriteLine);
            _billboard.Start(); // Production host preparation; fake source Refresh only.
            Step(CheckInitial);
            Step(CheckCalendar);
            Step(CheckStableDateMeasurement);
            CaptureStep("01-dark-default");
            Step(CheckBalancePointerPaths);
            Step(() => OpenBalanceTip());
            CaptureStep("01a-dark-hover-label");
            CaptureStep("01c-dark-balance-tooltip", tooltip: true);
            Step(() =>
            {
                Require(_balanceOpenEvents == 1, "native popup did not open exactly once after production enter");
                _balanceController!.Exit(new Point(20, 12));
                _balanceController.Enter(new Point(60, 12));
            });
            Step(() => Require(_balanceOpenEvents == 1 && _balanceCloseEvents == 0 && _openTip!.IsOpen,
                "loaded popup flickered across child traversal"));
            Step(() => CloseTip());
            Step(() => Require(!_observedBalanceTip!.IsOpen && _balanceCloseEvents == 1 &&
                ToolTipService.GetToolTip(Balance) is null &&
                ((Microsoft.UI.Xaml.Controls.Border)Balance).BorderBrush is null,
                "native popup reopened after exit or retained hover state"));
            Step(() =>
            {
                _window.NativeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1380, 900));
            });
            Step(() =>
            {
                CheckCalendar();
                Require(Math.Abs(CalendarGrid.ActualWidth - _calendarWidth) < .1, "calendar stretched with parent growth");
                OpenDateTip(new(2026, 8, 30));
            });
            CaptureStep("01d-disabled-date-tooltip", tooltip: true);
            Step(() => CloseTip());
            Step(() =>
            {
                Require(_billboard.State == BillboardSessionState.Visible, "production billboard did not complete preparation");
                var root = _billboardWindow.NativeWindow.Content;
                var texts = Descendants<XamlText>(root).Select(t => t.Text).ToArray();
                Require(texts.Contains("Copilot Usage") && texts.Contains("Credits") &&
                    texts.Count(t => t == "Used Percentage") == 1 && !texts.Contains("Used percentage"),
                    "billboard titles/card order or duplicate row");
                var scroll = Descendants<ScrollViewer>(root).Single();
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
            CaptureStep("01b-production-billboard", billboard: true);
            Step(() => Toggle(new(2026, 9, 10)));
            Step(() =>
            {
                Require(_draftModel!.Pace.TotalWorkdays == 21 && _draftModel.Pace.ElapsedWorkdays == 7,
                    "today draft stats did not update");
                Require(SavedA.Pace.TotalWorkdays == 22 && SavedB.Pace.TotalWorkdays == 22,
                    "draft leaked into saved shell");
                CheckCalendar();
                Require(Math.Abs(CalendarGrid.ActualWidth - _calendarWidth) < .1, "draft toggle resized calendar");
                CheckBalance();
                Toggle(new(2026, 9, 12));
            });
            Step(() =>
            {
                Require(_draftModel!.Pace.TotalWorkdays == 22 && _draftModel.Pace.ElapsedWorkdays == 7,
                    "future workday did not update total independently");
                Invoke("Save draft");
            });
            Step(() =>
            {
                Require(SavedA.Pace.ElapsedWorkdays == 7 && SavedB.Pace.ElapsedWorkdays == 8,
                    "Save did not isolate shell schedule");
                Require(Draft!.Value.GetProperty("unrelated").GetString() == "preserved",
                    "unrelated settings lost");
                Toggle(new(2026, 9, 1));
            });
            Step(() =>
            {
                Require(_draftModel!.Pace.ElapsedWorkdays == 6, "past draft toggle did not update elapsed");
                Invoke("Revert draft");
            });
            Step(() =>
            {
                Require(_draftModel!.Pace.ElapsedWorkdays == 7 && !Dirty, "Revert did not restore saved draft");
                Toggle(new(2026, 9, 2));
            });
            Step(() => { OpenBalanceTip(); Invoke("Discard draft"); });
            Step(() =>
            {
                Require(_source.Subscribers == 2, $"unmounted configuration retained source subscription: {_source.Subscribers}");
                Require(!Dirty && SavedA.Pace.ElapsedWorkdays == 7, "discard changed saved runtime");
                Require(!_openTip!.IsOpen && _openTip.Content is null, "first editor unmount leaked tooltip");
                CloseTip();
                Invoke("Reopen editor");
            });
            Step(() =>
            {
                Require(_source.Subscribers == 3, "reopen did not own exactly one model");
                Dark = false; Theme.Set(false); Changed?.Invoke();
            });
            CaptureStep("02-light-saved");
            Step(() => { CheckCalendar(); CheckBalance(); OpenBalanceTip(); });
            CaptureStep("02b-light-balance-tooltip", tooltip: true);
            Step(() => CloseTip());
            Step(() => CheckTint());
            Step(() => { OpenBalanceTip(); Theme.Set(true); }); // No parent render: Settings theme subscription must drive the row.
            Step(() =>
            {
                CheckBalance();
                Require(_openTip!.IsOpen && ReferenceEquals(_openTip, UsageBalanceTooltip.For((Microsoft.UI.Xaml.Controls.Border)Balance).Tip),
                    "theme reconciliation replaced/closed live tooltip");
                CheckTooltipPalette(true, false);
                Theme.Set(false);
            });
            Step(() => { CheckBalance(); CheckTooltipPalette(false, false); CloseTip(); });
            Step(() => CheckAxis(0, 0));
            CaptureStep("03-axis-zero");
            Step(() => CheckAxis(100, 100));
            CaptureStep("04-axis-hundred");
            Step(() => CheckAxis(0, 100));
            Step(() => CheckAxis(100, 0));
            Step(() => CheckAxis(33.333, 66.667));
            Step(() =>
            {
                var axis = Find<UsageAxis>().Single();
                axis.UsedLabel.FontSize = axis.WorkdayLabel.FontSize = 27;
                CheckAxis(100, 100, longLabel: true);
            });
            CaptureStep("05-scaled-text-long-label");
            Step(() =>
            {
                // Outside OS High Contrast these WinUI resources can both be
                // magenta sentinels. Substitute a BLACK/WHITE system-palette
                // fixture in this process only; restore it after this branch.
                _originalResources = Application.Current.Resources;
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(_originalResources);
                resources["SystemColorWindowTextColorBrush"] = new SolidColorBrush(Microsoft.UI.Colors.White);
                resources["SystemColorWindowColorBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Black);
                Application.Current.Resources = resources;
                HighContrast = true; Changed?.Invoke();
            });
            Step(() =>
            {
                var brush = UsageTint.BrushFor(-6, true, true);
                Require(ReferenceEquals(brush, UsageTint.SystemBrush("SystemColorWindowTextColorBrush")),
                    "HC ramp did not use system foreground");
                var row = Find<FrameworkElement>().Single(e =>
                    AutomationProperties.GetAutomationId(e) == "FixtureHighContrastBalance");
                Require(Descendants<XamlText>(row).Take(2).All(t => ReferenceEquals(t.Foreground, brush)),
                    "HC balance dot/text did not use the same system foreground");
                OpenBalanceTip(row);
                Require(Descendants<XamlText>((FrameworkElement)_openTip!.Content)
                    .Where(t => t.Text != UsageCalendarView.RoundingHelp)
                    .All(t => ReferenceEquals(t.Foreground, brush)), "HC tooltip legend kept signal colors");
                Require(((Microsoft.UI.Xaml.Controls.Border)row).BorderThickness.Left == 2 &&
                    ReferenceEquals(((Microsoft.UI.Xaml.Controls.Border)row).BorderBrush, brush),
                    "HC hover border not a reserved system-color border");
                CheckAxis(100, 100);
            });
            CaptureStep("06b-system-color-tooltip", tooltip: true);
            Step(() => CloseTip());
            CaptureStep("06-system-color-branch");
            Step(() =>
            {
                HighContrast = false;
                Application.Current.Resources = _originalResources!;
                _source.Publish(_source.State with { Usage = new(_source.State.Usage.PremiumInteractions! with { UsedPercentage = 0 }) });
                Changed?.Invoke();
            });
            Step(() =>
            {
                Require(SavedA.Pace.BalanceText == "You have a surplus of 7 days’ worth of credits.", "surplus label must retain integer beyond six");
                CheckRenderedTint();
            });
            CaptureStep("07-surplus-green");
            Step(() =>
            {
                _source.Publish(_source.State with { Usage = new(_source.State.Usage.PremiumInteractions! with { UsedPercentage = 100 }) });
                Changed?.Invoke();
            });
            Step(() =>
            {
                Require(SavedA.Pace.BalanceText == "You’re over budget by 15 days’ worth of credits.", "debt label must retain integer beyond six");
                CheckRenderedTint();
            });
            CaptureStep("08-debt-red");
            Step(() => _window.NativeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 900)));
            Step(() =>
            {
                CheckBalanceWidth();
                Require(CalendarScroller.ScrollableWidth > 0, "narrow calendar lost horizontal scrolling");
                OpenBalanceTip();
                Require(((FrameworkElement)_openTip!.Content).ActualWidth < 720, "narrow tooltip not constrained");
                Require(_openTip.ActualWidth <= Root.XamlRoot.Size.Width - 31, "narrow tooltip escapes window");
            });
            CaptureStep("08b-narrow-tooltip", tooltip: true);
            CaptureStep("08c-narrow-label");
            Step(() =>
            {
                var width = CalendarGrid.ActualWidth;
                foreach (var text in Descendants<XamlText>(Balance)) text.FontSize = 35;
                Root.UpdateLayout();
                CheckBalanceWidth();
                Require(Math.Abs(CalendarGrid.ActualWidth - width) < .1, "long scaled sentence widened date columns");
                Require(Balance.ActualHeight > 80, "scaled balance sentence did not wrap in narrow viewport");
            });
            CaptureStep("08e-narrow-scaled-label");
            Step(() =>
            {
                CloseTip();
                foreach (var text in Descendants<XamlText>(Balance)) text.FontSize = 14;
                _window.NativeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1380, 900));
            });
            Step(() =>
            {
                OpenBalanceTip();
                foreach (var text in Descendants<XamlText>((FrameworkElement)_openTip!.Content)) text.FontSize = 28;
                Root.UpdateLayout();
            });
            Step(() =>
            {
                ((FrameworkElement)_openTip!.Content).UpdateLayout();
                Require(Math.Abs(((FrameworkElement)_openTip.Content).ActualWidth - 600) < .1,
                    "scaled tooltip lost target width");
                foreach (var text in Descendants<XamlText>((FrameworkElement)_openTip.Content))
                    Require(!text.IsTextTrimmed && text.DesiredSize.Height <= Math.Ceiling(text.ActualHeight),
                        $"scaled tooltip text clipped: {text.Text}, desired={text.DesiredSize}, actual={text.ActualWidth}x{text.ActualHeight}");
            });
            CaptureStep("08d-scaled-tooltip", tooltip: true);
            Step(CloseTip);
            Step(() =>
            {
                foreach (var button in Find<ToggleButton>()) button.FontSize = 28;
                foreach (var text in Descendants<XamlText>(Balance)) text.FontSize = 28;
                Root.UpdateLayout();
                Require(CalendarGrid.ActualWidth > _calendarWidth, "scaled date typography did not grow measured columns");
                CheckBalanceWidth();
                foreach (var button in Find<ToggleButton>())
                {
                    var content = (UsageDateContent)button.Content;
                    foreach (var text in new[] { content.DateLabel, content.TemporalLabel })
                    {
                        Require(text.ActualWidth <= button.ActualWidth + .1, "scaled calendar label escapes its column");
                        Require(!text.IsTextTrimmed, "scaled date text was trimmed");
                        var bottom = text.TransformToVisual(button).TransformPoint(new()).Y + text.ActualHeight;
                        Require(bottom <= button.ActualHeight + .1, "scaled calendar label clips vertically");
                    }
                }
            });
            CaptureStep("09-scaled-calendar");
            Step(() =>
            {
                var scroll = Find<ScrollViewer>().First(s =>
                    AutomationProperties.GetAutomationId(s) != "UsageCalendarScroller");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
            CaptureStep("09b-scaled-calendar-bottom");
            Step(() =>
            {
                foreach (var size in new[] { 21d, 35d })
                {
                    foreach (var button in Find<ToggleButton>()) button.FontSize = size;
                    Root.UpdateLayout();
                    CheckBalanceWidth();
                    var buttons = Find<ToggleButton>().ToArray();
                    Require(buttons.All(b => Math.Abs(b.ActualWidth - buttons[0].ActualWidth) < .1),
                        "scaled columns lost equal width");
                    foreach (var button in buttons)
                    {
                        var content = (UsageDateContent)button.Content;
                        Require(content.DateLabel.DesiredSize.Width <= content.ActualWidth + .1 &&
                            content.DateLabel.ActualHeight + content.TemporalLabel.ActualHeight <= content.ActualHeight + .1,
                            "150%/250% font stress clipped a date");
                    }
                    Console.WriteLine($"Calendar font stress: {size / 14:P0}, grid={CalendarGrid.ActualWidth:F2}, cell={buttons[0].ActualWidth:F2}x{buttons[0].ActualHeight:F2}");
                }
                Require(CalendarScroller.ScrollableWidth > 0, "large text overflow is not horizontally reachable");
                CalendarScroller.ChangeView(CalendarScroller.ScrollableWidth, null, null, true);
            });
            CaptureStep("09c-largest-text-scroll-right");
            Step(() =>
            {
                HighContrast = false; Changed?.Invoke();
                _source.Publish(_source.State with { Usage = new(_source.State.Usage.PremiumInteractions! with { ResetIsEstimated = true }) });
            });
            Step(() =>
            {
                Require(!CalendarTexts.Any(t => t.Contains("Estimated cycle") || t.Contains("inferred")),
                    "Config must not show a period explanation");
                Require(!Find<XamlText>().Any(t => t.Text.Contains("Estimated cycle") || t.Text.Contains("Estimated reset")),
                    "Billboard must not show estimated cycle/reset rows");
                var allRest = CopilotUsageSettings.Default;
                foreach (var day in SavedA.Pace.Cycle!.EligibleDays.Where(d => CopilotUsageSettings.DefaultWorkday(d.Date)))
                    allRest = allRest.Toggle(day.Date);
                SavedA.ApplySettings(allRest.Write(null)); Changed?.Invoke();
            });
            Step(() =>
            {
                Require(SavedA.Pace.BalanceText == "No workdays configured", "all-rest comparison false success");
                Require(Find<XamlText>().Any(t => t.Text == "No workdays configured"), "all-rest message not rendered");
                Now = Now.AddDays(1); SavedB.ObserveClock();
                Require(SavedB.Pace.ElapsedWorkdays == 9, "clock did not advance local workday");
                Now = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(8));
                SavedB.ObserveClock();
                Require(SavedB.Pace.UsedPercentage is null && _source.Refreshes > 0, "expired cycle retained Credits comparison");
                OpenBalanceTip();
                _oldBalance = Balance;
                Invoke("Discard draft");
            });
            Step(() =>
            {
                Require(_source.Subscribers == 2, "second draft unmount leaked");
                Require(_openTip is { IsOpen: false }, "rich tooltip remained open after calendar unmount");
                Require(_openTip!.Content is null && _openTip.PlacementTarget is null, "tooltip retains content/owner after unmount");
                _balanceController!.Enter(new Point(10, 10));
                Require(!_openTip.IsOpen, "disposed controller reopened from stale input");
                Require(_oldBalance!.XamlRoot is null || !_oldBalance.IsLoaded, "calendar row retained in visual tree");
                SavedA.Dispose(); SavedB.Dispose();
                Require(_source.Subscribers == 0, "shell models retained fake source");
                _billboard.Close();
                Terminal = true; Changed?.Invoke();
            });
            Step(() =>
            {
                Require(Theme.Subscribers == 0, "cell retained sampled taskbar theme subscription");
                Finish();
            });
            _timer = ReactorApp.UIDispatcher!.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += async (_, _) =>
            {
                if (_busy || _finished) return;
                _busy = true;
                try { await _steps.Dequeue()(); } catch (Exception ex) { Fail(ex.ToString()); }
                finally { _busy = false; }
            };
            _timer.Start();
        }
        catch (Exception ex) { Fail(ex.ToString()); }
    }

    private void CheckInitial()
    {
        Require(_source.Subscribers == 3, "source subscription count");
        Require(SavedA.Pace.TotalWorkdays == 22 && SavedA.Pace.ElapsedWorkdays == 8, "September baseline");
        var toggles = Find<ToggleButton>().ToArray();
        Require(toggles.Length == 35, "calendar must contain five complete seven-day rows");
        Require(!toggles[0].IsEnabled && AutomationProperties.GetName(toggles[0]).Contains("Sunday, August 30") &&
            AutomationProperties.GetName(toggles[^1]).Contains("Saturday, October 3"),
            "calendar not Sunday-first with disabled outside dates");
        Require(toggles.Count(t => t.IsEnabled) == 30, "cycle dates incorrect");
        Require(toggles.All(t => !string.IsNullOrEmpty(AutomationProperties.GetName(t))), "calendar date missing accessible name");
        Require(toggles.Where(t => t.IsEnabled).All(t => t.IsTabStop), "enabled date not keyboard accessible");
        Require(AutomationProperties.GetName(DateButton(new(2026, 9, 10))).Contains("Today"), "today not exposed");
        var before = Draft?.GetRawText();
        try { ((IToggleProvider)new ToggleButtonAutomationPeer(toggles[0]).GetPattern(PatternInterface.Toggle)).Toggle(); }
        catch (ElementNotEnabledException) { }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040200)) { }
        Require(before == Draft?.GetRawText(), "outside date accepted automation toggle");
        var pulse = Descendants<UsagePulse>(CellRoot!).Single();
        var usageLabel = Descendants<XamlText>(CellRoot!).Single(t => t.Text.EndsWith('%'));
        var metricsLabel = Descendants<XamlText>(MetricsRoot!).Single(t => t.Text == "CPU");
        var usageBottom = usageLabel.TransformToVisual(CellRoot).TransformPoint(new()).Y + usageLabel.ActualHeight;
        var metricsBottom = metricsLabel.TransformToVisual(MetricsRoot).TransformPoint(new()).Y + metricsLabel.ActualHeight;
        Require(Math.Abs(usageBottom - metricsBottom) < 0.6, $"bottom baseline differs: {usageBottom}/{metricsBottom}");
        Require(pulse.VisibleBounds.Height > 12, "pulse not enlarged above original 7.68 DIP visible height");
        Require(usageBottom <= 48, "usage baseline clips outside taskbar slot");
        Require(Descendants<ProgressBar>(CellRoot!).Single().ActualHeight == 3, "top gauge not 3 DIP");
        Console.WriteLine($"Cell: visible pulse {pulse.VisibleBounds.Width:F2}x{pulse.VisibleBounds.Height:F2} DIP; bottoms Usage={usageBottom:F2}, Metrics={metricsBottom:F2}. RasterizationScale={Root.XamlRoot.RasterizationScale}");
        _calendarWidth = CalendarGrid.ActualWidth;
    }

    private Microsoft.UI.Xaml.Controls.Grid CalendarGrid => Find<Microsoft.UI.Xaml.Controls.Grid>()
        .Single(g => AutomationProperties.GetAutomationId(g) == "UsageCalendarGrid");
    private ScrollViewer CalendarScroller => Find<ScrollViewer>()
        .Single(g => AutomationProperties.GetAutomationId(g) == "UsageCalendarScroller");
    private FrameworkElement CalendarSection => Find<FrameworkElement>()
        .Single(g => AutomationProperties.GetAutomationId(g) == "UsageCalendarSection");
    private FrameworkElement Balance => Find<FrameworkElement>()
        .Single(e => AutomationProperties.GetAutomationId(e) == "UsageCalendarBalance");
    private string[] CalendarTexts => Descendants<XamlText>(CalendarSection).Select(t => t.Text).ToArray();

    private void CheckStableDateMeasurement()
    {
        var content = (UsageDateContent)DateButton(new(2026, 9, 10)).Content;
        foreach (var label in new[] { "May 31", "Sep 30", "Nov 30", "Dec 31", "Jan 1" })
        {
            foreach (var temporal in new[] { "", "✓", "Today" })
            {
                content.Update(label, temporal);
                Root.UpdateLayout();
                Require(Math.Abs(CalendarGrid.ActualWidth - _calendarWidth) < .1,
                    "month/date/temporal label changed the stable calendar width");
                Require(content.DateLabel.DesiredSize.Width <= content.ActualWidth + .1,
                    "long month/date label clipped");
            }
        }
        content.Update("Sep 10", "Today");
        Root.UpdateLayout();
    }

    private void CheckCalendar()
    {
        var texts = CalendarTexts;
        var pace = _draftModel!.Pace;
        Require(texts[0] == "Workday calendar" &&
            texts[1] == "This period: Sep 1, 2026 - Sep 30, 2026" &&
            texts[2] == "●" && texts[3] == pace.BalanceText &&
            texts[4] == $"{pace.TotalWorkdays} total workdays · {pace.ElapsedWorkdays} elapsed through today",
            "Config header ordering/text incorrect");
        Require(texts.Skip(5).Take(7).SequenceEqual(new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }),
            "weekday headings not Sunday first");
        Require(!texts.Any(t => t.Contains("assumption") || t.Contains("inferred") || t.Contains("Estimated cycle") ||
            t.Contains("counts as a full") || t.Contains("cumulative") || t.Contains("by default") ||
            t is "Work" or "Rest" or "Outside cycle"), "removed Config explanation or state label visible");
        var buttons = Find<ToggleButton>().ToArray();
        Require(buttons.All(b => Math.Abs(b.ActualWidth - buttons[0].ActualWidth) < .1), "calendar columns unequal");
        Require(CalendarGrid.ActualWidth < CalendarSection.ActualWidth - 16,
            "calendar not compact relative to parent");
        Require(Math.Abs(CalendarGrid.TransformToVisual(CalendarSection).TransformPoint(new()).X) < .1,
            "calendar not left aligned");
        double? lineHeight = null;
        foreach (var day in _draftModel!.Pace.Cycle!.Days)
        {
            var button = DateButton(day.Date);
            var content = (UsageDateContent)button.Content;
            Require(content.Children.Count == 2, "calendar date must have exactly two text lines");
            Require(content.DateLabel.Text == day.Date.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
                "date must retain month and day");
            Require(content.TemporalLabel.Text == (day.Date < _draftModel.Pace.Today ? "✓" :
                day.Date == _draftModel.Pace.Today ? "Today" : ""), "incorrect temporal line including outside/weekend dates");
            var reserved = content.TemporalLabel.ActualHeight;
            lineHeight ??= reserved;
            Require(reserved > 0 && Math.Abs(reserved - lineHeight.Value) < .1, "blank temporal line not reserved consistently");
            Require(!content.DateLabel.IsTextTrimmed && content.DateLabel.DesiredSize.Width <= content.ActualWidth + .1,
                "calendar date clipped");
            Require(button.IsChecked == (day.IsEligible && day.IsWorkday), "native toggle highlight incorrect");
            var tip = ToolTipService.GetToolTip(button.Parent);
            var tipText = tip is ToolTip tooltip ? tooltip.Content?.ToString() : tip?.ToString();
            Require(tipText?.Contains(day.IsWorkday ? "Workday" : "Not a workday") == true &&
                tipText.Contains(day.Date.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"))),
                "date tooltip missing full date/workday state");
            Require(day.IsEligible || tipText!.Contains("Outside this period, cannot be changed"),
                "outside date tooltip qualification absent");
        }
        Console.WriteLine($"Calendar: width={CalendarGrid.ActualWidth:F2}, cell={buttons[0].ActualWidth:F2}x{buttons[0].ActualHeight:F2}, temporal={lineHeight:F2}.");
        CheckBalance();
    }

    private void CheckBalance()
    {
        var texts = Descendants<XamlText>(Balance).ToArray();
        Require(texts.Length == 3 && texts[0].Text == "●" && texts[1].Text == _draftModel!.Pace.BalanceText &&
            texts[2].Text == $"{_draftModel.Pace.TotalWorkdays} total workdays · {_draftModel.Pace.ElapsedWorkdays} elapsed through today",
            "balance row not using shared draft sentence");
        Require(texts.Take(2).All(t => ((SolidColorBrush)t.Foreground).Color ==
            UsageTint.ColorFor(_draftModel!.Pace.RoundedBalance, Theme.IsDark)), "draft tint or effective Settings theme incorrect");
        Require(AutomationProperties.GetHelpText(Balance) == UsageCalendarView.BalanceHelp, "balance accessible help absent");
        CheckBalanceWidth();
        var origin = Balance.TransformToVisual(Root).TransformPoint(new());
        foreach (var x in new[] { 2d, texts[0].ActualWidth + 4, Balance.ActualWidth - 2 })
            Require(VisualTreeHelper.FindElementsInHostCoordinates(new Point(origin.X + x, origin.Y + 2), Root)
                .Contains(Balance), "balance row gap/circle/end not hit-testable");
    }

    private void CheckBalanceWidth()
    {
        Require(Math.Abs(Balance.ActualWidth - Math.Min(CalendarGrid.ActualWidth, CalendarScroller.ViewportWidth)) < .1,
            $"balance outer width {Balance.ActualWidth:F2} != calendar/viewport {CalendarGrid.ActualWidth:F2}/{CalendarScroller.ViewportWidth:F2}");
        Require(Math.Abs(Balance.TransformToVisual(CalendarSection).TransformPoint(new()).X) < .1,
            "balance left edge differs from calendar");
    }

    private void CheckBalancePointerPaths()
    {
        var row = (Microsoft.UI.Xaml.Controls.Border)Balance;
        var controller = UsageBalanceTooltip.For(row);
        Require(row.BorderBrush is null && row.BorderThickness.Left == 1, "idle border must be invisible but reserved");
        Require(ToolTipService.GetToolTip(row) is null, "balance has duplicate native dwell tooltip");
        var before = (row.ActualWidth, row.ActualHeight, CalendarGrid.TransformToVisual(Root).TransformPoint(new()).Y);
        var texts = Descendants<XamlText>(row).ToArray();
        var origin = row.TransformToVisual(Root).TransformPoint(new());
        var circle = texts[0].TransformToVisual(row).TransformPoint(new());
        var sentence = texts[1].TransformToVisual(row).TransformPoint(new());
        // Padding/corners, glyph, inter-child gap, sentence and unused tail.
        var points = new[] { new Point(1, 1), new Point(4, row.ActualHeight / 2),
            new Point(circle.X + 2, circle.Y + 2),
            new Point(sentence.X - 4, sentence.Y + 2),
            new Point(sentence.X + 8, sentence.Y + 4),
            new Point(row.ActualWidth - 2, row.ActualHeight - 2) };
        foreach (var point in points)
        {
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(
                new Point(origin.X + point.X, origin.Y + point.Y), Root).ToArray();
            Require(hits.Contains(row) && !hits.OfType<XamlText>().Any(), "rectangle must be one hit target, not child boundaries");
            controller.Enter(point);
            // Deliberately before UpdateLayout, await, timer tick or message pumping.
            Require(controller.Tip.IsOpen && row.BorderBrush is not null, "production enter did not open synchronously");
            controller.Exit(points[2]); // Simulated child-boundary exit inside owner.
            controller.Enter(points[4]);
            Require(controller.Tip.IsOpen, "child transition closed tooltip");
            Root.UpdateLayout();
            Require(before == (row.ActualWidth, row.ActualHeight, CalendarGrid.TransformToVisual(Root).TransformPoint(new()).Y),
                "hover changed label/calendar geometry");
            controller.Exit(new Point(-1, point.Y));
            Require(!controller.Tip.IsOpen && row.BorderBrush is null, "production exit failed to close synchronously");
            Require(ToolTipService.GetToolTip(row) is null, "exit retained native dwell target");
        }
        controller.Enter(points[0]);
        controller.Cancel();
        Require(!controller.Tip.IsOpen && row.BorderBrush is null, "cancel left hover state");
        Console.WriteLine($"Balance pointer paths: {points.Length} hit regions, immediate IsOpen; child traversal stable. No physical pointer injection.");
    }

    private void OpenBalanceTip(FrameworkElement? row = null)
    {
        row ??= Balance;
        _tipOwner = row;
        _balanceController = UsageBalanceTooltip.For((Microsoft.UI.Xaml.Controls.Border)row);
        _openTip = _balanceController.Tip;
        if (!ReferenceEquals(_observedBalanceTip, _openTip))
        {
            if (_observedBalanceTip is not null)
            {
                _observedBalanceTip.Opened -= BalanceOpened;
                _observedBalanceTip.Closed -= BalanceClosed;
            }
            _observedBalanceTip = _openTip;
            _balanceOpenEvents = _balanceCloseEvents = 0;
            _openTip.Opened += BalanceOpened;
            _openTip.Closed += BalanceClosed;
        }
        _balanceController.Enter(new Point(10, 10));
        Require(_openTip.IsOpen, "production enter must set IsOpen synchronously, before layout/timer tick");
        Root.UpdateLayout();
        Require(Math.Abs(_openTip.HorizontalOffset -
            Math.Max(0, (_openTip.ActualWidth - row.ActualWidth) / 2)) < .1,
            "balance tooltip is not horizontally aligned with the label's left edge");
        var content = (FrameworkElement)_openTip.Content;
        var body = (StackPanel)((ScrollViewer)content).Content;
        var texts = Descendants<XamlText>(body).ToArray();
        Require(body.Children.Count == 4, "tooltip body must have three rows and one paragraph");
        Require(texts.Select(t => t.Text).SequenceEqual(new[] { "●", UsageCalendarView.SurplusHelp,
            "●", UsageCalendarView.BudgetHelp, "●", UsageCalendarView.NeutralHelp, UsageCalendarView.RoundingHelp }),
            "tooltip must contain exactly three bullet sentences and only the parenthetical paragraph: " +
                string.Join(" | ", texts.Select(t => t.Text)));
        foreach (var text in texts.Where(t => t.Inlines.Count == 2))
            Require(((Microsoft.UI.Xaml.Documents.Run)text.Inlines[0]).FontWeight == Microsoft.UI.Text.FontWeights.SemiBold,
                "tooltip title not emphasized");
        Require(!Descendants<Expander>(content).Any(), "tooltip must not contain interactive expanders");
        if (Root.XamlRoot.Size.Width >= 800)
            Require(Math.Abs(content.ActualWidth - 600) < .1 && _openTip.ActualWidth >= 600,
                $"actual tooltip width capped: content={content.ActualWidth}, tooltip={_openTip.ActualWidth}");
        if (row == Balance && _openTip.Placement == PlacementMode.Top)
            Require(_openTip.VerticalOffset <= 0, "balance tooltip is not placed above the label");
        Require(ReferenceEquals(ToolTipService.GetToolTip(row), _openTip),
            "native service must reference only the already-open production tooltip, never a private duplicate");
        Require(_openTip.IsOpen && content.ActualWidth > 0,
            $"rich tooltip did not open/measure: open={_openTip.IsOpen}, loaded={content.IsLoaded}, width={content.ActualWidth}");
        Console.WriteLine($"Tooltip: content={content.ActualWidth:F2}, outer={_openTip.ActualWidth:F2}, height={_openTip.ActualHeight:F2}, window={Root.XamlRoot.Size.Width:F2}");
        Console.WriteLine($"Theme: root={Root.ActualTheme}, row={row.ActualTheme}, tip={_openTip.ActualTheme}, tip background={(_openTip.Background as SolidColorBrush)?.Color}");
        if (row == Balance)
        {
            CheckTooltipPalette(Theme.IsDark, false);
            var theme = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[Theme.IsDark ? "Default" : "Light"];
            Require(((_openTip.Background as SolidColorBrush)?.Color) == (Windows.UI.Color)theme["SolidBackgroundFillColorBase"],
                "tooltip surface uses application-default rather than effective Settings theme");
        }
    }

    private void BalanceOpened(object sender, RoutedEventArgs e) => _balanceOpenEvents++;
    private void BalanceClosed(object sender, RoutedEventArgs e) => _balanceCloseEvents++;

    private void OpenDateTip(DateOnly date)
    {
        var owner = (FrameworkElement)DateButton(date).Parent;
        var value = ToolTipService.GetToolTip(owner);
        _tipOwner = owner;
        _tipValue = value;
        _openTip = value as ToolTip ?? new ToolTip { Content = value };
        ToolTipService.SetToolTip(owner, _openTip);
        _openTip.PlacementTarget = owner;
        _openTip.IsOpen = true;
        Root.UpdateLayout();
        Require(_openTip.IsOpen, "disabled date wrapper tooltip did not open");
    }

    private void CheckTooltipPalette(bool dark, bool highContrast)
    {
        var texts = Descendants<XamlText>((StackPanel)((ScrollViewer)_openTip!.Content).Content).ToArray();
        foreach (var (index, balance) in new[] { (0, 6), (2, -6), (4, 0) })
        {
            var expected = UsageTint.BrushFor(balance, dark, highContrast);
            Require(((SolidColorBrush)texts[index].Foreground).Color == ((SolidColorBrush)expected).Color &&
                ReferenceEquals(texts[index].Foreground, texts[index + 1].Foreground),
                "tooltip circle/sentence no longer share current palette");
        }
    }

    private void CloseTip()
    {
        if (_balanceController is not null)
        {
            _balanceController.Exit(new Point(-1, -1));
            Require(!_openTip!.IsOpen, "production tooltip exit failed");
            _balanceController = null;
            _openTip = null;
            _tipOwner = null;
            return;
        }
        _openTip!.IsOpen = false;
        ToolTipService.SetToolTip(_tipOwner!, null);
        _openTip.Content = null;
        ToolTipService.SetToolTip(_tipOwner!, _tipValue);
        _openTip = null;
        _tipOwner = null;
        _tipValue = null;
    }

    private void CheckTint()
    {
        foreach (bool dark in new[] { true, false })
        {
            Require(UsageTint.ColorFor(null, dark) == UsageTint.ColorFor(0, dark), "unavailable tint not neutral");
            Require(UsageTint.ColorFor(6, dark) == UsageTint.ColorFor(16, dark), "surplus not saturated at six");
            Require(UsageTint.ColorFor(-6, dark) == UsageTint.ColorFor(-16, dark), "debt not saturated at six");
            Require(UsageTint.ColorFor(0, dark) != UsageTint.ColorFor(1, dark), "ramp omitted first day");
            Require(UsageTint.ColorFor(1, dark) != UsageTint.ColorFor(-1, dark), "debt and surplus colors equal");
        }

        Require((SavedA.Pace with { RoundedBalance = 12 }).BalanceText == "You have a surplus of 12 days’ worth of credits.", "balance label incorrectly saturated");
    }

    private void CheckRenderedTint()
    {
        var expected = UsageTint.ColorFor(SavedA.Pace.RoundedBalance, Theme.IsDark);
        var icon = Descendants<Microsoft.UI.Xaml.Controls.BitmapIcon>(CellRoot!).Single();
        Require(((SolidColorBrush)icon.Foreground).Color == expected, "pulse tint disagrees with displayed rounded balance");
        var axis = Find<UsageAxis>().Single();
        var bookmark = Descendants<Microsoft.UI.Xaml.Shapes.Polygon>(axis).Single();
        Require(((SolidColorBrush)bookmark.Fill).Color == expected, "bookmark tint disagrees with pulse");
    }

    private void CheckAxis(double used, double work, bool longLabel = false)
    {
        var axis = Find<UsageAxis>().Single();
        var pace = SavedA.Pace with { UsedPercentage = used, WorkdayPercentage = work };
        if (longLabel) pace = pace with { TotalWorkdays = 123456789, ElapsedWorkdays = 123456789 };
        axis.Update(pace, Dark, HighContrast);
        Root.UpdateLayout();
        Require(Math.Abs(axis.UsedX - (8 + axis.TrackWidth * used / 100)) < .01, "Credits marker moved off raw percentage");
        Require(Math.Abs(axis.WorkdayX - (8 + axis.TrackWidth * work / 100)) < .01, "workday marker uses another axis");
        foreach (var label in new[] { axis.UsedLabel, axis.WorkdayLabel })
        {
            var point = label.TransformToVisual(axis).TransformPoint(new());
            Require(point.X >= -.1 && point.X + label.ActualWidth <= axis.ActualWidth + .1, "label clipped at endpoint");
            Require(point.Y + label.ActualHeight <= axis.ActualHeight + .1,
                $"scaled label clipped vertically: y={point.Y} h={label.ActualHeight} axis={axis.ActualHeight} children={axis.Children.Count}");
        }
        var top = axis.UsedLabel.TransformToVisual(axis).TransformPoint(new());
        var bottom = axis.WorkdayLabel.TransformToVisual(axis).TransformPoint(new());
        Require(top.Y + axis.UsedLabel.ActualHeight < bottom.Y, "upper and lower labels collide");
        Console.WriteLine($"Axis {used}/{work}: width={axis.ActualWidth:F2}, track={axis.TrackWidth:F2}, markers={axis.UsedX:F2}/{axis.WorkdayX:F2}, height={axis.ActualHeight:F2}");
    }

    private ToggleButton DateButton(DateOnly date) => Find<ToggleButton>().Single(t =>
        AutomationProperties.GetName(t).Contains(date.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"))));
    private void Toggle(DateOnly date) => ((IToggleProvider)new ToggleButtonAutomationPeer(DateButton(date))
        .GetPattern(PatternInterface.Toggle)).Toggle();
    private void Invoke(string content)
    {
        var button = Find<Microsoft.UI.Xaml.Controls.Button>().Single(b => b.Content?.ToString() == content);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private IEnumerable<T> Find<T>() where T : DependencyObject => Descendants<T>(Root);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private void Step(Action action) => _steps.Enqueue(() => { action(); return Task.CompletedTask; });
    private void CaptureStep(string name, bool billboard = false, bool tooltip = false) => _steps.Enqueue(async () =>
    {
        Directory.CreateDirectory(_output);
        byte[] pixels;
        int width, height;
        if (billboard)
        {
            // Acrylic is compositor-owned and RenderTargetBitmap cannot capture
            // it faithfully. Read only the isolated fixture billboard bounds.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_billboardWindow.NativeWindow);
            (pixels, width, height) = await Task.Run(() => WindowPixels(hwnd));
            bool visiblePixel = false;
            for (int i = 0; i < pixels.Length; i += 4)
                visiblePixel |= pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0;
            if (!visiblePixel)
            {
                File.Delete(Path.Combine(_output, name + ".png"));
                Console.WriteLine("LIMITATION: compositor billboard screenshot unavailable (screen read returned uniform black). " +
                    "Production host geometry/text checked; same card is captured in the isolated solid-surface window.");
                return;
            }
        }
        else
        {
            var bitmap = new RenderTargetBitmap();
            UIElement target = Root;
            if (tooltip)
            {
                Require(_openTip!.IsOpen, "tooltip closed before screenshot");
                target = _openTip.Content as UIElement ?? Descendants<XamlText>(_openTip).First();
                Require(target is FrameworkElement { IsLoaded: true }, "tooltip content never loaded into popup");
                // RenderTargetBitmap cannot render the popup ToolTip object
                // itself. Its template root can render the real native chrome.
                while (VisualTreeHelper.GetParent(target) is FrameworkElement parent && parent != _openTip)
                    target = parent;
            }
            await bitmap.RenderAsync(target);
            pixels = (await bitmap.GetPixelsAsync()).ToArray();
            width = bitmap.PixelWidth; height = bitmap.PixelHeight;
            Require(width > 0 && height > 0, $"screenshot target has no pixels: {target.GetType()}, tooltip={_openTip?.IsOpen}");
        }
        var file = await StorageFile.GetFileFromPathAsync(CreateCaptureFile(name));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        Console.WriteLine($"Screenshot: {file.Path} ({width}x{height})");
    });
    private string CreateCaptureFile(string name)
    {
        var path = Path.Combine(_output, name + ".png");
        using (File.Create(path)) { }
        return path;
    }
    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Finish()
    {
        _finished = true; _timer?.Stop();
        Console.WriteLine($"PASS: Copilot Usage presentation {_checks} checks. Fake source only; no SDK/auth/settings IO.");
        Console.WriteLine("Limitations: balance tests invoke the production Enter/Exit/Cancel paths with local coordinates and validate native hit testing, not physical routed pointer delivery. UIA toggles are keyboard-equivalent, not physical key injection. High Contrast uses an isolated black/white system-resource fixture, not an OS theme change. Font-size stress is local, not the OS text setting. Only the current display's rasterization scale is measured.");
        _window.Close();
        ReactorApp.Exit(0); Environment.Exit(0);
    }
    public void Fail(string error)
    {
        _finished = true; _timer?.Stop(); Console.Error.WriteLine(error);
        ReactorApp.Exit(1); Environment.Exit(1);
    }

    private static (byte[], int, int) WindowPixels(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Fixture window bounds unavailable");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = 0, old = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new InvalidOperationException("Fixture screenshot allocation failed");
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020))
                throw new InvalidOperationException("Fixture screenshot copy failed");
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return (pixels, width, height);
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
}
