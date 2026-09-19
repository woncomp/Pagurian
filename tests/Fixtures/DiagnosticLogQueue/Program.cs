using System.Globalization;
using System.Text.RegularExpressions;
using Pagurian;

await OrderedConcurrentWrites();
await OverflowFlushesWithoutAnotherEvent();
await IdleWorkerRestartsAndIgnoresSinkFailures();
Console.WriteLine("Navigation diagnostic queue passed: ordered async writes, bounded overflow, flush, restart and failure isolation.");

static async Task OrderedConcurrentWrites()
{
    var output = new List<string>();
    int activeWriters = 0;
    int maximumWriters = 0;
    var queue = new DiagnosticLogQueue(line =>
    {
        int active = Interlocked.Increment(ref activeWriters);
        maximumWriters = Math.Max(maximumWriters, active);
        output.Add(line);
        Interlocked.Decrement(ref activeWriters);
    });
    Parallel.For(0, 8, producer =>
    {
        for (int index = 0; index < 200; index++)
            queue.Enqueue($"session-{producer}", $"event=test index={index}");
    });
    await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Require(output.Count == 1600, "All non-overflowing concurrent events must be written.");
    Require(maximumWriters == 1, "There must only be one sink writer.");
    for (int index = 0; index < output.Count; index++)
    {
        Require(Sequence(output[index]) == index + 1, "Concurrent events must follow sequence order.");
        Require(output[index].Contains($"[shell-navigation] pid={Environment.ProcessId} ", StringComparison.Ordinal),
            "Every line must identify its category and process.");
    }
}

static async Task OverflowFlushesWithoutAnotherEvent()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var output = new List<string>();
    int callerThread = Environment.CurrentManagedThreadId;
    int sinkThread = callerThread;
    var queue = new DiagnosticLogQueue(line =>
    {
        if (Sequence(line) == 1)
        {
            sinkThread = Environment.CurrentManagedThreadId;
            entered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(5)), "Blocked sink must be released.");
        }
        output.Add(line);
    }, capacity: 2);

    queue.Enqueue("test", "event=blocking-first");
    Require(entered.Wait(TimeSpan.FromSeconds(5)), "The worker must start.");
    Require(sinkThread != callerThread, "The caller must never execute the disk sink.");
    DateTimeOffset before = DateTimeOffset.Now;
    queue.Enqueue("test", "event=timestamp-check");
    DateTimeOffset after = DateTimeOffset.Now;
    queue.Enqueue("test", "event=last-accepted");
    queue.Enqueue("test", "event=first-dropped");
    queue.Enqueue("test", "event=second-dropped");
    Task flush = queue.FlushAsync();
    Require(!flush.IsCompleted, "Flush must wait for pending writes.");
    release.Set();
    await flush.WaitAsync(TimeSpan.FromSeconds(5));

    Require(output.Count == 4, "Overflow must produce one report without a later enqueue.");
    Require(output.Select(Sequence).SequenceEqual(new long[] { 1, 2, 3, 5 }), "Overflow report must retain sequence order.");
    Require(output[3].Contains("event=queue-overflow dropped=2 firstSeq=4 lastSeq=5", StringComparison.Ordinal),
        "Overflow must explicitly report omitted counts and ranges.");
    DateTimeOffset timestamp = DateTimeOffset.Parse(output[1].Split(' ')[0], CultureInfo.InvariantCulture);
    Require(timestamp >= before && timestamp <= after, "Timestamp must describe enqueue rather than disk-write time.");
}

static async Task IdleWorkerRestartsAndIgnoresSinkFailures()
{
    var output = new List<string>();
    var queue = new DiagnosticLogQueue(line =>
    {
        if (Sequence(line) == 1)
            throw new IOException("Test-only unavailable sink.");
        output.Add(line);
    });
    queue.Enqueue("test", "event=write-failure");
    queue.Enqueue("test", "event=after-failure");
    await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Require(output.Count == 1 && Sequence(output[0]) == 2, "A failed write must not strand later events.");
    Require(queue.FlushAsync().IsCompleted, "Idle flush must complete immediately.");
    queue.Enqueue("test", "event=restart\r\ncontinued");
    await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Require(output.Count == 2 && Sequence(output[1]) == 3, "An idle worker must restart without resetting IDs.");
    Require(!output[1].Contains('\n') && !output[1].Contains('\r') && output[1].Contains("\\r\\n", StringComparison.Ordinal),
        "Diagnostic messages must stay on one log line.");
}

static long Sequence(string line) => long.Parse(Regex.Match(line, @"\bseq=(\d+)\b").Groups[1].Value, CultureInfo.InvariantCulture);

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
