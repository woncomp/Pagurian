# Copilot Instructions — Pagurian

## Build and run

Always build/run with an explicit platform (required for WinUI 3, like the Tea project this repo mirrors):

```powershell
dotnet build Pagurian.sln -p:Platform=x64
dotnet run --project Pagurian -p:Platform=x64
```

There are no tests and no linter. Verification is done by launching the app and observing
the tray icon, taskbar icon window, popup, and message box.

### Quick compile check on macOS (temporary development)

A full build requires Windows (the WinAppSDK self-contained step runs `mt.exe`,
a Windows-only binary). For fast feedback while coding on macOS, run:

```bash
dotnet build Pagurian.sln -p:EnableWindowsTargeting=true
```

The C# compiler runs to completion on macOS, so any syntax/type errors surface
normally; the build then fails at the `mt.exe` manifest step, which is expected
and unrelated to code correctness. Treat "compiles clean up to the mt.exe
error" as a passing compile check; use a Windows machine/VM/CI for full builds
and running the app.

## What this app is

A WinUI 3 Fluent-style utility that (1) runs a system-tray icon with a Quit-only
native Win32 context menu, (2) injects a borderless window into the Windows
taskbar (child of `Shell_TrayWnd`) that is a **horizontal container of widget
cells**: the first cell replicates the native Windows 11 datetime widget — two
centered 12-DIP lines (time over date, current-culture short formats, updated
every second) — followed by fixed **CPU and memory** cells showing percentage +
thin gauge, and then **one cell per tracked GitHub Copilot CLI session**
(GitHub icon + colored status text), all blending into the taskbar, with the
native rounded translucent hover/pressed highlight per cell and light/dark text
adaptation, (3) opens a small popup (icon + "Hello World" + button → Win32
MessageBox) when clicking the clock cell, (4) opens detail popups for the CPU
and memory cells, and (5) registers a **Copilot CLI hook** while running: hook
events flow in over a named pipe, each new session gets a taskbar cell showing
its status (`Idle`/`Working`/`Blocked`, color-coded; `sessionEnd` removes the
cell), hovering a session cell shows a tooltip with the session name, and
clicking it opens a popup with the name, status, and the last received event
dumped as JSON.

## High-level architecture

C#-only, no XAML. UI is built with **Microsoft.UI.Reactor 0.1.0-preview.12** (React-style
components: `Component.Render()`, `useState`-style hooks, `static Factories` helpers).

- `Program.cs` — single entry. First statement is the **bridge-mode intercept**:
  `Pagurian.exe --hook <event>` runs `CopilotHookBridge.Run(event)` and returns
  before ever touching WinUI or the metrics tracker (see the Copilot hook
  bullets below). Otherwise `ReactorApp.Run(startup)` where startup sets
  `ShutdownPolicy.Explicit`, installs the Copilot hook file, starts the
  session tracker and the system-metrics tracker, opens the tray icon
  (`ReactorApp.OpenTrayIcon`, all `ReactorApp` methods are **static**), opens
  the icon window, then hands both to the controller. Nothing else may own app
  lifecycle; quitting happens only via the tray menu
  (`TaskbarController.Stop()` → `SystemMetricsTracker.Stop()` →
  `CopilotSessionTracker.Stop()` → `CopilotHookInstaller.Uninstall()` →
  `tray.Close()` → `ReactorApp.Exit(0)` → `Environment.Exit(0)` — the last call
  is required: `ReactorApp.Exit(0)` alone closes the windows but leaves the
  process running).
- `TaskbarController.cs` — the heart of the app. A `DispatcherQueueTimer` on
  `ReactorApp.UIDispatcher` polls every 50 ms and does these things:
  1. **Inject/anchor**: parents the icon window into `Shell_TrayWnd` (`SetParent`,
     one attempt per tick) and keeps it positioned at the taskbar's left-bottom
     corner via `SetWindowPos` in client coordinates; re-injects after Explorer
     restarts. Anchor geometry is derived from the **live taskbar rect**, never
     from the window's `DipScale` (which can lag behind display-topology
     changes): taskbars are 48 DIP thick by design, so `taskbar.Height / 48`
     doubles as the taskbar's own DPI scale, and the widget height is the taskbar
     thickness minus `2 × WindowInsetYDip` (2 DIP clear top and bottom, so it
     sits slightly inside the taskbar — and can never stick out of it), with
     the position clamped inside the taskbar rect on top of that (a stale
     DPI/rect leaving the widget covering the taskbar's top edge was a real
     bug). The window **width** is `TaskbarIconWindow.TotalWidthDip()` — clock
     cell + CPU cell + memory cell + one cell per tracked session — read every
     tick, so session add/remove resizes the window through the normal
     `SetWindowPos` path. After anchoring, the controller rebuilds the
     **per-cell hit-test rects** (`_cellRectsPx`: clock, CPU, memory, then
     sessions in first-seen order) used by hover/click/tooltip.
  2. **Blend**: the taskbar is translucent, so its apparent color *varies along
     its length* (wallpaper showing through — measured deltas over 25 levels
     across the widget's own width; a single sampled color visibly mismatched
     the far edge). The widget therefore paints a **gradient**
     (`TaskbarIconWindow.TaskbarColorBrush`, a 5-stop `LinearGradientBrush`
     along the taskbar's long axis) whose stops are sampled from the
     2-DIP native taskbar sliver the widget's inset leaves uncovered (below
     the widget for horizontal taskbars; above+below with linear
     interpolation for vertical ones). Each stop is a trimmed mean over a
     column slice of the captured strip (sort by luminance, average the
     middle 50%) so acrylic noise and glyph pixels are dropped; the theme
     color is the average of the stops. (The injected child window can't be
     transparent, so the sampled opaque colors *are* the blend — like the
     native clock's transparent background, minus the transparency.)
     **Sampling never runs on the UI thread** — see "Screen reads block"
     below; the tick only kicks off a throttled (250 ms) background capture
     (`TaskbarInterop.CaptureScreenRegionPixels`, one BitBlt per strip) and
     applies the result via `UIDispatcher.TryEnqueue`.
  3. **Theme/hover visuals**: derives light/dark from the average sampled
     taskbar luminance (Rec.601, threshold 140 — always matches the taskbar itself,
     including translucency/accent tints; no registry/UISettings) and mutates
     `TextBrush` (TextFillColorPrimary: white / #1A1A1A) plus the **per-cell**
     hover overlay brush of the hovered cell only (`TaskbarIconWindow
     .HoverBrushFor(widgetId)`: rounded translucent overlay inset 3×4 DIP with
     4 DIP radius, alpha 0 idle, SubtleFillColorSecondary #0F white / #09 black
     on hover, SubtleFillColorTertiary #0A / #06 while pressed; pressed =
     `GetAsyncKeyState(VK_LBUTTON)` polled while hovering). A theme flip also
     calls `CopilotSessionTracker.NotifyChanged()` and refreshes the CPU/memory
     gauge accent/track brushes so the per-render status colors stay current.
  4. **Popup/tooltip**: polls `GetCursorPos` + `GetAsyncKeyState(VK_LBUTTON)`
     against the cached per-cell pixel rects; a left-button up→down edge
     (50 ms polling reliably catches physical clicks) inside the clock cell
     toggles the Hello popup, inside the CPU or memory cells toggles their
     detail popups, inside a session cell toggles that session's popup, and a
     click outside all cells and popups dismisses whichever is open (native
     flyout style; clicks inside a popup don't dismiss; **one popup at a time**
     — opening one closes the other). Resting the cursor on a session cell for
     ~400 ms (8 ticks) opens a `TooltipWindow` with the session name above the
     cell; leaving the cell or clicking hides it.

- `TaskbarIconWindow.cs` / `HoverPopupWindow.cs` / `SessionPopupWindow.cs` /
  `CpuMetricsPopupWindow.cs` / `MemoryMetricsPopupWindow.cs` / `TooltipWindow.cs`
  — Reactor `Component`s plus `CreateSpec()` factories returning their `WindowSpec`.
 Window chrome comes entirely from `WindowSpec`
  (`Style.None`, `Backdrop`, `CornerStyle.Rounded`, `NoActivate`, …), not from
  content. `TaskbarIconWindow` is the **cell container**: an `HStack` of keyed
  cells — the clock replica (`UseState` + a 1 s `DispatcherQueueTimer` in
  `UseEffect` update the two `TextBlock` lines, guarded so it only re-renders
  when the text actually changes) followed by one cell per tracked Copilot
  session (GitHub icon + colored status text). It re-renders when
  `CopilotSessionTracker.UiChanged` fires (a version-`UseState` bump — the Tea
  refresh-counter pattern). Live visuals that change at poll frequency are
  static brushes mutated in place by the controller (no re-render):
  `TaskbarColorBrush` (the blend gradient), `TextBrush`, the per-cell
  `HoverBrushFor(widgetId)` overlays, and the per-session `StatusBrushFor`
  text colors (also the REACTOR_THEME_004-compliant answer to inline brushes).
  `SessionPopupWindow` (per session: name, colored status, last-event JSON
  dump in a `ScrollViewer`) subscribes to `UiChanged` the same way so it
  live-updates while open. `TooltipWindow` is a tiny one-shot window (unique
  `WindowKey` per show, closed on hide) — see the tooltip convention below.
- `CopilotHookInstaller.cs` — writes/deletes the Copilot CLI hook file
  `%USERPROFILE%\.copilot\hooks\pagurian-copilot-hook.json` (one file per hook
  provider; installed on startup, removed on quit + `ProcessExit` backstop):
  `{"version":1,"hooks":{...}}` registering all **14 camelCase events**
  (`sessionStart`, `sessionEnd`, `userPromptSubmitted`, `userPromptTransformed`,
  `preToolUse`, `postToolUse`, `postToolUseFailure`, `permissionRequest`,
  `agentStop`, `subagentStart`, `subagentStop`, `errorOccurred`, `preCompact`,
  `notification`), each `type:"command"` with `bash` + `powershell` forms of
  `"<exe>" --hook <event>` and `timeoutSec:10`. Two gotchas from the
  CopilotHookMonitor reference this mirrors: the file must be **UTF-8 without
  BOM** (a BOM makes the CLI reject the whole file as invalid JSON), and under
  `dotnet run` `Environment.ProcessPath` is `dotnet.exe`, so the hook must
  point at `AppContext.BaseDirectory\Pagurian.exe` instead.
- `CopilotHookBridge.cs` — bridge mode (`--hook <event>`): the CLI pipes the
  event payload JSON to stdin; the bridge wraps it in a one-line envelope
  `{"loggedAt":...,"event":...,"payload":<raw JSON|null>}`, appends it to
  `hook-events.log` next to the exe, forwards it to pipe
  `\\.\pipe\PagurianCopilotHook` (5 retries, 500 ms connect + 150 ms backoff),
  swallows all exceptions and **always exits 0** — preToolUse command hooks are
  fail-closed, so a non-zero exit would block Copilot tool calls.
- `CopilotSessionTracker.cs` — named-pipe server (background thread, one
  single-line JSON envelope per connection) that marshals every message to the
  UI thread via `UIDispatcher.TryEnqueue` before touching state (all session
  state is UI-thread-only — no locks). A session is created on the **first
  event with an unknown `sessionId`** (any event), kept in first-seen order
  (that is the cell order), and **removed when its `sessionEnd` event arrives**
  — a cell exists only while its session is alive (an open popup for it closes
  on the controller's next tick). Event → status: `sessionStart`/`agentStop` →
  Idle, `userPromptSubmitted`/`preToolUse`/`postToolUse`/`postToolUseFailure` →
  Working, `permissionRequest` → Blocked; all events update the pretty-printed
  `LastEventDump`. Changes bump `Version` and raise
  `UiChanged` (UI thread) — windows subscribe in `UseEffect` and re-render.
- `SystemMetricsTracker.cs` — background CPU/memory sampler. Uses
  `NtQuerySystemInformation` for per-logical-processor CPU, `GlobalMemoryStatusEx`
  for total physical memory, and per-process `TotalProcessorTime`/`WorkingSet64`
  for Top 3 rankings. Snapshots are immutable, published on the UI thread with a
  `Version`/`UiChanged` refresh-counter pattern, and sampled at 1-second cadence
  while the matching popup is visible and 10-second cadence otherwise.
- `SystemMetricsColors.cs` — small helper for the CPU (green) and memory (blue)
  gauge accents and the light/dark taskbar track colors.
- `TaskbarInterop.cs` — all P/Invoke (no extra packages): `SHAppBarMessage` /
  `FindWindow` / `GetCursorPos` / `MessageBoxW` / `ShowTrayMenu` (see below).
- `AppAssets.cs` — resolves asset paths from `AppContext.BaseDirectory` (both images are
  csproj `Content` with `CopyToOutputDirectory`; the .ico is also `ApplicationIcon`).

## Key conventions (learned empirically, not obvious from the code)

- **Reactor window APIs use DIPs, not physical pixels.** `WindowSpec.Width/Height`,
  `ManualPosition`, and `ReactorWindow.SetPosition/SetSize` all take DIPs and are
  scaled by the window DPI internally (verified on a 250% DPI machine: passing
  physical px placed windows 2.5× off-screen). Design sizes live as `*Dip`
  constants in the window classes. **Raw Win32 calls are the exception**:
  `SetWindowPos` for the injected icon child window, and the cursor hit-test
  rects (`GetCursorPos`, taskbar rects) are all physical px — convert with
  `ReactorWindow.DipScale` at the boundary.
- **Screen reads block (~1 frame each) — never on the UI thread.** Every read
  from the screen DC (`GetPixel`/`BitBlt` on `GetDC(NULL)`) synchronizes with
  DWM composition and stalls ~one display frame (measured: 17 ms idle, 33 ms
  under contention). Per-pixel `GetPixel` loops on the screen DC once cost
  ~50 frames per 50 ms tick, starving the UI thread — and because the
  cross-process `SetParent` attaches our input queue to Explorer's, the whole
  taskbar stopped responding (the bug this design fixes). Rules: capture with
  **one `BitBlt` per region** into a memory DC and read pixels from there
  (plain memory reads), do it on a **background thread**, throttle it, and
  apply brush changes via `UIDispatcher.TryEnqueue`.
- **Taskbar rect**: use `FindWindowW("Shell_TrayWnd")` + `GetWindowRect` first —
  it sends no message and reflects the actual on-screen position (auto-hide
  slide included). `SHAppBarMessage(ABM_GETTASKBARPOS)` is a cross-process
  `SendMessage` to the taskbar that can block the UI thread (see above), so
  it is only a fallback for when window enumeration doesn't see
  `Shell_TrayWnd`.
- **Hover detection is cursor polling, not XAML pointer events.** The windows are
  `NoActivate` topmost overlays; pointer-event modifiers proved unreliable in that
  configuration, so `TaskbarController` polls `GetCursorPos` and keeps pixel rects in
  sync. Keep that pattern if you add more hover targets. (Same for the pressed
  state and for clicks: `GetAsyncKeyState(VK_LBUTTON)` polling, via
  `TaskbarInterop.IsLeftButtonDown()` — a click is an up→down edge of that
  state while the cursor is inside the target rect.) **Consequence: XAML
  `.ToolTip()` doesn't fire either** — the session-name tooltip is a real
  borderless window (`TooltipWindow`) shown/hidden by the controller after a
  ~400 ms hover dwell.
- **Copilot session names** come from `%USERPROFILE%\.copilot\session-state\
  {sessionId}\workspace.yaml`, the top-level `name:` field, parsed with a plain
  line scan (no YAML dependency). Missing file/field → the session id is the
  display name; resolution is retried on every event until it succeeds because
  the file can lag `sessionStart`. (Event payloads also carry `initialPrompt`/
  `prompt`, but workspace.yaml is the source of truth by design.)
- **Theme comes from the sampled taskbar pixel, not from app theme APIs.** The
  widget must match the *taskbar*, which follows the Windows (system) mode and
  may be translucency/accent-tinted; `UseIsDarkTheme()`/`AppsUseLightTheme`
  follow the *app* mode and can disagree with it. Deriving light/dark from the
  sampled luminance is always consistent with the actual backdrop.
- **Message boxes**: use Win32 `MessageBoxW` (works headless in an unpackaged app);
  `ContentDialog` needs an owner window and was avoided deliberately.
- **Tray menu is a native Win32 popup menu**, not a Reactor flyout: on the tray icon's
  `RightClick` event, `Program.cs` calls `TaskbarInterop.ShowTrayMenu(ownerHwnd)`
  (`CreatePopupMenu` + `AppendMenuW` "Quit" = `QuitCommandId` + `TrackPopupMenu` with
  `TPM_RETURNCMD | TPM_NONOTIFY` at the cursor; `SetForegroundWindow` + posted `WM_NULL`
  so the menu dismisses correctly). The call blocks on the UI thread until selection;
  a `QuitCommandId` result runs the quit sequence.
- Reactor compiler warnings: images must end with `.AccessibilityHidden()` or
  `.AutomationName(...)` (REACTOR_A11Y_002); hooks may only be called inside a
  `Component.Render` override or a method named `Use*` (REACTOR_HOOKS_005 —
  that's why `TaskbarIconWindow` is a `Component`, not a render lambda);
  layout/alignment properties must use the pooled modifiers
  (`.VerticalAlignment(...)`, …), not `.Set(...)` (REACTOR_POOL_001 — `.Set`
  writes are lost on pool re-render). The build is kept at 0 warnings.
- The codebase mirrors `D:\Workspace\gitea_backup\Tea` (`Tea.Gui` project): same csproj
  settings (`net10.0-windows10.0.22621.0`, `UseWinUI`, `WindowsPackageType=None`,
  `Platforms x64;ARM64`), same Reactor version.
