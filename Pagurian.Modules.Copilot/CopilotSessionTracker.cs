using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

// UI adapter only. Filesystem discovery runs on the resolver worker; all state,
// owner cell notifications and the existing visual debounce run on the dispatcher.
static class CopilotSessionTracker
{
    private static CopilotSessionState? _state;
    private static CopilotSessionIdentityResolver? _resolver;
    private static DispatcherQueueTimer? _timer;
    private static int _generation;
    private static int _users;

    public static int Version { get; private set; }
    public static event Action? UiChanged;
    public static event Action<CopilotSession>? SessionStarted;
    public static event Action<CopilotSession>? SessionEnded;
    public static IReadOnlyCollection<CopilotSession> Sessions => _state?.Sessions ?? [];
    public static CopilotSession? Find(string sessionId) => _state?.Find(sessionId);

    public static void Start(Logger log, string? stateDirectory = null, ICopilotAppLifecycleReader? lifecycle = null)
    {
        if (_users++ > 0)
            return;
        var dispatcher = ReactorApp.UIDispatcher!;
        var generation = ++_generation;
        var state = new CopilotSessionState(diagnostic: transition =>
            log.Info($"session-transition source={transition.SourceId} owner={transition.OwnerId} " +
                $"event={transition.EventName} at={transition.EventAt:o} " +
                $"{transition.Before}->{transition.After} reason={transition.Reason}"));
        _state = state;
        state.SessionStarted += OnStarted;
        state.SessionEnded += OnEnded;
        var directory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
        _resolver = new(directory, log.Warn, lifecycle, enableAppLifecycle: stateDirectory is null);
        _resolver.StartSnapshots((identities, snapshot) => dispatcher.TryEnqueue(() =>
        {
            if (_generation != generation || !ReferenceEquals(_state, state))
                return;
            state.Resolve(identities, snapshot);
            NotifyChanged();
        }));
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public static void Stop()
    {
        if (_users == 0 || --_users > 0)
            return;
        ++_generation; // Invalidate already queued resolver/timer callbacks first.
        _resolver?.Dispose();
        _resolver = null;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            _timer = null;
        }
        var state = _state;
        _state = null;
        state?.Clear();
        if (state is not null)
        {
            state.SessionStarted -= OnStarted;
            state.SessionEnded -= OnEnded;
        }
        NotifyChanged();
    }

    public static void HandleHookEvent(string eventName, string? payloadJson, DateTimeOffset receivedAt)
    {
        if (_state is not { } state || _resolver is not { } resolver ||
            CopilotHookEvent.Parse(eventName, payloadJson, receivedAt) is not { } hook)
            return;
        // Reduce before observing: even a very fast background reply cannot
        // attach an empty source in place of the early permission event.
        state.Handle(hook);
        resolver.Observe(hook.SourceId, hook.TranscriptPath, hook.ParentId,
            hook.Name is "subagentStart" or "subagentStop" ? hook.AgentId : null);
        NotifyChanged();
    }

    private static void OnStarted(CopilotSession session) => SessionStarted?.Invoke(session);
    private static void OnEnded(CopilotSession session) => SessionEnded?.Invoke(session);
    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (ReferenceEquals(sender, _timer) && _state?.CommitStableStatuses() == true)
            NotifyChanged();
    }

    private static void NotifyChanged()
    {
        Version++;
        UiChanged?.Invoke();
    }
}
