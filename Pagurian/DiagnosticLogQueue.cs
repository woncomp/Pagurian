namespace Pagurian;

// UI observations never write to disk on the UI thread. The sole
// worker exits as soon as the bounded queue becomes empty.
internal sealed class DiagnosticLogQueue
{
    private readonly object gate = new();
    private readonly Queue<Entry> pending = new();
    private readonly Action<string> writeLine;
    private readonly int capacity;
    private readonly string tag;
    private Task? worker;
    private long nextSequence;
    private DroppedEntries? dropped;

    internal DiagnosticLogQueue(Action<string> writeLine, int capacity = 4096, string tag = "shell-navigation")
    {
        ArgumentNullException.ThrowIfNull(writeLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.writeLine = writeLine;
        this.tag = SingleLine(tag);
        this.capacity = capacity;
    }

    internal void Enqueue(string sessionId, string message, string level = "INFO")
    {
        lock (gate)
        {
            // Capture both at enqueue, not when a delayed disk write occurs.
            long sequence = ++nextSequence;
            DateTimeOffset timestamp = DateTimeOffset.Now;
            PublishDroppedEntries();
            if (pending.Count == capacity)
            {
                dropped = dropped is { } previous
                    ? previous with { LastSequence = sequence, LastTimestamp = timestamp, Count = previous.Count + 1 }
                    : new DroppedEntries(sequence, sequence, timestamp, timestamp, 1);
            }
            else
            {
                pending.Enqueue(new Entry(timestamp, sequence, sessionId, level, message));
            }

            worker ??= Task.Run(Drain);
        }
    }

    // Flush includes overflow even when no event was enqueued after it.
    internal Task FlushAsync()
    {
        lock (gate)
            return worker ?? Task.CompletedTask;
    }

    private void Drain()
    {
        while (true)
        {
            Entry entry;
            lock (gate)
            {
                PublishDroppedEntries();
                if (!pending.TryDequeue(out entry))
                {
                    worker = null;
                    return;
                }

                // Insert overflow before newer accepted events to preserve
                // sequence order even while the queue is saturated.
                PublishDroppedEntries();
            }

            try
            {
                writeLine($"{entry.Timestamp:O} [{SingleLine(entry.Level)}] [{tag}] " +
                    $"pid={Environment.ProcessId} seq={entry.Sequence} session={SingleLine(entry.SessionId)} " +
                    SingleLine(entry.Message));
            }
            catch
            {
                // Logging must not strand the worker or prevent shutdown.
            }
        }
    }

    // Called under gate. The report uses the last dropped ID and describes
    // the entire omitted contiguous range; it also consumes one queue slot.
    private void PublishDroppedEntries()
    {
        if (dropped is not { } lost || pending.Count == capacity)
            return;

        pending.Enqueue(new Entry(lost.LastTimestamp, lost.LastSequence, "-", "WARN",
            $"event=queue-overflow dropped={lost.Count} firstSeq={lost.FirstSequence} " +
            $"lastSeq={lost.LastSequence} firstTime={lost.FirstTimestamp:O} lastTime={lost.LastTimestamp:O}"));
        dropped = null;
    }

    private static string SingleLine(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private readonly record struct Entry(DateTimeOffset Timestamp, long Sequence, string SessionId, string Level, string Message);
    private readonly record struct DroppedEntries(long FirstSequence, long LastSequence,
        DateTimeOffset FirstTimestamp, DateTimeOffset LastTimestamp, long Count);
}
