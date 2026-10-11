using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

class SessionBillboard : Billboard
{
    private readonly CopilotSession _session;

    public SessionBillboard(CopilotSession session) => _session = session;

    public override double WidthDip => 600;
    public override double HeightDip => 640;
    public override string Title => "Copilot Session";

    public override void OnOpened()
    {
        // Opening the billboard acknowledges a pending completion: the tray
        // cell's Done state falls back to ordinary Idle.
        if (!_session.IsSdk)
            CopilotSessionTracker.AcknowledgeDone(_session.SessionId);
    }

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();
        // Expansion belongs to this opening, not to the changing node snapshot.
        // Even collapsing an ancestor leaves descendants' choices intact.
        var (collapsed, updateCollapsed) = UseReducer(new HashSet<string>(StringComparer.Ordinal));
        var (selected, setSelected) = UseState<string?>(null);
        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            CopilotSessionTracker.UiChanged += OnChanged;
            if (_session.IsSdk)
                CopilotModule.Instance.SdkSessions.UiChanged += OnChanged;
            _session.Changed += OnChanged;
            Theme.Changed += OnChanged;
            return () =>
            {
                CopilotSessionTracker.UiChanged -= OnChanged;
                if (_session.IsSdk)
                    CopilotModule.Instance.SdkSessions.UiChanged -= OnChanged;
                _session.Changed -= OnChanged;
                Theme.Changed -= OnChanged;
            };
        }, Array.Empty<object>());

        bool hc = colorScheme == ColorScheme.HighContrast;
        var nodes = PresentationNodes(_session);
        // The details card tracks the tapped row, surviving re-renders;
        // nodes that leave the snapshot fall back to the owner root.
        var effectiveSelected = nodes.Any(n => n.SessionId == selected)
            ? selected
            : nodes.FirstOrDefault(n => n.SessionId == _session.SessionId)?.SessionId
                ?? nodes.First().SessionId;
        void Toggle(string id) => updateCollapsed(previous =>
        {
            var next = new HashSet<string>(previous, StringComparer.Ordinal);
            if (!next.Add(id)) next.Remove(id);
            return next;
        });

        var header = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto, GridSize.Auto],
            Image(CopilotModule.ClientIconPath(_session.Client))
                .Width(32).Height(32).AccessibilityHidden()
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0, rowSpan: 2),
            Subtitle(_session.Name)
                .MaxLines(1).TextTrimming(TextTrimming.CharacterEllipsis)
                .HeadingLevel(AutomationHeadingLevel.Level1)
                .ToolTip(_session.Name)
                .Margin(12, 0, 0, 0).Grid(row: 0, column: 1),
            Caption(CopilotModule.ClientLabel(_session.Client))
                .MaxLines(1).TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(_session.ClientMarker ?? CopilotModule.ClientLabel(_session.Client))
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(12, 4, 0, 0).Grid(row: 1, column: 1),
            StatusBadge(_session.Status.ToString(), _session.Status, hc, Theme.IsDark)
                .Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Top)
                .Grid(row: 0, column: 2, rowSpan: 2));

        var overview = MaterialCard(VStack(8,
            Section("Overview"),
            Field("Project", _session.ProjectName),
            Field("Working directory", _session.Cwd),
            _session.IsSdk
                ? Field("SDK observation",
                    $"{_session.SdkReadHealth}: {_session.SdkStatusReason ?? "No status evidence"}")
                : null,
            _session.IsSdk && _session.SdkHistoryPartial
                ? Caption("Recent persisted events are a bounded view; intermediate history may be missing.")
                    .TextWrapping(TextWrapping.Wrap)
                    .Foreground(ReactorTheme.SecondaryText)
                : null,
            CopyId(_session.SessionId, "Main session ID"),
            DetailFields(_session.Details),
            Section("Attributable group totals" + Partial(_session.GroupDetails)),
            UsageFields(_session.GroupDetails),
            Caption("Includes retained completed/ended nodes. Unavailable values are not zero.")
                .TextWrapping(TextWrapping.Wrap).Foreground(ReactorTheme.SecondaryText)), hc);

        var tree = MaterialCard(VStack(8,
            Section("Session tree"),
            RenderTree(nodes, collapsed, Toggle, effectiveSelected, setSelected, hc, Theme.IsDark)), hc);

        var selectedNode = nodes.SingleOrDefault(n => n.SessionId == effectiveSelected)
            ?? nodes.First();
        var details = MaterialCard(VStack(8,
            BodyStrong(selectedNode.Name).MaxLines(1)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(selectedNode.Name),
            Caption(NodeLabel(selectedNode)).Foreground(ReactorTheme.SecondaryText),
            selectedNode.Blocker is { } b
                ? Caption($"Blocked since {b.BlockedSince.ToLocalTime():HH:mm:ss} · {b.EventName} · Source {b.SourceId}")
                    .TextWrapping(TextWrapping.Wrap).Foreground(hc
                        ? ReactorTheme.Ref("SystemColorWindowTextColorBrush") : ReactorTheme.SecondaryText)
                : null,
            CopyId(selectedNode.SessionId, $"Session ID for {selectedNode.Name}"),
            DetailFields(selectedNode.Details),
            Caption("Own usage" + Partial(selectedNode.Details)).SemiBold(),
            UsageFields(selectedNode.Details),
            _session.IsSdk ? null : Section("Recent hooks"),
            _session.IsSdk
                ? null
                : selectedNode.RecentHooks.Count == 0
                    ? Caption("No hooks received.").Foreground(ReactorTheme.SecondaryText)
                    : VStack(4, selectedNode.RecentHooks.Take(5).Select(hook =>
                        Grid([GridSize.Star()], [GridSize.Auto],
                            Caption(hook.Line).MaxLines(1)
                                .TextTrimming(TextTrimming.CharacterEllipsis)
                                .AutomationName(hook.Line))
                            .WithKey(hook.Sequence.ToString(CultureInfo.InvariantCulture))).ToArray()),
            _session.IsSdk ? PersistedEvents(_session) : null), hc);

        var scroll = (ScrollViewer(VStack(12, overview, tree, details)) with
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        }).HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Margin(0, 12, 0, 0).Grid(row: 1);

        // No fixed height: BillboardSession measures naturally with infinite
        // height, then clamps to the monitor. The star viewport also shrinks
        // when the work area is smaller than this ceiling.
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                header.Grid(row: 0), scroll))
            .Padding(8).MaxHeight(640)
            .Background(hc ? ReactorTheme.Ref("SystemColorWindowColorBrush") : ReactorTheme.LayerFill)
            .WithBorder(hc ? ReactorTheme.Ref("SystemColorWindowTextColorBrush") : ReactorTheme.SurfaceStroke,
                hc ? 2 : 1)
            .CornerRadius(8)
            .RequestedTheme(hc ? ElementTheme.Default : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light);
    }

    internal static IReadOnlyList<CopilotSessionNode> PresentationNodes(CopilotSession session)
    {
        var nodes = session.Nodes.GroupBy(node => node.SessionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(node => string.IsNullOrWhiteSpace(node.Name) ? node with { Name = node.SessionId } : node)
            .ToList();
        if (nodes.All(node => node.SessionId != session.SessionId))
            nodes.Insert(0, new(session.SessionId, null, session.Name, null, "Unknown", null, session.Details, []));
        return nodes.OrderBy(node => node.SessionId == session.SessionId ? 0 : 1).ToArray();
    }

    private static Element RenderTree(IReadOnlyList<CopilotSessionNode> nodes,
        HashSet<string> collapsed, Action<string> toggle, string? selected, Action<string> select,
        bool hc, bool isDark)
    {
        var ids = nodes.Select(node => node.SessionId).ToHashSet(StringComparer.Ordinal);
        var children = nodes.Where(node => node.ParentId is not null && node.ParentId != node.SessionId)
            .ToLookup(node => node.ParentId!, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Element Branch(CopilotSessionNode node, int depth)
        {
            visited.Add(node.SessionId);
            var descendants = children[node.SessionId].Where(child => !visited.Contains(child.SessionId))
                .Select(child => Branch(child, depth + 1)).ToArray();
            bool expanded = !collapsed.Contains(node.SessionId);
            bool isSelected = node.SessionId == selected;
            var row = Grid([GridSize.Px(32), GridSize.Px(24), GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                (descendants.Length == 0 ? (Element)Caption("")
                    : Button(expanded ? "−" : "+", () => toggle(node.SessionId))
                        .Padding(4)
                        .AutomationName($"{(expanded ? "Collapse" : "Expand")} {node.Name}"))
                    .Grid(column: 0),
                SessionStatusIcon.ForNode(node, StatusBrush(node, hc, isDark), InactiveBrush(hc, isDark))
                    .Grid(column: 1),
                StatusWord(node, hc, isDark)
                    .VAlign(VerticalAlignment.Center).Margin(4, 0, 0, 0).Grid(column: 2),
                BodyStrong(node.Name).MaxLines(1).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(node.Name).VAlign(VerticalAlignment.Center).Margin(8, 0, 0, 0).Grid(column: 3));
            // A Button (not a Border) hosts each row: it keeps the visual
            // chrome via a transparent template while exposing the
            // UIAutomation Invoke pattern, so the row is keyboard- and
            // test-reachable like the +/- expanders.
            var rowButton = Button(row, () => select(node.SessionId))
                .Padding(6)
                .CornerRadius(4)
                .WithKey((isSelected ? "selected-" : "clear-") + node.SessionId)
                .AutomationName(NodeLabel(node));
            rowButton = isSelected
                ? rowButton
                    .Background(hc ? ReactorTheme.Ref("SystemColorHighlightColorBrush") : ReactorTheme.SubtleFill)
                    .WithBorder(hc ? ReactorTheme.Ref("SystemColorWindowTextColorBrush") : ReactorTheme.Accent,
                        hc ? 2 : 1)
                : rowButton
                    .WithBorder(hc ? ReactorTheme.Ref("SystemColorWindowTextColorBrush") : ReactorTheme.Accent,
                        2);
            return VStack(4, rowButton,
                expanded && descendants.Length > 0
                    ? VStack(4, descendants).Margin(depth < 12 ? 16 : 0, 0, 0, 0) : null)
                .WithKey(node.SessionId);
        }
        var forest = new List<Element>();
        foreach (var node in nodes.Where(node => node.ParentId is null || !ids.Contains(node.ParentId)))
            if (!visited.Contains(node.SessionId)) forest.Add(Branch(node, 0));
        // Defensive against partial/conflicting parent data. Never hide a node
        // or assign it a fabricated parent; a cycle is shown as a separate branch.
        foreach (var node in nodes)
            if (!visited.Contains(node.SessionId)) forest.Add(Branch(node, 0));
        return VStack(8, forest.ToArray());
    }

    private static Brush StatusBrush(CopilotSessionNode node, bool hc, bool isDark)
    {
        if (hc) return (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"];
        return new SolidColorBrush(
            CopilotStatusColors.StatusColorFor(node.Status ?? CopilotSessionStatus.Unknown, isDark));
    }

    private static Brush InactiveBrush(bool hc, bool isDark)
    {
        if (hc) return (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"];
        return new SolidColorBrush(
            CopilotStatusColors.StatusColorFor(CopilotSessionStatus.Unknown, isDark));
    }

    private static Element StatusWord(CopilotSessionNode node, bool hc, bool isDark)
    {
        string word = node.Lifecycle == "Active"
            ? node.Status?.ToString() ?? "Unknown"
            : node.Lifecycle;
        Brush brush = node.Lifecycle == "Active"
            ? StatusBrush(node, hc, isDark)
            : InactiveBrush(hc, isDark);
        return Caption(word).Foreground(brush);
    }

    internal static string NodeLabel(CopilotSessionNode node) =>
        $"{node.Lifecycle} · {node.Status?.ToString() ?? "Status unknown"}";

    private static Element CopyId(string id, string label) =>
        Component<SessionIdCopy, SessionIdCopyProps>(new(id, label)).WithKey(label + ":" + id);

    private static Element DetailFields(CopilotSessionDetails details) => VStack(4,
        Field("Model", details.Model),
        Field("Reasoning", details.ReasoningEffort));

    private static Element UsageFields(CopilotSessionDetails details) => VStack(4,
        Field("Input / output tokens", $"{Number(details.InputTokens)} / {Number(details.OutputTokens)}"),
        Field("Cache read / creation tokens", $"{Number(details.CachedTokens)} / {Number(details.CacheCreationTokens)}"),
        Field("nano-AIU", Number(details.NanoAiu)),
        Field("Premium requests", details.PremiumRequests?.ToString(CultureInfo.CurrentCulture)),
        details.ReportedNanoAiu.HasValue
            ? Field("Reported session nano-AIU (may include tasks; not added)", Number(details.ReportedNanoAiu)) : null,
        details.ReportedPremiumRequests.HasValue
            ? Field("Reported session premium requests (not added)",
                details.ReportedPremiumRequests.Value.ToString(CultureInfo.CurrentCulture)) : null);

    private static string Number(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unavailable";
    private static string Partial(CopilotSessionDetails details) => details.IsPartial ? " · Partial" : "";
    private static Element Section(string title) =>
        BodyStrong(title).HeadingLevel(AutomationHeadingLevel.Level2);

    private static Element PersistedEvents(CopilotSession session)
    {
        var rows = session.RecentPersistedEvents.TakeLast(5).Select(item =>
        {
            var tool = CopilotRecentHook.Token(item.ToolName);
            var line = $"{item.At?.ToLocalTime():HH:mm:ss} {CopilotRecentHook.Token(item.Type)} {tool}";
            return Grid([GridSize.Star()], [GridSize.Auto],
                    Caption(line).MaxLines(1)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .AutomationName(line))
                .WithKey(item.EventId);
        }).ToArray();
        return VStack(4,
            Section("Recent persisted events"),
            rows.Length == 0
                ? Caption("No persisted events available.").Foreground(ReactorTheme.SecondaryText)
                : VStack(4, rows));
    }

    private static Element Field(string label, string? value) =>
        Grid([GridSize.Px(160), GridSize.Star()], [GridSize.Auto],
            Caption(label).TextWrapping(TextWrapping.Wrap)
                .Foreground(ReactorTheme.SecondaryText).Grid(column: 0),
            Caption(string.IsNullOrWhiteSpace(value) ? "Unavailable" : value)
                .MaxLines(2).TextWrapping(TextWrapping.Wrap)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(value ?? "Unavailable").Grid(column: 1));

    private static BorderElement StatusBadge(string label, CopilotSessionStatus status, bool hc, bool isDark)
    {
        var foreground = new SolidColorBrush(CopilotStatusColors.StatusColorFor(status, isDark));
        var badge = Border(Caption(label).SemiBold().Padding(0))
            .Padding(8, 4).CornerRadius(4);
        badge = status switch
        {
            CopilotSessionStatus.Done => badge.Background(ReactorTheme.SystemSuccessBackground),
            CopilotSessionStatus.Blocked => badge.Background(ReactorTheme.SystemCautionBackground),
            // Working has no System*Background token; tint the card with the
            // same hue at low opacity instead.
            CopilotSessionStatus.Working => badge.Background(WorkingBackground(isDark)),
            _ => badge.Background(ReactorTheme.SystemNeutralBackground),
        };
        badge = hc
            ? badge.Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
            : badge;
        return hc
            ? badge.WithBorder(ReactorTheme.Ref("SystemColorWindowTextColorBrush"), 2)
            : badge.WithBorder(foreground, 1);
    }

    private static string WorkingBackground(bool isDark)
    {
        var c = CopilotStatusColors.StatusColorFor(CopilotSessionStatus.Working, isDark);
        return $"#{(isDark ? 0x33 : 0x1A):X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static BorderElement MaterialCard(Element content, bool hc) =>
        Border(content).Padding(12).CornerRadius(8)
            .Background(hc ? ReactorTheme.Ref("SystemColorWindowColorBrush") : ReactorTheme.CardBackground)
            .WithBorder(hc ? ReactorTheme.Ref("SystemColorWindowTextColorBrush") : ReactorTheme.CardStroke, hc ? 2 : 1);
}

internal sealed record SessionIdCopyProps(string Id, string Label);

internal sealed class SessionIdCopy : Component<SessionIdCopyProps>
{
    public override Element Render()
    {
        var (feedback, setFeedback) = UseState("");
        var button = UseRef<Microsoft.UI.Xaml.Controls.Button?>(null);
        return VStack(4,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                Caption(Props.Id).MaxLines(1).TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(Props.Id).VAlign(VerticalAlignment.Center).Grid(column: 0),
                Button("Copy ID", () =>
                {
                    SessionClipboard.TryCopy(button.Current, Props.Id, out string message);
                    setFeedback(message);
                }).OnMount(element => button.Current = (Microsoft.UI.Xaml.Controls.Button)element)
                    .OnUnmount(_ => button.Current = null)
                    .AutomationName($"Copy full {Props.Label}")
                    .Margin(8, 0, 0, 0).Grid(column: 1)),
            string.IsNullOrEmpty(feedback) ? null
                : Caption(feedback).TextWrapping(TextWrapping.Wrap)
                    .LiveRegion(AutomationLiveSetting.Polite));
    }
}
