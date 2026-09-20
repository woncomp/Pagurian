# Tray lifecycle and coordinated layout

Each presentation surface (one live display's taskbar at one edge) has one
persistent TrayWindowSession; see "Multiple displays" below. Startup and
recovery prepare an invisible window; ordinary shell edits retain the HWND and
surviving keyed cell controls. Module APIs and configuration formats are
unchanged.

## Multiple displays

Three layers, one-directional:

- **DisplayTopology** owns the physical world: EnumDisplayMonitors geometry
  joined with DisplayConfig EDID identity (`monitorFriendlyDeviceName`,
  `monitorDevicePath`; durable key `MODEL-UID`, e.g. `DEL40A6-UID4354`), plus
  the taskbar window on each display (`Shell_TrayWnd` primary,
  `Shell_SecondaryTrayWnd` secondaries, mapped via MonitorFromWindow).
  Structural changes (identity set, geometry, primary flag, taskbar presence)
  raise Changed; taskbar HWNDs are re-resolved lazily because Explorer
  recreates them without a topology change. Last-seen name/geometry per
  identity is persisted to `monitors.json` (host state, atomic write; cap 64).
- **TrayManager** owns the logical world: one ShellTray per configured
  `TrayId(MonitorKey, Edge)`; shells keep running and `post` keeps routing
  while their display is absent. The binding engine maps each tray to a live
  surface: exact display (the `primary` alias follows the current primary) →
  closest aspect ratio to the recorded geometry (|ln(a/b)| ≤ 0.15) → a display
  no configured tray owns → the primary display. A surface renders the cells of
  every tray bound to it, in config order. Right-edge trays bind to their
  display's right surface and keep their edge through the fallback chain;
  only on vertical taskbars (no system area to anchor beside) do they fold
  to the left surface.
- **TraySurface** owns the presentation world: one TrayWindowSession, one
  sampled ThemeService, and the interaction state for its display. The
  TraySurfaceController reconciles the surface set on a throttled cadence: the
  primary display's left surface always exists (tray-icon menu owner), all
  other surfaces — non-primary left, every right — exist only while cells are
  bound to them.

## Right-edge trays

A right-edge surface anchors its window to the left of the taskbar's system
area: the notification tray (`TrayNotifyWnd`) on the primary taskbar, the
clock on secondaries. Native evidence must be a visible `TrayNotifyWnd` or
`TrayClockWClass` belonging to the taskbar's own process, with valid system-area
geometry. An arbitrary narrow/right-anchored child is **not** evidence: our own
injected HWND used to satisfy that heuristic, causing a width-plus-gap position
oscillation on every environment tick. A show-desktop button alone does not
locate the left edge of the clock/notification cluster.

Each right surface owns a `TaskbarSystemAreaObserver`. When no native system
control exists, its background MTA worker queries only that taskbar's UIA
subtree. The supported secondary-clock structure is `SystemTrayIcon`,
`SystemTray.OmniButton`, Button control type, and a `TimeInnerTextBlock`
descendant, with matching process ownership and valid physical bounds.
These are observed Windows internals, not a permanent OS contract. Full-width
XAML bridges and localized time/date strings are never used as geometry.
Queries have node/depth and provider timeout limits, one in-flight call per
observer, 500 ms successful refreshes and bounded failure backoff. No UIA calls
run in the UI/input loop. COM objects stay on the worker.

The observer retains the last reliable boundary on a transient query failure
only within the same HWND/owner/geometry/DPI context. A new context starts
Unknown. Without a reliable boundary, the right window stays hidden (including
its hit targets), while its Shells keep running. Recovery reuses the HWND and
keyed content through the existing presentation gate. Closed observers reject
late results; they do not block the UI waiting for a provider.

`TaskbarTrayPlacement.Surface` retains the complete content rectangle and a
separate `SystemAreaLeftPx` constraint. The tray ends 8 DIP before that boundary,
scaled from the original taskbar thickness. The 50 ms environment check only
consumes cached evidence. Stable observations never request another layout.
Boundary source/failure/recovery diagnostics are per observer/context, deduped
and rate limited through the existing asynchronous tray log, not synchronous
full child-tree dumps.

Cells render in **reversed config order** (`TaskbarTrayWindow` reverses the
array for right surfaces): the first configured cell sits nearest the system
area, and the layout snapshot — which hit-testing, hover and billboard anchors
read — follows render order, so everything stays keyed consistently. The
logical order in `TrayManager.CellsForSurface` remains pure config order.

DPI falls out of the existing math (surface scale is the measured taskbar
thickness ÷ 48 DIP). Tooltips flip against the owning display's monitor rect —
never the primary origin, which is wrong for negative-coordinate displays.
Hover overlay brushes are reference-counted per cell key because a rebinding
cell briefly mounts on two surfaces. Billboard anchoring was already
monitor-aware (MonitorFromRect work areas).

## Preparation and presentation

States: Preparing, Visible, Closing, Closed. An empty tray returns to Preparing
and hides its existing window; adding cells prepares another frame on that HWND.
The root Border, background brush, natural-measure panel and Reactor content
target exist before the component tree mounts. Auto-activation and automatic
size-to-content are disabled.

The session stages on the target monitor, changes the native style and parents
to the taskbar, measures at a fixed 44 DIP height and unconstrained width, then
commits geometry. It shows through AppWindow while cloaked so XAML can load.
A Rendered event followed by an off-thread DwmFlush gates uncloaking. Geometry
and background versions prevent a superseded preparation frame from revealing.
Static content needs only one dirty frame. All callbacks check session lifetime.
Empty windows never reveal, and failed native preparation remains hidden.

**Child windows require WS_EX_LAYERED for the DWM cloak path.** Applying cloak
only before SetParent is insufficient: uncloaking an ordinary child returned
E_HANDLE in both the isolated and real taskbar fixture. The layered style stays
on the window for its lifetime, with full alpha. Raw ShowWindow alone did not
initialize the WinUI content pipeline; the session uses ReactorWindow.Show and
reasserts child geometry after first-show placement. ScreenToClient converts
screen coordinates to the actual parent client origin.

[DWM window attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
explains cloaking layered child windows; [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)
describes the presentation fence. Neither screen capture nor the fence runs on the UI thread.

## One-way geometry and color

The natural-measure panel isolates content width from the HWND's current width.
Reactor completion, changed desired size, DPI and taskbar geometry invalidate
layout. Reactor completion commits synchronously after its batched reconciliation,
before returning to XAML painting. External invalidations coalesce on the UI
dispatcher; the panel's arrange callback drains pending layout before painting.
Deferring every commit to a low-priority dispatcher callback reproduced one
clipped frame when a cell grew, even though deletion was already correct.
A commit measures the cell
Borders (DesiredSize already includes margins), arranges the content with the
DPI compensation transform, positions/resizes once, then publishes an immutable
snapshot containing version, cell order/bounds, total width and scales.

The root stays left/top aligned. The old ActualWidth / width-change broadcast /
Reactor render / 50 ms resize feedback loop is gone. Input polling consumes only
the committed snapshot; removed cells immediately cease being interaction targets.
Reentrant layout notifications cause a follow-up check, not recursive mutation.
The environment timer only requests layout when geometry changes or a failed
native geometry operation needs retrying. Stable color sampling does not measure.

Background capture runs every 250 ms. Horizontal samples cover the entire taskbar
content edge, indexed in screen coordinates; vertical samples cover strips above
and below the tray. A width or system-area-boundary change derives gradient
stops from the same cache; only a change in the actual sampling geometry
invalidates that cache.
Results from an old environment generation or disposed session are discarded.
Startup waits for a sample or a 500 ms deadline, then uses a system light/dark
fallback. High contrast uses the system background and default XAML theme.
A two-channel-level tolerance suppresses capture noise; only a light/dark change
notifies module theme subscribers.

Configuration application suppresses intermediate cell publications, including
notifications from Startup/Shutdown. Dynamic changes in one UI turn publish once.
The published list is immutable; existing shell instances and keys are retained.

## Native loss and diagnostics

Explorer may destroy the child HWND without delivering WinUI Window.Closed.
Calling Close afterwards reproduced ACCESS_VIOLATION; calling Dispose reproduced
another AV in SystemBackdrop cleanup. ReactorDestroyedWindow is a small,
explicit compatibility adapter for **Reactor 0.1.0-preview.12**. It invokes the
framework's two existing Closed handlers in their normal order only after
IsWindow proves the native window is gone. This marks the surface destroyed,
unmounts module controls, unregisters the window and releases Reactor resources.
It never invokes native Close. The integration fixture destroys its own parent
and then opens another tray to verify the process remains usable.

This adapter uses two private framework members. Re-run the native-loss fixture
when upgrading Reactor, and remove the adapter once a supported native-loss API
exists. The Reactor dependency itself has not been changed.

Session, HWND, mount/unmount, layout version, measurement and commit counts go
through the bounded asynchronous diagnostic queue under the tray log tag.
Unchanged polling produces no layout diagnostics. Failed sessions/injection retry
at a bounded cadence; close invalidates queued capture and first-frame callbacks.

## Verification

- `tests/Verify-TrayLifecycle.ps1`: isolated native parent; startup, survivor
  identity, first/middle/last removal, dynamic width, publication batching, idle
  stability, empty/repopulate, positioning, light/dark samples, 500 ms fallback,
  late results, floating fallback, parent destruction and subsequent recovery.
- `tests/Verify-TrayTopology.ps1`: config v1→v2 migration and roundtrip, global
  id uniqueness across trays, exact/alias binding, the full fallback chain
  (aspect → unoccupied → primary), reconnection, right-edge binding
  (right surface, edge-preserving fallback, vertical-taskbar fold),
  cross-tray `post` routing, and cross-tray move restart semantics. Fabricated
  displays and an isolated temp config; never touches user state.
- `tests/Verify-TrayLifecycle.ps1 -Taskbar`: the same production session under
  the actual Explorer taskbar; no Explorer restart or user config/module loading.
- `tests/Verify-TrayLifecycle.ps1 -Right`: isolated right placement, the exact
  self-anchor regression, candidate validation, worker generation fences,
  initial Unknown, retained transient evidence, hidden geometry invalidation,
  recovery without remounting, live clock-width changes, and 200 continuous
  stable environment polls through the production selector and observer.
- `tests/Verify-TrayLifecycle.ps1 -SecondaryTaskbar <HWND>`: explicitly targets
  a live secondary taskbar using the real native/UIA observer. Asserts clock
  clearance and 200 stable polls, never loads user configuration. A missing or
  primary HWND fails rather than silently substituting another taskbar.
- `python tests/Capture-TrayLifecycle.py --taskbar`: desktop frame capture and
  contact sheet in the fixture's ignored bin output. Requires Python and Pillow.
  Omit --taskbar for the isolated parent. --exe and --output permit baseline runs.
  `--right` and `--secondary-taskbar <HWND>` select the new right fixtures and
  record their scan line throughout the run.
- `tests/Verify-BillboardLifecycle.ps1`, `tests/Verify-ModuleIsolation.ps1`,
  `tests/Verify-DiagnosticLogQueue.ps1`, and an explicit x64 solution build.

The pre-change baseline was reconstructed from HEAD into an ignored fixture,
using the same probe cells, taskbar capture region and deletion schedule.
The baseline recording caught an initial mostly-white tray frame and deletion
frames where the first icon moved from x=11 to x=44 and back two frames later.
The final updated recording (505 frames) kept x=11 through both removals, with
no white tray frame. Its scan line switched directly from the narrow cell to
the expanded cell; the earlier one-frame clipping during growth was also removed.
The native fixture passed 101 assertions on Explorer and 102 with a synthetic
parent (the extra assertion checks actual destruction of that parent).
These coordinates are relative to the captured region at 100% scale.

Physical mixed-DPI monitor transitions, actual high-contrast switching, vertical
or relocated taskbars, and a real Explorer process restart still require manual
coverage. The isolated parent-loss test exercises native child destruction but
is not a substitute for restarting the user's Explorer. The synthetic parent
itself may show a startup white frame; use the real-taskbar recording to assess
tray startup. The fixture's later light-background case intentionally paints white.

The secondary-clock regression recording used the real UIA clock boundary
at x=4797 and a 66 px probe tray ending at x=4789 (8 px gap at 100%).
All 200 stable environment polls produced no additional measurements or
geometry commits. The final recording contained 612 frames, 588 with the probe
visible; its teal footprint did not move/change width, and the tray border
never flashed white. The isolated right fixture also covers unavailable/recovered
boundaries and a clock-width change. Real minute-boundary/clock-hover transitions,
mixed-DPI hardware changes and ARM64 execution still need manual coverage;
x64 solution and ARM64 host compilation are covered. The solution itself only
declares x64, so the ARM64 check targets `Pagurian\Pagurian.csproj` directly.

The existing module-isolation PowerShell runner needs WinAppSDK bootstrapping:
load the host output's runtimes/win-x64/native/Microsoft.WindowsAppRuntime.Bootstrap.dll
with NativeLibrary.Load, and Microsoft.WindowsAppRuntime.Bootstrap.Net.dll into
the default AssemblyLoadContext before invoking it in the same process.
