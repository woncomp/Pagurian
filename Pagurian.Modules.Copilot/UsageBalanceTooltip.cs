using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Pagurian.Modules.Copilot;

internal sealed record UsageCalendarSurfaceProps(CopilotUsagePace Pace, Action<DateOnly> Toggle,
    bool Dark, bool HighContrast);

internal sealed class UsageCalendarSurface : Component<UsageCalendarSurfaceProps>
{
    public override Element Render()
    {
        var layout = UseMemo(() => new UsageCalendarWidth());
        UseEffect(() => (Action)layout.Dispose, layout);
        return UsageCalendarView.RenderSurface(Props.Pace, Props.Toggle, Props.Dark, Props.HighContrast, layout);
    }
}

// Observe actual layout, never a second date-width formula. The label starts at
// zero width so its sentence cannot contribute to the calendar's desired width.
// LayoutUpdated also covers ScrollViewer viewport changes without an outer
// SizeChanged (e.g. scrollbar changes). Writes are guarded; no queued callbacks.
internal sealed class UsageCalendarWidth : IDisposable
{
    private Border? _balance;
    private FrameworkElement? _grid;
    private ScrollViewer? _scroller;

    internal void AttachBalance(Border balance)
    {
        _balance = balance;
        balance.Width = 0;
        Update(null, null!);
    }

    internal void AttachGrid(FrameworkElement grid) { _grid = grid; }

    internal void AttachScroller(ScrollViewer scroller)
    {
        if (_scroller is not null) _scroller.LayoutUpdated -= Update;
        _scroller = scroller;
        scroller.LayoutUpdated += Update;
    }

    private void Update(object? sender, object args)
    {
        if (_balance is null || _grid is null || _scroller is null) return;
        var width = Math.Min(_grid.ActualWidth, _scroller.ViewportWidth);
        if (double.IsFinite(width) && width > 0 && Math.Abs(_balance.Width - width) > .01)
            _balance.Width = width;
    }

    public void Dispose()
    {
        if (_scroller is not null) _scroller.LayoutUpdated -= Update;
        _balance = null;
        _grid = null;
        _scroller = null;
    }
}

// WinUI ToolTipService has a dwell timer and .WithToolTip creates a private
// wrapper. One controller owns one explicit native tooltip/content tree,
// exclusively, with no second Reactor renderer or reparented DSL content.
// Reactor owns only the label and calls Attach/Detach on reconciliation/unmount.
internal sealed class UsageBalanceTooltip : IDisposable
{
    private static readonly ConditionalWeakTable<Border, UsageBalanceTooltip> Controllers = new();
    private readonly Border _owner;
    private readonly StackPanel _content = new() { Spacing = 8 };
    private readonly ScrollViewer _viewport;
    private readonly List<(TextBlock Bullet, TextBlock Text, int Balance)> _rows = [];
    private readonly TextBlock _rounding;
    private Brush? _hoverBrush;
    private bool _disposed, _hovered;
    internal ToolTip Tip { get; }

    internal static UsageBalanceTooltip For(Border owner) => Controllers.GetValue(owner, b => new(b));
    internal static void Attach(Border owner, bool dark, bool highContrast) => For(owner).Update(dark, highContrast);
    internal static void Detach(FrameworkElement owner)
    {
        if (owner is Border border && Controllers.TryGetValue(border, out var controller))
        {
            controller.Dispose();
            Controllers.Remove(border);
        }
    }

    private UsageBalanceTooltip(Border owner)
    {
        _owner = owner;
        AddRow(UsageCalendarView.SurplusHelp, 6);
        AddRow(UsageCalendarView.BudgetHelp, -6);
        AddRow(UsageCalendarView.NeutralHelp, 0);
        _rounding = BodyText(UsageCalendarView.RoundingHelp);
        _content.Children.Add(_rounding);
        _viewport = new ScrollViewer
        {
            Content = _content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Tip = new ToolTip
        {
            Content = _viewport,
            Placement = PlacementMode.Top,
            Padding = new Thickness(12),
            // Override the native default width cap, not just content.MaxWidth.
            MaxWidth = double.PositiveInfinity,
            IsHitTestVisible = false,
        };
        owner.PointerEntered += PointerEntered;
        owner.PointerExited += PointerExited;
        owner.PointerCanceled += PointerCanceled;
        owner.Unloaded += Unloaded;
        owner.SizeChanged += SizeChanged;
        Tip.SizeChanged += TipSizeChanged;
    }

    private static TextBlock BodyText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.WrapWholeWords,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        VerticalAlignment = VerticalAlignment.Top,
    };

    private void AddRow(string sentence, int balance)
    {
        var bullet = BodyText("●");
        var text = BodyText("");
        var split = sentence.IndexOf(':') + 1;
        text.Inlines.Add(new Run { Text = sentence[..split], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run { Text = sentence[split..] });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(bullet);
        row.Children.Add(text);
        _content.Children.Add(row);
        _rows.Add((bullet, text, balance));
    }

    private void Update(bool dark, bool highContrast)
    {
        Tip.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        foreach (var (bullet, text, balance) in _rows)
            bullet.Foreground = text.Foreground = UsageTint.BrushFor(balance, dark, highContrast);
        _rounding.Foreground = UsageTint.BrushFor(0, dark, highContrast);
        _hoverBrush = highContrast ? UsageTint.SystemBrush("SystemColorWindowTextColorBrush")
            : ThemeBrush("ControlStrokeColorDefaultBrush", dark);
        _owner.BorderThickness = new Thickness(highContrast ? 2 : 1);
        _owner.BorderBrush = _hovered ? _hoverBrush : null;
        Tip.BorderThickness = new Thickness(highContrast ? 2 : 1);
        Tip.Background = highContrast ? UsageTint.SystemBrush("SystemColorWindowColorBrush")
            : ThemeBrush("SolidBackgroundFillColorBaseBrush", dark);
        Tip.BorderBrush = highContrast ? _hoverBrush : ThemeBrush("SurfaceStrokeColorFlyoutBrush", dark);
        if (Tip.IsOpen) Constrain();
    }

    // This Reactor version's ThemeRef.Resolve can return the application's
    // default brush even with an explicit dark argument. The shared brush's
    // ThemeResource color also follows that default. Resolve the named COLOR
    // in the selected WinUI dictionary for this independently themed popup.
    internal static Brush ThemeBrush(string key, bool dark)
    {
        static Brush? Find(ResourceDictionary resources, string key, string theme)
        {
            if (resources.ThemeDictionaries.TryGetValue(theme, out var selected) &&
                selected is ResourceDictionary dictionary)
            {
                if (key.EndsWith("Brush", StringComparison.Ordinal) &&
                    dictionary.TryGetValue(key[..^5], out var color) && color is Windows.UI.Color themedColor)
                    return new SolidColorBrush(themedColor);
                if (dictionary.TryGetValue(key, out var value) && value is Brush brush) return brush;
                foreach (var merged in dictionary.MergedDictionaries.Reverse())
                    if (Find(merged, key, theme) is { } match) return match;
            }
            foreach (var merged in resources.MergedDictionaries.Reverse())
                if (Find(merged, key, theme) is { } match) return match;
            return null;
        }
        var resources = Application.Current.Resources;
        return Find(resources, key, dark ? "Dark" : "Light") ??
            (dark ? Find(resources, key, "Default") : null) ??
            ThemeRef.Resolve(key, dark) ??
            throw new InvalidOperationException($"Missing WinUI brush: {key}");
    }

    private bool Contains(Point point) => point.X >= 0 && point.Y >= 0 &&
        point.X < _owner.ActualWidth && point.Y < _owner.ActualHeight;

    private void PointerEntered(object sender, PointerRoutedEventArgs e) => Enter(e.GetCurrentPoint(_owner).Position);
    private void PointerExited(object sender, PointerRoutedEventArgs e) => Exit(e.GetCurrentPoint(_owner).Position);
    private void PointerCanceled(object sender, PointerRoutedEventArgs e) => Cancel();
    private void Unloaded(object sender, RoutedEventArgs e) => Cancel();
    private void SizeChanged(object sender, SizeChangedEventArgs e) { if (Tip.IsOpen) Constrain(); }
    private void TipSizeChanged(object sender, SizeChangedEventArgs e) => AlignLeftEdge();
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) { if (Tip.IsOpen) Constrain(); }
    private XamlRoot? _root;

    // These are the production pointer paths, also exercised synchronously by
    // the isolated fixture (which does not move the user's physical cursor).
    internal void Enter(Point point)
    {
        if (_disposed || !_owner.IsLoaded || !Contains(point)) return;
        _hovered = true;
        _owner.BorderBrush = _hoverBrush;
        if (Tip.IsOpen) return;
        _root = _owner.XamlRoot;
        _root.Changed += RootChanged;
        Tip.XamlRoot = _root;
        Tip.PlacementTarget = _owner;
        Constrain();
        // WinUI requires a service owner even for an explicitly opened ToolTip.
        // Register only during hover, after our enter path starts, and always
        // unregister on exit. There is no idle dwell target/private wrapper;
        // even a service open can only address this already-open instance.
        ToolTipService.SetToolTip(_owner, Tip);
        Tip.IsOpen = true; // Synchronous: no service timer, dispatcher or render tick.
    }

    internal void Exit(Point point)
    {
        // Routed child-boundary exits are not exits from the rectangle.
        if (!Contains(point)) Cancel();
    }

    internal void Cancel()
    {
        _hovered = false;
        _owner.BorderBrush = null;
        Tip.IsOpen = false;
        ToolTipService.SetToolTip(_owner, null);
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
        Tip.PlacementTarget = null;
    }

    private void Constrain()
    {
        if (_owner.XamlRoot is not { } root) return;
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            root.ContentIslandEnvironment.AppWindowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var width = Math.Min(root.Size.Width, display.WorkArea.Width / root.RasterizationScale);
        var height = Math.Min(root.Size.Height, display.WorkArea.Height / root.RasterizationScale);
        var chromeX = Tip.Padding.Left + Tip.Padding.Right + Tip.BorderThickness.Left + Tip.BorderThickness.Right;
        var chromeY = Tip.Padding.Top + Tip.Padding.Bottom + Tip.BorderThickness.Top + Tip.BorderThickness.Bottom;
        _viewport.Width = Math.Max(1, Math.Min(600, width - chromeX - 32));
        _viewport.MaxHeight = Math.Max(1, height - chromeY - 32);
        Tip.MaxWidth = Math.Max(1, width - 32);
        Tip.MaxHeight = Math.Max(1, height - 32);
        AlignLeftEdge();
    }

    private void AlignLeftEdge()
    {
        if (Tip.ActualWidth > 0 && _owner.ActualWidth > 0)
            Tip.HorizontalOffset = Math.Max(0, (Tip.ActualWidth - _owner.ActualWidth) / 2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _owner.PointerEntered -= PointerEntered;
        _owner.PointerExited -= PointerExited;
        _owner.PointerCanceled -= PointerCanceled;
        _owner.Unloaded -= Unloaded;
        _owner.SizeChanged -= SizeChanged;
        Tip.SizeChanged -= TipSizeChanged;
        Tip.Content = null;
        _viewport.Content = null;
    }
}
