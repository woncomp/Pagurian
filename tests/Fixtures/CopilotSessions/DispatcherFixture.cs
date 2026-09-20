using System.Collections.Concurrent;

// Only the dispatcher boundary is substituted. The actual resolver, reducer
// and tracker are linked into this dependency-free console executable.
namespace Microsoft.UI.Dispatching
{
    sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool Running { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Fire() => Tick?.Invoke(this, new());
        public Action CaptureTick()
        {
            var callbacks = Tick;
            return () => callbacks?.Invoke(this, new());
        }
    }

    sealed class DispatcherQueue
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public List<DispatcherQueueTimer> Timers { get; } = [];
        public int PendingCount => _queue.Count;
        public bool TryEnqueue(Action callback) { _queue.Enqueue(callback); return true; }
        public DispatcherQueueTimer CreateTimer()
        {
            var timer = new DispatcherQueueTimer();
            Timers.Add(timer);
            return timer;
        }
        public void Drain()
        {
            while (_queue.TryDequeue(out var callback))
                callback();
        }
    }
}

namespace Microsoft.UI.Reactor
{
    static class ReactorApp
    {
        public static Microsoft.UI.Dispatching.DispatcherQueue UIDispatcher { get; } = new();
    }
}
