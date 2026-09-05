using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;

namespace Pagurian.Modules.Metrics;

internal enum SystemMetricKind
{
    Cpu,
    Memory,
}

internal record CpuProcessUsage(int ProcessId, string Name, double Percent);

internal record CpuSnapshot(
    double TotalPercent,
    IReadOnlyList<double> PerLogicalProcessorPercent,
    IReadOnlyList<CpuProcessUsage> TopProcesses,
    DateTimeOffset CapturedAt);

internal record MemoryProcessUsage(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    double SystemPercent);

internal record MemorySnapshot(
    ulong TotalBytes,
    ulong UsedBytes,
    double UsedPercent,
    IReadOnlyList<MemoryProcessUsage> TopProcesses,
    DateTimeOffset CapturedAt);

// Collects CPU and memory snapshots on a background thread and publishes
// immutable, UI-thread-only snapshots to the taskbar cells and detail popups.
// See docs/roadmap/System-Metrics-Widgets-Design.md for the cadence and
// calculation rules.
static class SystemMetricsTracker
{
    public static CpuSnapshot? Cpu { get; private set; }
    public static MemorySnapshot? Memory { get; private set; }

    public static int Version { get; private set; }
    public static event Action? UiChanged;

    private static readonly TimeSpan TimerInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CpuBaselineFollowUpDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InactiveInterval = TimeSpan.FromSeconds(10);

    private static CancellationTokenSource? _cts;
    private static long _generation;
    private static DispatcherQueueTimer? _timer;
    private static int _inFlight;

    private static bool _cpuVisible;
    private static bool _memoryVisible;

    private static DateTimeOffset _cpuDueAt = DateTimeOffset.MaxValue;
    private static DateTimeOffset _memoryDueAt = DateTimeOffset.MaxValue;
    private static DateTimeOffset _lastCpuCompletedAt = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastMemoryCompletedAt = DateTimeOffset.MinValue;

    private static ProcessorInfo? _cpuBaseline;
    private static Dictionary<ProcessKey, ProcessSample> _cpuProcessBaselines = new();

    public static void Start()
    {
        _cts = new CancellationTokenSource();
        Interlocked.Increment(ref _generation);

        Cpu = null;
        Memory = null;
        Version = 0;
        _cpuBaseline = null;
        _cpuProcessBaselines.Clear();
        _cpuVisible = false;
        _memoryVisible = false;
        _lastCpuCompletedAt = DateTimeOffset.MinValue;
        _lastMemoryCompletedAt = DateTimeOffset.MinValue;

        // Memory is cheap to publish immediately; CPU needs a baseline so the
        // first calculated value is published after the 1-second follow-up.
        var now = DateTimeOffset.UtcNow;
        _memoryDueAt = now;
        _cpuDueAt = now + CpuBaselineFollowUpDelay;

        _timer = ReactorApp.UIDispatcher!.CreateTimer();
        _timer.Interval = TimerInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _cts?.Cancel();
        Interlocked.Increment(ref _generation);
    }

    public static void SetPopupVisible(SystemMetricKind kind, bool visible)
    {
        if (kind == SystemMetricKind.Cpu)
        {
            if (_cpuVisible == visible)
                return;
            _cpuVisible = visible;
            _cpuDueAt = visible
                ? DateTimeOffset.UtcNow
                : (_lastCpuCompletedAt == DateTimeOffset.MinValue
                    ? DateTimeOffset.UtcNow
                    : _lastCpuCompletedAt) + InactiveInterval;
        }
        else
        {
            if (_memoryVisible == visible)
                return;
            _memoryVisible = visible;
            _memoryDueAt = visible
                ? DateTimeOffset.UtcNow
                : (_lastMemoryCompletedAt == DateTimeOffset.MinValue
                    ? DateTimeOffset.UtcNow
                    : _lastMemoryCompletedAt) + InactiveInterval;
        }
    }

    public static void NotifyChanged()
    {
        Version++;
        UiChanged?.Invoke();
    }

    private static void OnTick()
    {
        var now = DateTimeOffset.UtcNow;
        var cpuDue = now >= _cpuDueAt;
        var memoryDue = now >= _memoryDueAt;
        if (!cpuDue && !memoryDue)
            return;

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        var capturedGen = Interlocked.Read(ref _generation);
        var ct = _cts!.Token;
        Task.Run(() => CollectAndPublish(cpuDue, memoryDue, capturedGen, ct), ct);
    }

    private static void CollectAndPublish(bool cpuDue, bool memoryDue, long generation, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            ProcessorInfo? currentCpu = null;
            if (cpuDue)
                currentCpu = QueryProcessorInfo(ct);

            MemoryInfo? currentMemory = null;
            if (memoryDue)
                currentMemory = QueryMemoryInfo();

            IReadOnlyList<ProcessSample>? processSamples = null;
            if (cpuDue || memoryDue)
                processSamples = EnumerateProcesses(cpuDue, memoryDue);

            CpuSnapshot? cpuSnapshot = null;
            var cpuBaselineOnly = false;
            var cpuBaselineAt = DateTimeOffset.UtcNow;
            if (cpuDue && currentCpu != null)
            {
                if (_cpuBaseline == null || currentCpu.Count != _cpuBaseline.Count)
                {
                    _cpuBaseline = currentCpu;
                    _cpuProcessBaselines = new Dictionary<ProcessKey, ProcessSample>(
                        processSamples?.ToDictionary(p => new ProcessKey(p.Pid, p.Name)) ?? new Dictionary<ProcessKey, ProcessSample>());
                    cpuBaselineOnly = true;
                    cpuBaselineAt = currentCpu.CapturedAt;
                }
                else
                {
                    var snapshot = ComputeCpuSnapshot(_cpuBaseline, currentCpu, _cpuProcessBaselines, processSamples ?? new List<ProcessSample>(), ct);
                    if (snapshot != null)
                    {
                        _cpuBaseline = currentCpu;
                        cpuSnapshot = snapshot;
                    }
                }
            }

            MemorySnapshot? memorySnapshot = null;
            if (memoryDue && currentMemory != null)
                memorySnapshot = ComputeMemorySnapshot(currentMemory, processSamples ?? new List<ProcessSample>());

            if (ct.IsCancellationRequested)
                return;

            ReactorApp.UIDispatcher?.TryEnqueue(() =>
                Publish(new Result(cpuSnapshot, memorySnapshot, cpuBaselineOnly, cpuBaselineAt, cpuDue, memoryDue), generation));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Best-effort: retry on the next scheduler tick.
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private static void Publish(Result result, long generation)
    {
        if (Interlocked.Read(ref _generation) != generation)
            return;

        var now = DateTimeOffset.UtcNow;
        var changed = false;

        if (result.CpuSnapshot != null)
        {
            Cpu = result.CpuSnapshot;
            Version++;
            _lastCpuCompletedAt = now;
            _cpuDueAt = now + (_cpuVisible ? ActiveInterval : InactiveInterval);
            changed = true;
        }
        else if (result.CpuBaselineOnly)
        {
            // Do not publish a value; schedule the 1-second follow-up baseline.
            _cpuDueAt = result.CpuBaselineAt + CpuBaselineFollowUpDelay;
        }
        else if (result.CpuRequested)
        {
            // The sample failed to produce a value; retry quickly.
            _cpuDueAt = now + ActiveInterval;
        }

        if (result.MemorySnapshot != null)
        {
            Memory = result.MemorySnapshot;
            Version++;
            _lastMemoryCompletedAt = now;
            _memoryDueAt = now + (_memoryVisible ? ActiveInterval : InactiveInterval);
            changed = true;
        }
        else if (result.MemoryRequested)
        {
            _memoryDueAt = now + ActiveInterval;
        }

        if (changed)
            UiChanged?.Invoke();
    }

    private static ProcessorInfo? QueryProcessorInfo(CancellationToken ct)
    {
        int count = Environment.ProcessorCount;
        int entrySize = Marshal.SizeOf<SystemProcessorPerformanceInformation>();
        int bufferSize = count * entrySize;
        if (bufferSize <= 0)
            bufferSize = entrySize * 64;

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessorPerformanceInformation,
                    buffer,
                    bufferSize,
                    out int returnLength);

                if (status == NativeMethods.STATUS_SUCCESS)
                {
                    int returnedCount = returnLength / entrySize;
                    if (returnedCount <= 0)
                        return null;

                    var idle = new long[returnedCount];
                    var kernel = new long[returnedCount];
                    var user = new long[returnedCount];
                    for (int i = 0; i < returnedCount; i++)
                    {
                        var ptr = buffer + i * entrySize;
                        var info = Marshal.PtrToStructure<SystemProcessorPerformanceInformation>(ptr);
                        idle[i] = info.IdleTime;
                        kernel[i] = info.KernelTime;
                        user[i] = info.UserTime;
                    }
                    return new ProcessorInfo(idle, kernel, user, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
                }

                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH && returnLength > bufferSize)
                {
                    bufferSize = returnLength;
                    buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)bufferSize);
                    continue;
                }

                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MemoryInfo? QueryMemoryInfo()
    {
        var stat = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref stat))
            return null;
        return new MemoryInfo(stat.ullTotalPhys, stat.ullAvailPhys, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<ProcessSample> EnumerateProcesses(bool needCpuTime, bool needWorkingSet)
    {
        var list = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                string name = process.ProcessName;
                if (pid == 0 || name == "System Idle Process")
                    continue;

                TimeSpan? cpuTime = null;
                if (needCpuTime)
                {
                    try { cpuTime = process.TotalProcessorTime; }
                    catch { continue; }
                }

                long? workingSet = null;
                if (needWorkingSet)
                {
                    try { workingSet = process.WorkingSet64; }
                    catch { continue; }
                }

                list.Add(new ProcessSample(
                    pid,
                    name,
                    cpuTime ?? TimeSpan.Zero,
                    workingSet ?? 0,
                    Stopwatch.GetTimestamp()));
            }
            catch
            {
                // Ignore a single protected or exited process.
            }
            finally
            {
                process.Dispose();
            }
        }
        return list;
    }

    private static CpuSnapshot? ComputeCpuSnapshot(
        ProcessorInfo baseline,
        ProcessorInfo current,
        Dictionary<ProcessKey, ProcessSample> processBaselines,
        IReadOnlyList<ProcessSample> currentSamples,
        CancellationToken ct)
    {
        int n = baseline.Count;
        long idleDelta = 0;
        long kernelDelta = 0;
        long userDelta = 0;
        var perCore = new double[n];
        bool valid = true;

        for (int i = 0; i < n && valid; i++)
        {
            ct.ThrowIfCancellationRequested();
            long idle = current.Idle[i] - baseline.Idle[i];
            long kernel = current.Kernel[i] - baseline.Kernel[i];
            long user = current.User[i] - baseline.User[i];
            long total = kernel + user;
            if (total <= 0 || idle < 0)
            {
                valid = false;
                break;
            }
            long busy = total - idle;
            if (busy < 0)
                busy = 0;
            perCore[i] = (double)busy / total * 100.0;
            idleDelta += idle;
            kernelDelta += kernel;
            userDelta += user;
        }

        if (!valid)
            return null;

        long totalDelta = kernelDelta + userDelta;
        if (totalDelta <= 0)
            return null;
        long totalBusy = totalDelta - idleDelta;
        if (totalBusy < 0)
            totalBusy = 0;
        double totalPercent = (double)totalBusy / totalDelta * 100.0;
        totalPercent = Math.Clamp(totalPercent, 0, 100);

        for (int i = 0; i < n; i++)
            perCore[i] = Math.Clamp(perCore[i], 0, 100);

        var candidates = new List<CpuProcessUsage>();
        var newBaselines = new Dictionary<ProcessKey, ProcessSample>();
        double elapsedSec = (current.Timestamp - baseline.Timestamp) / (double)Stopwatch.Frequency;

        if (elapsedSec > 0 && n > 0)
        {
            foreach (var curr in currentSamples)
            {
                var key = new ProcessKey(curr.Pid, curr.Name);
                newBaselines[key] = curr;
                if (processBaselines.TryGetValue(key, out var prev))
                {
                    var delta = curr.TotalProcessorTime - prev.TotalProcessorTime;
                    if (delta >= TimeSpan.Zero)
                    {
                        double pct = delta.TotalSeconds / elapsedSec / n * 100.0;
                        pct = Math.Clamp(pct, 0, 100);
                        candidates.Add(new CpuProcessUsage(curr.Pid, curr.Name, pct));
                    }
                }
            }
        }
        else
        {
            foreach (var curr in currentSamples)
                newBaselines[new ProcessKey(curr.Pid, curr.Name)] = curr;
        }

        processBaselines.Clear();
        foreach (var kv in newBaselines)
            processBaselines[kv.Key] = kv.Value;

        var top = candidates
            .OrderByDescending(c => c.Percent)
            .ThenBy(c => c.ProcessId)
            .Take(MetricsSettings.MaxTopProcesses)
            .ToList();

        return new CpuSnapshot(
            totalPercent,
            perCore,
            top,
            DateTimeOffset.UtcNow);
    }

    private static MemorySnapshot? ComputeMemorySnapshot(MemoryInfo info, IReadOnlyList<ProcessSample> currentSamples)
    {
        if (info.TotalBytes == 0)
            return null;
        ulong used = info.AvailBytes > info.TotalBytes ? 0 : info.TotalBytes - info.AvailBytes;
        double usedPercent = (double)used / info.TotalBytes * 100.0;
        usedPercent = Math.Clamp(usedPercent, 0, 100);

        var top = currentSamples
            .Where(p => p.WorkingSetBytes > 0)
            .OrderByDescending(p => p.WorkingSetBytes)
            .ThenBy(p => p.Pid)
            .Take(MetricsSettings.MaxTopProcesses)
            .Select(p => new MemoryProcessUsage(
                p.Pid,
                p.Name,
                p.WorkingSetBytes,
                (double)p.WorkingSetBytes / info.TotalBytes * 100.0))
            .ToList();

        return new MemorySnapshot(info.TotalBytes, used, usedPercent, top, DateTimeOffset.UtcNow);
    }

    private record ProcessKey(int Pid, string Name);

    private record ProcessSample(
        int Pid,
        string Name,
        TimeSpan TotalProcessorTime,
        long WorkingSetBytes,
        long Timestamp);

    private record ProcessorInfo(
        IReadOnlyList<long> Idle,
        IReadOnlyList<long> Kernel,
        IReadOnlyList<long> User,
        long Timestamp,
        DateTimeOffset CapturedAt)
    {
        public int Count => Idle.Count;
    }

    private record MemoryInfo(
        ulong TotalBytes,
        ulong AvailBytes,
        long Timestamp,
        DateTimeOffset CapturedAt);

    private record Result(
        CpuSnapshot? CpuSnapshot,
        MemorySnapshot? MemorySnapshot,
        bool CpuBaselineOnly,
        DateTimeOffset CpuBaselineAt,
        bool CpuRequested,
        bool MemoryRequested);

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct SystemProcessorPerformanceInformation
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public ulong InterruptCount;
    }

    private static class NativeMethods
    {
        public const int SystemProcessorPerformanceInformation = 8;
        public const int STATUS_SUCCESS = 0;
        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
