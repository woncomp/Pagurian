# CPU and Memory Taskbar Widgets — Design

## Summary

Pagurian will add two fixed taskbar widget cells alongside the existing clock/Hello World cell and dynamic Copilot session cells:

1. Clock / Hello World
2. CPU
3. Memory
4. Copilot sessions, in first-seen order

The CPU and memory cells show an integer percentage above a thin horizontal gauge. Clicking either cell opens a Fluent-style details popup. The popups retain the application's existing non-activating, always-on-top behavior and participate in the existing one-popup-at-a-time interaction model.

Metrics refresh every 10 seconds while their popup is closed and every second while their popup is visible. In this design, a popup being visible is the definition of it being “active”; Windows focus is not used because Pagurian popups use `NoActivate`.

## User Experience

### Taskbar cells

Each new fixed cell uses the existing sampled taskbar background, theme-aware foreground, rounded hover overlay, pressed feedback, cursor polling, and click-edge detection.

- The CPU cell displays text such as `CPU 42%` with a thin gauge below it.
- The memory cell displays text such as `MEM 68%` with a thin gauge below it.
- Percentages are rounded to the nearest integer and clamped to 0–100.
- Before the first valid sample, a cell displays `--%` with an empty gauge.
- CPU uses a green accent and memory uses a blue accent, with readable track colors for both light and dark taskbars.
- The implementation uses Reactor's determinate `Progress(value)` element, which reconciles to the native WinUI `ProgressBar`.

Both cells have stable widget IDs (`cpu` and `memory`) and a fixed DIP width. Their visual order, total window width, and physical-pixel hit-test rectangles must use the same ordering so the controller never dispatches a click to the wrong cell.

### CPU popup

The CPU popup shows overall CPU usage, per-logical-processor usage, and the three processes with the highest CPU usage.

The processor section contains one row for every logical processor, labelled `CPU 0`, `CPU 1`, and so on. Every row uses the same fixed columns:

- Processor label
- Horizontal gauge
- Right-aligned percentage

Fixed column widths keep all gauges and percentages aligned. The processor list is placed in a height-limited `ScrollViewer` so systems with many logical processors do not produce an oversized window.

The Top Processes section shows up to three rows with:

- Process name
- PID
- CPU percentage

Long process names are truncated with an ellipsis. Processes with the same name remain separate rows because PID is part of the display. If fewer than three measurable processes exist, only the available processes are shown.

### Memory popup

The memory popup shows a system summary followed by the three processes with the largest working sets.

The summary contains:

- Used physical memory in GB
- Total physical memory in GB
- Used-memory percentage

Each process row contains:

- Process name
- PID
- Working set in MB
- Working set as a percentage of total physical memory

The ranking uses `Process.WorkingSet64`. It intentionally does not use private committed bytes or attempt to reproduce Task Manager's private working-set calculation.

### Popup behavior

- CPU and memory popups use `WindowStyle.None`, rounded corners, thin acrylic, `NoActivate`, no taskbar/switcher entry, and always-on-top positioning, consistent with the existing popups.
- Clicking a closed CPU or memory cell closes any Hello World or Copilot popup and opens the selected metrics popup.
- Clicking the same cell again closes its popup.
- Clicking the other metrics cell replaces the currently open popup.
- Clicking outside the taskbar cells and the open popup dismisses it.
- Only Copilot session cells participate in the session-name tooltip dwell behavior; CPU and memory IDs must not be passed to `CopilotSessionTracker.Find`.

## Metrics Architecture

### Tracker ownership and lifecycle

Add an internal static `SystemMetricsTracker` owned by the existing application lifecycle in `Program.cs`.

- `Start()` is called during normal application startup, before the taskbar widget begins consuming snapshots.
- `Stop()` is called during the existing Quit sequence.
- The Copilot bridge-mode path must still return before starting WinUI or the metrics tracker.
- `Stop()` stops the scheduler, advances a generation/cancellation token, and prevents in-flight background work from publishing after shutdown.

The tracker exposes immutable latest snapshots, an integer `Version`, and a UI-thread `UiChanged` event. Taskbar and popup components subscribe using the same Reactor `UseState`/`UseEffect` refresh-counter pattern already used by `CopilotSessionTracker`.

### Internal data types

No external public API is added. Introduce internal immutable data types equivalent to:

```csharp
enum SystemMetricKind
{
    Cpu,
    Memory,
}

record CpuProcessUsage(int ProcessId, string Name, double Percent);

record CpuSnapshot(
    double TotalPercent,
    IReadOnlyList<double> PerLogicalProcessorPercent,
    IReadOnlyList<CpuProcessUsage> TopProcesses,
    DateTimeOffset CapturedAt);

record MemoryProcessUsage(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    double SystemPercent);

record MemorySnapshot(
    ulong TotalBytes,
    ulong UsedBytes,
    double UsedPercent,
    IReadOnlyList<MemoryProcessUsage> TopProcesses,
    DateTimeOffset CapturedAt);
```

`SystemMetricsTracker` provides:

- Read-only `Cpu` and `Memory` latest-snapshot properties
- `Version` and `UiChanged`
- `Start()` and `Stop()`
- `SetPopupVisible(SystemMetricKind kind, bool visible)`

Snapshots may initially be absent. UI components use this state to render `--` rather than treating unavailable data as zero usage.

### Scheduling and threading

Use one repeating `DispatcherQueueTimer` to check independent CPU and memory due times. Actual sampling and process enumeration run through `Task.Run`; they must not block the UI thread or the taskbar's attached input queue.

- Closed popup: the corresponding metric is due every 10 seconds.
- Visible popup: the corresponding metric is due every 1 second.
- Opening a popup marks that metric immediately due.
- Closing a popup schedules its next sample relative to the latest completed sample at the 10-second cadence.
- If both metrics are due together, one background operation collects both and enumerates processes only once.
- A single in-flight guard prevents overlapping samples. A missed tick is retried on the next scheduler tick rather than queued.
- Results are marshalled through `ReactorApp.UIDispatcher.TryEnqueue` before snapshots, `Version`, or `UiChanged` are updated.

At startup, memory can be published immediately. CPU counters require a delta, so startup first captures a CPU baseline and schedules a one-second follow-up sample. After the first calculated value is published, CPU uses the normal adaptive cadence.

## Data Collection and Calculations

### Overall and per-processor CPU

Use a small metrics-specific interop layer with no additional NuGet dependencies. Query `NtQuerySystemInformation` using `SystemProcessorPerformanceInformation` to obtain idle, kernel, and user time for every logical processor.

For each processor, compare two consecutive snapshots:

```text
total delta = kernel delta + user delta
busy delta  = total delta - idle delta
usage       = busy delta / total delta * 100
```

Kernel time includes idle time on Windows, which is why idle is subtracted from the combined kernel and user delta. Invalid, zero, negative, NaN, or infinite deltas do not replace the last valid value. Valid results are clamped to 0–100. Overall CPU usage is calculated from the aggregate deltas across all logical processors, rather than by averaging already-rounded UI values.

The interop buffer must be sized from the returned byte count and retried when the native call reports that the buffer is too small. If the logical processor count changes, discard the incompatible baseline and establish a new one before publishing another calculated snapshot.

### Top CPU processes

For every readable non-idle process, capture `TotalProcessorTime` and identify the sample by PID plus process identity. Calculate process CPU usage with a monotonic elapsed time:

```text
process usage = process CPU-time delta
                / elapsed wall-clock time
                / logical processor count
                * 100
```

This yields a whole-system percentage on a 0–100 scale instead of allowing one fully occupied logical processor to report 100% on a multi-core system.

- A new process establishes a baseline and participates in ranking after a subsequent sample.
- PID reuse, identity mismatch, negative deltas, or a failed read reset that process's baseline.
- PID 0 / System Idle Process is excluded because its accumulated time represents idle CPU.
- Sort by descending CPU percentage, then PID for deterministic ties, and retain the first three entries.
- CPU Top 3 represents average usage over the same interval used for that snapshot: approximately one second with the popup open or ten seconds with it closed.

### Total and per-process memory

Use `GlobalMemoryStatusEx` to obtain total and available physical memory:

```text
used bytes   = total physical bytes - available physical bytes
used percent = used bytes / total physical bytes * 100
```

Enumerate readable processes and rank them by `WorkingSet64`, descending, then PID. For each selected process:

```text
system percent = process working-set bytes / total physical bytes * 100
```

### Process enumeration resilience

Process enumeration is best-effort:

- Catch failures per process so one protected or exited process cannot fail the sample.
- Dispose every `Process` instance.
- Preserve separate entries for duplicate process names.
- Publish fewer than three rows when fewer valid entries are available.
- Preserve the last valid snapshot if the system-level native call fails.
- If no valid snapshot has ever been collected, show `Data unavailable` in the popup and `--%` in the taskbar cell; do not display modal errors.

## Taskbar and Controller Integration

### Taskbar rendering

Update the taskbar window's total-width calculation and keyed `HStack` to include the two fixed cells before Copilot sessions. Subscribe to both tracker events:

- `CopilotSessionTracker.UiChanged` for session changes
- `SystemMetricsTracker.UiChanged` for metric changes

The hover-brush pruning logic must treat clock, CPU, and memory as permanent built-in IDs. Only removed Copilot session IDs are pruned.

### Hit testing and popup state

Rebuild physical-pixel hit-test rectangles in exactly this order on every anchor pass:

1. Clock width
2. CPU width
3. Memory width
4. One fixed-width rectangle per Copilot session

Extend `TaskbarController` with one metrics-popup window reference, its metric kind, and its physical popup rectangle. Centralize the following transitions so every path correctly updates `SystemMetricsTracker.SetPopupVisible`:

- Show CPU popup
- Show memory popup
- Hide/replace metrics popup
- Open Hello World popup
- Open Copilot session popup
- Outside-click dismissal
- Controller shutdown

The popup is positioned relative to the clicked cell in DIPs, while its dismissal rectangle remains in physical pixels, following the existing Reactor/Win32 boundary convention.

## Verification

### Build

The implementation must keep the project at zero warnings and zero errors:

```powershell
dotnet build Pagurian.sln -p:Platform=x64
```

Also compile-check the ARM64 platform because all new native structures and pointer arithmetic must remain architecture-neutral.

### Manual scenarios

1. Compare overall CPU, logical-processor trends, total memory, and Top 3 process trends with Task Manager.
2. Confirm CPU and memory percentages never become negative, NaN, infinite, or greater than 100.
3. Confirm inactive cells refresh approximately every 10 seconds.
4. Open the CPU popup and confirm CPU updates approximately every second while memory remains on its inactive cadence.
5. Open the memory popup and confirm memory updates approximately every second while CPU remains on its inactive cadence.
6. Verify the first startup CPU value appears after the baseline follow-up rather than displaying a false zero.
7. Verify aligned processor rows and scrolling on machines with few and many logical processors.
8. Start and stop busy processes rapidly; confirm exited, protected, duplicate-name, and fewer-than-three-process cases do not crash or leave stale rows indefinitely.
9. Verify the visual and hit-test order is clock, CPU, memory, then Copilot sessions.
10. Verify clicking the same cell toggles its popup, opening another cell replaces it, and outside clicks dismiss it.
11. Verify CPU and memory cells do not trigger Copilot tooltips.
12. Verify Explorer restart recreates and reinjects the expanded taskbar window.
13. Check light and dark taskbars, mixed DPI, and bottom, top, and vertical taskbar positions for readable gauges and correctly placed popups.

## Assumptions

- “Core” means a logical processor, including separate rows for simultaneous-multithreading siblings.
- Popup visibility, not Windows focus, selects the one-second refresh cadence.
- Memory process ranking uses working set.
- UI labels remain in English to match the existing application.
- The feature adds no third-party performance-counter or management packages.
- The repository's existing build-and-manual-observation verification model remains unchanged; no test framework is introduced as part of this feature.
