using Microsoft.UI.Xaml;

namespace Pagurian;

internal enum ShellConfigurationVisitPhase
{
    Entering,
    Active,
    Exiting,
}

internal sealed class ShellConfigurationVisit(
    long visitId,
    TrayConfig.Entry entry,
    bool animateOnMount)
{
    internal long VisitId { get; } = visitId;
    internal TrayConfig.Entry Entry { get; set; } = entry;
    internal ShellConfigurationVisitPhase Phase { get; set; } =
        ShellConfigurationVisitPhase.Entering;
    internal bool AnimateOnMount { get; set; } = animateOnMount;
    internal FrameworkElement? Element { get; set; }
    internal ShellConfigurationPageMotion? Motion { get; set; }
}

internal sealed class ShellConfigurationTransitionState(
    Action changed,
    Action<string>? trace = null) : IDisposable
{
    private readonly List<ShellConfigurationVisit> _visits = [];
    private long _nextVisitId;
    private double _extent = 1;
    private bool _reducedMotion;
    private bool _visible = true;
    private bool _disposed;

    internal IReadOnlyList<ShellConfigurationVisit> Visits => _visits;

    internal string? SelectedInstanceId =>
        _visits.LastOrDefault(visit => visit.Phase != ShellConfigurationVisitPhase.Exiting)
            ?.Entry.Id;

    internal long? SelectedVisitId =>
        _visits.LastOrDefault(visit => visit.Phase != ShellConfigurationVisitPhase.Exiting)
            ?.VisitId;

    internal bool Open(TrayConfig.Entry entry)
    {
        if (_disposed || SelectedInstanceId == entry.Id)
            return false;

        foreach (var visit in _visits.Where(
                     visit => visit.Phase != ShellConfigurationVisitPhase.Exiting).ToArray())
        {
            StartExit(visit);
        }

        var createdVisit = new ShellConfigurationVisit(++_nextVisitId, entry, animateOnMount: true);
        _visits.Add(createdVisit);
        Trace(createdVisit, "created");
        changed();
        return true;
    }

    internal bool Close()
    {
        if (_disposed)
            return false;
        var current = _visits.LastOrDefault(
            visit => visit.Phase != ShellConfigurationVisitPhase.Exiting);
        if (current == null)
            return false;

        StartExit(current);
        changed();
        return true;
    }

    internal bool IsCurrent(long visitId) => SelectedVisitId == visitId;

    internal void RefreshCurrent(TrayConfig.Entry entry)
    {
        var current = _visits.LastOrDefault(
            visit => visit.Phase != ShellConfigurationVisitPhase.Exiting);
        if (current?.Entry.Id == entry.Id)
            current.Entry = entry;
    }

    internal void Mount(long visitId, FrameworkElement element)
    {
        var visit = Find(visitId);
        if (_disposed || visit == null)
            return;

        visit.Element = element;
        visit.Motion?.Dispose();
        var motion = new ShellConfigurationPageMotion();
        visit.Motion = motion;
        Trace(visit, "mounted");

        if (!_visible)
        {
            motion.Attach(element, _extent, startOffscreen: false);
            visit.AnimateOnMount = false;
            return;
        }

        if (visit.Phase == ShellConfigurationVisitPhase.Exiting)
        {
            element.IsHitTestVisible = false;
            motion.Attach(element, _extent, startOffscreen: true);
            Remove(visitId);
            return;
        }

        var animate = visit.AnimateOnMount && !_reducedMotion;
        motion.Attach(element, _extent, startOffscreen: animate);
        visit.AnimateOnMount = false;
        if (!animate)
        {
            visit.Phase = ShellConfigurationVisitPhase.Active;
            motion.CompleteAt(offscreen: false);
            return;
        }

        motion.Enter(() =>
        {
            var current = Find(visitId);
            if (current?.Phase != ShellConfigurationVisitPhase.Entering)
                return;
            current.Phase = ShellConfigurationVisitPhase.Active;
            Trace(current, "entered");
            changed();
        });
    }

    internal void Unmount(ShellConfigurationVisit visit, FrameworkElement element)
    {
        if (!ReferenceEquals(visit.Element, element))
            return;
        visit.Motion?.Dispose();
        visit.Motion = null;
        visit.Element = null;
    }

    internal void SetExtent(double extent)
    {
        _extent = Math.Max(1, extent);
        foreach (var visit in _visits)
            visit.Motion?.UpdateExtent(_extent);
    }

    internal void SetReducedMotion(bool reducedMotion)
    {
        if (_disposed || _reducedMotion == reducedMotion)
            return;
        _reducedMotion = reducedMotion;
        if (!reducedMotion)
            return;

        foreach (var visit in _visits.ToArray())
        {
            if (visit.Phase == ShellConfigurationVisitPhase.Exiting)
                Remove(visit.VisitId);
            else
            {
                visit.Motion?.CompleteAt(offscreen: false);
                visit.Phase = ShellConfigurationVisitPhase.Active;
            }
        }
        changed();
    }

    internal void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible)
            return;
        _visible = visible;
        if (visible)
            return;

        foreach (var visit in _visits.ToArray())
        {
            if (visit.Phase == ShellConfigurationVisitPhase.Exiting)
                Remove(visit.VisitId);
            else
            {
                visit.Motion?.CompleteAt(offscreen: false);
                visit.Phase = ShellConfigurationVisitPhase.Active;
                visit.AnimateOnMount = false;
            }
        }
        changed();
    }

    private void StartExit(ShellConfigurationVisit visit)
    {
        if (visit.Phase == ShellConfigurationVisitPhase.Exiting)
            return;

        visit.Phase = ShellConfigurationVisitPhase.Exiting;
        Trace(visit, "exiting");
        if (visit.Element != null)
            visit.Element.IsHitTestVisible = false;

        if (_reducedMotion || !_visible || visit.Motion == null)
        {
            Remove(visit.VisitId);
            return;
        }

        var visitId = visit.VisitId;
        visit.Motion.Exit(() =>
        {
            if (Find(visitId)?.Phase == ShellConfigurationVisitPhase.Exiting)
                Remove(visitId);
        });
    }

    private void Remove(long visitId)
    {
        var index = _visits.FindIndex(visit => visit.VisitId == visitId);
        if (index < 0)
            return;
        var visit = _visits[index];
        _visits.RemoveAt(index);
        Trace(visit, "removed");
        if (visit.Element == null)
        {
            visit.Motion?.Dispose();
            visit.Motion = null;
        }
        else
        {
            visit.Motion?.Stop();
        }
        changed();
    }

    private ShellConfigurationVisit? Find(long visitId) =>
        _visits.FirstOrDefault(visit => visit.VisitId == visitId);

    private void Trace(ShellConfigurationVisit visit, string operation) =>
        trace?.Invoke(
            $"transition visit={visit.VisitId} shell={ShellNavigationDiagnostics.ConfigurationRoute(visit.Entry.Id)} " +
            $"operation={operation} phase={visit.Phase}");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var visit in _visits)
            visit.Motion?.Dispose();
        _visits.Clear();
    }
}
