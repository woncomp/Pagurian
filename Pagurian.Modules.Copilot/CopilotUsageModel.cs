using System.Text.Json;

namespace Pagurian.Modules.Copilot;

// No WinUI/SDK dependency: also exercised with the fixture's fake source,
// clock and dispatch queue. The shared source never owns a shell's schedule.
internal interface ICopilotUsageSource
{
    CopilotUsageState State { get; }
    event Action? Changed;
    void Refresh();
}

internal sealed class CopilotUsageModel : IDisposable
{
    private readonly ICopilotUsageSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly Action<string> _warn;
    private readonly Action<Action> _dispatch;
    private readonly Timer? _timer;
    private volatile bool _disposed;
    private int _queued;
    private string? _clockKey;
    private DateTimeOffset? _lastClock;
    private DateTimeOffset? _lastRefreshRequest;
    private CopilotUsageSettings _settings;
    // Publish account/quota and pace together. Reading the source directly
    // from a view could pair its new quota with our still-queued old pace.
    public CopilotUsageState State { get; private set; }
    public CopilotUsagePace Pace { get; private set; }
    public CopilotUsageSettings Schedule => _settings;
    public event Action? Changed;

    public CopilotUsageModel(ICopilotUsageSource source, JsonElement? settings,
        Action<string> warn, Action<Action> dispatch,
        Func<DateTimeOffset>? now = null, Func<TimeZoneInfo>? zone = null,
        bool observeClock = true, TimeSpan? clockInterval = null)
    {
        _source = source;
        _warn = warn;
        _dispatch = dispatch;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _zone = zone ?? ReadLocalZone;
        _settings = CopilotUsageSettings.Read(settings, warn);
        State = source.State;
        Pace = CopilotUsageCalendar.Calculate(State, _settings, _now(), _zone());
        _source.Changed += SourceChanged;
        ObserveClock();
        if (observeClock)
        {
            var interval = clockInterval ?? TimeSpan.FromSeconds(15);
            _timer = new Timer(_ => QueueClock(), null, interval, interval);
        }
    }

    private static TimeZoneInfo ReadLocalZone()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }

    public void ApplySettings(JsonElement? settings)
    {
        if (_disposed) return;
        _settings = CopilotUsageSettings.Read(settings, _warn);
        Recompute();
    }

    public void Refresh() { if (!_disposed) _source.Refresh(); }

    private void SourceChanged()
    {
        // Source publishes on UI, but fence queued callbacks for fake/background
        // sources too. Unmount/disposal invalidates the callback itself.
        _dispatch(() => { if (!_disposed) Recompute(); });
    }

    private void QueueClock()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _dispatch(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (!_disposed) ObserveClock();
        });
    }

    // Public to the module/fixtures for deterministic clock/resume simulation.
    public void ObserveClock()
    {
        if (_disposed) return;
        var now = _now();
        var zone = _zone();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var key = $"{local:yyyy-MM-dd}|{zone.ToSerializedString()}|{local.Offset}|{State.Usage.PremiumInteractions?.ResetDate}|{now >= State.Usage.PremiumInteractions?.ResetDate}";
        var resumed = _lastClock is { } last &&
            (now - last > TimeSpan.FromSeconds(45) || now < last);
        _lastClock = now;
        if (key != _clockKey || resumed)
        {
            _clockKey = key;
            Recompute(now, zone);
        }
        else
            RequestFreshCycle(now);
    }

    private void Recompute() => Recompute(_now(), _zone());

    private void Recompute(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (_disposed) return;
        State = _source.State;
        Pace = CopilotUsageCalendar.Calculate(State, _settings, now, zone);
        Changed?.Invoke();
        RequestFreshCycle(now);
    }

    private void RequestFreshCycle(DateTimeOffset now)
    {
        if (Pace.Cycle is not { } cycle) return;
        // A pre-boundary request can complete after the boundary; never call
        // its old consumption fresh just because completion happened later.
        var stale = now >= cycle.Reset ||
            (State.Usage.PremiumInteractions?.ReadStartedAt ?? State.RefreshedAt) is { } read
                && read < cycle.Start;
        // A newly fetched quota can contain an invalid reset (even "now").
        // Its estimated effective cycle remains usable; raw reset alone is
        // not evidence that the consumption snapshot predates that cycle.
        if (!stale)
        {
            _lastRefreshRequest = null;
            return;
        }
        if (State.IsRefreshing || State.IsLoggingIn) return;
        // Coalesced source operations may still be completing. Retry at most
        // once per minute, not on every tick or every source notification.
        if (_lastRefreshRequest is { } request && now >= request &&
            now - request < TimeSpan.FromMinutes(1)) return;
        _lastRefreshRequest = now;
        _source.Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _source.Changed -= SourceChanged;
        Changed = null;
    }
}
