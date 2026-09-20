# Copilot Instructions — Pagurian

## Build and run

Always build/run with an explicit platform (required for WinUI 3):

```powershell
dotnet build Pagurian.sln -p:Platform=x64
dotnet run --project Pagurian -p:Platform=x64
```

**Requires .NET SDK 10.0.302+** — SDK 10.0.302 is verified with Microsoft.UI.Reactor 0.1.0-preview.12. The library projects suppress their own project PRI files while the WinExe produces the app PRI, which keeps the standard x64 build working on this SDK.

There is no general unit-test suite or linter. Run
`tests\Verify-ModuleIsolation.ps1` for the module load-context contract, then
launch the app and observe the tray icon, taskbar trays, settings, billboards,
and message boxes.
`tests\Verify-TrayTopology.ps1` covers config migration, the tray→surface
binding engine and its fallback chain with fabricated displays.
`tests\Verify-CopilotSessions.ps1 -Platform x64` runs the dependency-free
Copilot identity/state/dispatcher fixture using temporary sanitized metadata
and transcripts; it does not launch Pagurian or change user hook/session files.
`tests\Verify-CopilotAppLifecycle.ps1 -Platform x64` exercises read-only SQLite,
WAL/lock/schema behavior and injected App/SDK process handles using repository
`artifacts` fixtures, never the user's database/processes. Optional `-BundlePath`
checks deployed SQLite managed/native resolution through `ModuleLoadContext`.

### Quick compile check on macOS (temporary development)

A full build requires Windows (the WinAppSDK self-contained step runs
`mt.exe`, a Windows-only binary). For fast feedback while coding on macOS:

```bash
dotnet build Pagurian.sln -p:EnableWindowsTargeting=true
```

Treat "compiles clean up to the mt.exe/manifest errors" as a passing compile
check; use a Windows machine/VM/CI for full builds and running the app. (The
library projects set `EnableMsixTooling=false` + `AppxGeneratePriEnabled=false`
so they don't run the Windows-only MakePri step; the PRI payloads are expanded
by the host project instead.)

## What this app is

A WinUI 3 Fluent-style utility that (1) runs a system-tray icon with a
native Win32 context menu ("Edit Shells…", "Settings…", and Quit;
double-click opens Edit Shells), (2) injects a borderless **Tray** window
into every display's Windows taskbar (child of `Shell_TrayWnd` /
`Shell_SecondaryTrayWnd`) that lays out **Shells**
horizontally, each shell contributing zero or more **ShellCells**, (3) shows
**Billboards** (detail panels) above the tray when cells are clicked,
(4) receives messages for shells via `Pagurian.exe post {shell_id} <cmd>
[args...]` (the Copilot CLI hooks are the primary caller), and (5) has a
single paged **Settings window** for both host configuration and Shell
editing.

Concepts:

- **Tray (logical)** — a configured, ordered set of shells belonging to one
  display/edge pair (`TrayId`); a config citizen that exists regardless of
  whether its display is connected. Ordered by the config file. Host-owned.
- **Surface (Tray surface)** — the injected window on one live display's
  taskbar; renders every tray bound to it. One per (display, edge); only the
  primary surface exists unconditionally.
- **Shell** — a configured module feature instance (plain object, not a
  component) with a persistent 4-digit id; the `post` target; a container of
  cells. A shell with zero cells occupies no tray space.
- **ShellCell** — a Reactor `Component` subclass rendering one cell's
  content; hover/click/tooltip/billboard are per-cell. Cells have no id and
  receive no messages.
- **Billboard** — a Reactor `Component` subclass popped up above the owner
  cell on click; content/size/lifecycle by the module, chrome/placement/
  dismissal by the host. One at a time.
- **Module** — an independent folder bundle (own csproj in this sln), loaded
  from `<exe>\modules\<name>\<name>.dll` or
  `%LOCALAPPDATA%\Pagurian\modules\<name>\<name>.dll`. The matching
  `.deps.json`, private dependencies, and assets live in the same folder.

Current modules: **World Clock** (per-city clock cell + World Clock billboard;
one city per shell instance, chosen in its configuration), **Metrics** (CPU
and MEM shells, each standalone, sharing one sampler), **Copilot** (CLI hook
installer + per-session dynamic cells).

## Solution layout

- `Pagurian` — the host (WinExe). See "Host architecture" below.
- `Pagurian.Sdk` — the module contract assembly. Everything a module author
  references: `PagurianModule` + `[PagurianModule]`, `Shell` + `[Shell]`,
  `ShellCell` (+ `ShellCellProps`/`ShellCellHandle`), `Billboard`,
  `IThemeService`, `ShellMessage`, `Logger`, `TextMeasurement`,
  `ModuleAssets`, `MessageBoxes`. `[InternalsVisibleTo("Pagurian")]` hides
  the host infrastructure members from modules.
- `Pagurian.Modules.Hello` / `.Metrics` / `.Copilot` — the first-party
  modules. Each has a `DeployToHostModules` post-build target creating its
  self-contained folder under the host output's `modules\` folder. The
  `Pagurian.Modules.Hello` project (kept under its historical name so the
  module bundle identity is stable) ships the **World Clock** module: types
  `WorldClockModule`/`WorldClockShell`/etc. under the `Pagurian.Modules.Hello`
  namespace, kind id `Pagurian.Modules.Hello.WorldClockShell`.

All projects target `net10.0-windows10.0.22621.0`, `UseWinUI`, platforms
x64;ARM64, Microsoft.UI.Reactor 0.1.0-preview.12. Each module loads in a
private non-collectible `AssemblyLoadContext` backed by
`AssemblyDependencyResolver`; Pagurian.Sdk/Reactor/WinUI stay shared from
Default so the host and modules retain one UI contract type universe. SDK and
Reactor package versions must match exactly. See `docs/External-Modules.md`.

## Host architecture (`Pagurian` project)

- `Program.cs` — single entry. First statement is the **bridge-mode
  intercept**: `Pagurian.exe post {id} <cmd> [args...]` runs `PostBridge.Run`
  and returns before touching WinUI. Otherwise `ReactorApp.Run(startup)`:
  `ShutdownPolicy.Explicit`, `PagurianLog.Initialize()`, `ModuleLoader
  .LoadAll()`, `TrayManager.LoadFromConfig(TrayConfig.Load())`,
  `ShellMessageServer.Start()`, then tray icon + tray surfaces + controller.
  The Settings window opens on the Shells page from tray-icon double-click or
  the tray menu's "Edit Shells…" item; "Settings…" opens the same window on
  its General page. Quit
  happens only via the tray menu:
  `TraySurfaceController.Stop()` → `ShellMessageServer.Stop()` →
  `SettingsWindow.CloseIfOpen()` →
  `TrayManager.ShutdownAll()` →
  `ModuleLoader.ShutdownAll()` → `tray.Close()` → `ReactorApp.Exit(0)` →
  `Environment.Exit(0)` (the last call is required).
- `ModuleLoader.cs` / `ModuleLoadContext.cs` — folders-only bundle discovery,
  exact SDK/Reactor compatibility metadata validation, private managed/native
  dependency resolution, and strict reflection validation: exactly one
  `[PagurianModule]`, valid `[Shell]`/configuration types, and deduplicated
  module/kind ids. Host-owned SDK/Reactor/WinUI assemblies are rejected from
  bundles and shared from Default. One failing bundle never stops the host;
  details go to the unified log.
- `HostSettings.cs` — registry-backed host settings:
  `HKCU\Software\Pagurian\ConfigDir` overrides the configuration directory
  (default `%LOCALAPPDATA%\Pagurian`; writing the default deletes the
  value). Read via `HostSettings.ConfigDir`.
- `TrayConfig.cs` — `<HostSettings.ConfigDir>\config.json`:
  `{ "trays": [ { "monitor": "primary" | "<EDID identity key>", "edge"?: "left",
  "shells": [ { "shell": "<FullName>", "id": "3842", "settings": {...}? } ] } ] }`.
  Each group is one logical tray (a display/edge pair); entry order = tray order.
  Ids are 4-digit, globally unique across all trays (`post` routes by bare id);
  duplicates are logged and skipped. The same kind twice = two instances.
  `settings` passes through to `Shell.Settings` verbatim (reserved). A legacy
  flat `"tray"` array maps to the primary left tray in memory (rewritten as v2
  on the next Save). A missing file is seeded with only
  the World Clock shell (tracking local time) and a random id; an existing
  file is only ever modified
  through `Save(groups)` (temp file + atomic move, called by the Shell
  editor), and the host never auto-adds discovered shells. `NextId(taken)`
  allocates a fresh 4-digit id.
- `Trays/TrayId.cs` — `TrayId(MonitorKey, Edge)` (logical tray identity;
  `MonitorKey` is `"primary"` or a display's EDID identity key, never a GDI
  ordinal) and `SurfaceKey(DisplayKey, Edge)` (a live display's taskbar
  surface). Right-edge trays bind to their display's right surface and anchor
  left of the taskbar's system area (the notification tray on the primary
  taskbar, the clock on secondaries — `TaskbarInterop
  .TryGetTaskbarSystemAreaLeft` accepts only visible, valid, taskbar-process-owned
  notification/clock windows; `TaskbarSystemAreaObserver` reads HWND-less
  secondary clocks through a bounded background MTA UIA query). Arbitrary
  narrow/right-anchored windows are never system-area evidence: injected
  Pagurian windows previously made that heuristic oscillate. Initial Unknown
  or changed taskbar/owner/geometry hides the right window until reliable
  evidence arrives; transient failures retain evidence only within the same
  context. Shells keep running. Boundary diagnostics are per context, deduped,
  rate limited and asynchronous; on vertical taskbars right trays fold to the
  left surface. Right-surface cells render in reversed config order so the
  first configured cell sits nearest the system area.
- `Displays/DisplayInterop.cs` + `Displays/DisplayTopology.cs` — the physical
  world: EnumDisplayMonitors geometry joined with DisplayConfig EDID identity
  (`MODEL-UID` keys, friendly names like "DELL U2720Q") and each display's
  taskbar window (`Shell_TrayWnd` + `Shell_SecondaryTrayWnd` via
  MonitorFromWindow). `Refresh()` raises `Changed` on structural change only.
  Last-seen name/geometry per identity persist to
  `<HostSettings.ConfigDir>\monitors.json` (atomic write, cap 64) for
  fallback matching and the settings picker's disconnected rows.
- `Trays/TrayManager.cs` + `Trays/ShellTray.cs` — the logical world: one
  ShellTray per configured TrayId (ordered shells, per-tray reconciliation,
  coalesced `Changed`). Shells keep running and `post` keeps routing while
  their display is absent; the binding engine maps each tray to a live
  surface: exact display → closest aspect ratio to the recorded geometry
  (monitors.json, |ln(a/b)| ≤ 0.15) → a display no configured tray owns →
  primary. `CellsForSurface(key)` composes bound trays in config order;
  `ApplyConfig(groups)` never restarts shells staying on the same tray (a
  cross-tray move is a restart). Surface themes are registered by surfaces
  and pushed into bound trays/shells.
- `Trays/TraySurface.cs` + `Trays/TraySurfaceController.cs` — the
  presentation world: one TrayWindowSession + one sampled ThemeService per
  live surface. The controller's 50 ms tick polls input; every ~1.25 s it
  refreshes the topology and reconciles surfaces (primary-left always exists
  as the tray-icon menu owner; right surfaces and non-primary left surfaces
  exist only while cells are bound, so an empty right tray injects nothing).
  Hover/
  tooltip/click dispatch iterates surfaces; the tooltip flips against the
  owning display's monitor rect, and the single billboard session uses the
  owner surface's theme.
- `SettingsWindow.cs` / `SettingsView.cs` — the singleton 1600×900
  Settings window and its `NavigationView` root. The General page owns the
  config-directory row (TextBox + folder picker →
  `HostSettings.SetConfigDir` → config reload → `TrayManager.ApplyConfig`).
  The Shells page keeps the module catalog or per-instance
  `ShellConfiguration` in the scrollable center, with the draft Tray fixed
  at the bottom. The Tray container is split into two drop zones per
  selected display: the left half edits the left tray (icons ordered left to
  right), the right half edits the right tray (icons hug the zone's right
  edge, ordered outward from the system area; insertion candidates are
  measured as distance from that edge, so the config-order spacing math is
  shared). Dragging an icon across halves moves it between trays. The draft
  is a list of per-display trays; a monitor-picker button at the right end
  of the Tray
  container's title row opens a scaled display-layout preview (friendly
  names, never display numbers; disconnected configured displays listed
  below, one row per display covering both edges)
  and switches which display's trays are being edited. Selecting a display
  alone never dirties the draft. The center uses a nested
  `NavigationHost` with `Modules` and per-instance `ShellConfiguration`
  routes: configurations enter from the right with
  `NavigationTransition.Spring()`, and Back plays the reverse transition.
  Reduce Motion uses `NavigationTransition.None`. Only the scrollable center
  participates; the Tray/footer remains fixed outside the transition. Module
  Shells are drag-only:
  any drag movement starts the native drag immediately, and dropping it into the Tray
  adds it. Drag a Tray icon within the Tray to reorder it, or drop it onto the
  central Modules/configuration panel (highlighted with the critical border)
  to remove it. Other page regions and the area outside the app reject the
  drop. Drag hover preserves every Tray layout slot, temporarily hides the
  dragged source, and overlays one vertical insertion marker among the remaining
  icons; the reordered or newly added list appears only after an accepted drop.
  Pressing Esc or a system cancellation restores the original list. Explicit
  Remove and Delete/Backspace remain available removal operations. The shared Shells
  draft, including per-instance
  `ShellConfiguration` settings, survives page switches. Save persists the
  complete draft with `TrayConfig.Save`, applies it with
  `TrayManager.ApplyConfig`, and keeps Settings open; Revert reloads the last
  persisted configuration into that draft. Closing with unsaved changes
  discards only after confirmation. Icons:
  `[Shell].PreviewIconPath` with `AppAssets.ModuleFallbackIconPath` fallback.
- `PostBridge.cs` / `ShellMessageServer.cs` — the `post` pipeline. The bridge
  packs `{id, command, args, payload, receivedAt}` (stdin piped → payload,
  embedded verbatim as raw JSON) onto the `Pagurian.ShellMessages` pipe with
  retries and **always exits 0** (callers are fail-closed). The server
  marshals each envelope to the UI thread and routes it; unknown ids / empty
  commands are dropped with a log line.
- `TaskbarTrayWindow.cs`: keyed cell Borders and stable hover brushes
  (reference-counted per cell key; a rebinding cell briefly lives on two
  surfaces). Cells measure naturally inside the session's left-aligned host
  panel; each window renders only its surface's cells.
- `TaskbarTrayLayout.cs`: per-window cell refs and immutable measured
  snapshots shared by native positioning, hit-testing and billboard anchors.
- `TrayWindowSession.cs`: persistent HWND, hidden preparation, injection,
  coalesced layout commits and a rendered/DwmFlush presentation gate. One
  session per surface; sampled luminance feeds the owning surface's theme
  via the injected theme sink.
  `TrayBackgroundSampler.cs` captures spatial taskbar strips off the UI thread;
  resizing reuses the cached strip. See `docs/Tray-Lifecycle.md` for native
  child-window requirements and the pinned Reactor native-loss adapter.
  System-area boundaries constrain placement separately from the full taskbar
  sampling rectangle, so clock-anchor and cell-width changes reuse the spatial
  background cache. `tests\Verify-TrayLifecycle.ps1 -Right` exercises the
  production boundary/observer/environment loop; `-SecondaryTaskbar <HWND>`
  explicitly verifies a live secondary clock without loading user settings.
- `ThemeService.cs` — host `IThemeService`, one instance per tray surface:
  `IsDark` from sampled luminance (Rec.601, threshold 140 — always matches
  that surface's taskbar), the shared live `TextBrush` (mutated in place on
  flips), and the `Changed` broadcast.
- `PagurianLog.cs` — the unified log (`%LOCALAPPDATA%\Pagurian\pagurian.log`,
  timestamp + level + tag). Installs the Sdk `Logger` sink; host writes with
  tag `host`.
- `ShellNavigationDiagnostics.cs` — passive Shell configuration navigation
  probes on the existing host/page controls (no wrappers or Composition access).
  The unified log's `shell-navigation` tag correlates session, PID, event sequence,
  navigation requests, accepted routes, Reactor mount/unmount and XAML
  Loaded/Unloaded. Coalesced snapshots after navigation and at 250ms/1s/2s list
  native host children; `suspected-residue` at the final check is a diagnostic
  hint, not proof of compositor state. XAML opacity is explicitly not animated
  Composition opacity. Payloads contain IDs and structural metadata, never
  configuration values or control text. A bounded asynchronous writer reports
  queue overflow and flushes on close/exit. Reproduce rapid A→B→C selection,
  wait at least 2 seconds, then inspect this tag before closing Settings.
  `tests\Verify-DiagnosticLogQueue.ps1` checks ordering, overflow and flush;
  `tests\Verify-NavigationDiagnostics.ps1` opens an isolated nonactivating
  test window to check live probes, delayed snapshots and teardown without
  loading user settings or writing to the user log. The latter needs an
  interactive Windows desktop (`-BuildOnly` skips its UI run).
- `TooltipWindow.cs`, `TaskbarInterop.cs` (all P/Invoke), `AppAssets.cs`
  (application and module-fallback icon paths).

## Module contract essentials (Pagurian.Sdk)

```csharp
[PagurianModule(DisplayName = "...")]           // one per assembly; id = type FullName
public sealed class MyModule : PagurianModule { public override void Startup()/Shutdown() ... }

[Shell(DisplayName = "...")]                    // per Shell subclass; kind id = type FullName
                                                // optional PreviewIcon = "Assets/foo.png" feeds the
                                                // Shell editor (fallback: the Pagurian app icon)
public sealed class MyShell : Shell
{
    public override void Startup() =>
        AddCell<MyCell>(model: ..., tooltip: () => ..., billboard: () => new MyBillboard(...));
    public override void OnMessage(ShellMessage m) ...   // "post {id} <cmd> args" arrives here
}
```

- `Shell` lifecycle: `Startup()` (heavy resources online — samplers, hook
  files), `Shutdown()`, `OnMessage(ShellMessage)` (UI thread). Context is
  injected before Startup: `InstanceId`, `Settings`, `Theme`, `Log`.
- `AddCell<TCell>(model, tooltip, billboard, onClicked)`: `TCell` is the
  Reactor view component; behavior comes as delegates capturing the model
  (they read live state). `onClicked` set → replaces the default
  toggle-billboard behavior entirely.
- **Reactor mechanics constraint**: Reactor embeds child components by TYPE
  and creates the instances itself (`ComponentElement(Type, props)`), so a
  cell's mutable per-cell state must live in the `model` object
  (`Props.Model`), never in cell instance fields. Cells subclass
  `ShellCell : Component<ShellCellProps>` and override `Render()` as usual
  (hooks allowed there); conveniences: `Owner`, `ModelAs<T>()`, `Theme`,
  `Log`.
- `Billboard : Component`: `WidthDip`/`HeightDip`/`Title`, `OnOpened()`/
  `OnClosed()` (self-report hooks — the metrics billboards switch sampler
  cadence here), `Shell`/`Theme`/`Log` accessors. A fresh instance is created
  per show.
- Theme: cells and billboards receive the sampled taskbar theme; shell
  configuration views receive the effective Windows theme of the Settings
  surface. Read `Theme.IsDark` for computed colors, use `Theme.TextBrush`
  for body text, and subscribe `Theme.Changed` in `UseEffect` when computed
  colors must be refreshed.
- Assets: ship next to the module dll, resolve with
  `ModuleAssets.Resolve(typeof(MyModule), "Assets/foo.png")`.

## Key conventions (learned empirically)

- **Reactor window APIs use DIPs; raw Win32 calls use physical pixels.**
  `WindowSpec.Width/Height`, `ManualPosition`, `SetPosition/SetSize` are DIPs;
  `SetWindowPos`, `GetCursorPos`, taskbar rects are physical px — convert
  with the window `DipScale` at the boundary. Anchor geometry derives from
  the live taskbar rect (48 DIP thickness ⇒ `taskbar.Height / 48` is the
  taskbar's own DPI scale), never from the window's `DipScale`.
- **Screen reads block (~1 frame each) — never on the UI thread.** Capture
  with one `BitBlt` per region on a background thread, throttle repeated
  sampling, and apply via `UIDispatcher.TryEnqueue`. (History: per-pixel
  `GetPixel` loops
  on the UI thread once wedged the whole taskbar because the cross-process
  `SetParent` attaches our input queue to Explorer's.)
- **Taskbar rect**: `GetWindowRect` on the display's own taskbar HWND first
  (no message sent); `SHAppBarMessage(ABM_GETTASKBARPOS)` is a blocking
  cross-process `SendMessage` — primary-only fallback only.
- **Hover/click detection is cursor polling**, not XAML pointer events (the
  windows are NoActivate topmost overlays). Clicks are up→down edges of
  `GetAsyncKeyState(VK_LBUTTON)`. XAML `.ToolTip()` doesn't fire either —
  tooltips are real borderless windows (`TooltipWindow`) after a ~400 ms
  dwell, flipped against the owning display's monitor rect (never the
  primary origin — secondary displays can be negative-coordinate).
- **Theme comes from sampled taskbar pixels**, per tray surface, not app
  theme APIs.
- **Message boxes**: Win32 `MessageBoxW` (`MessageBoxes.Show` in the Sdk).
- **Tray menu is a native Win32 popup menu** (`TaskbarInterop.ShowTrayMenu`,
  blocks the UI thread until selection; returns `EditShellsCommandId`,
  `SettingsCommandId`, or `QuitCommandId`, dispatched by Program.cs).
- **Fluent modifiers need `using Microsoft.UI.Reactor;`** — the pooled
  element extensions (`ElementExtensions`, `GridSize`, …) live in that
  namespace inside Reactor.dll; `Microsoft.UI.Reactor.Core` alone gives you
  the element types but not the modifiers (CS1955/CS0103).
- Reactor compiler warnings: images must end with `.AccessibilityHidden()`
  or `.AutomationName(...)` (REACTOR_A11Y_002); hooks only inside
  `Component.Render` overrides or `Use*` methods (REACTOR_HOOKS_005);
  layout/alignment via pooled modifiers, not `.Set(...)` (REACTOR_POOL_001).
  The build is kept at 0 warnings.
- **Copilot hook file** (`Pagurian.Modules.Copilot`): UTF-8 **without BOM**
  (a BOM makes the CLI reject the whole file); under `dotnet run`
  `Environment.ProcessPath` is `dotnet.exe`, so the hook points at
  `AppContext.BaseDirectory\Pagurian.exe`; commands are
  `"<exe>" post {shellId} hook <event>` for all 14 camelCase events; the
  shell's persistent config id is what keeps the hook stable across restarts.
- **Copilot hook event audit**: `HookEventLog` writes JSONL to
  `%LOCALAPPDATA%\Pagurian\Copilot\`, using one exclusive
  `hook-events-YYYYMMDD-HHmmss-fff.log` per bridge process. Concurrent
  processes choose another timestamp rather than overwrite; creating a new
  run file removes only matching files whose filesystem creation date is
  before today. This is separate from `pagurian.log`, Compass logs, hook
  installation, and Copilot session files, and remains best-effort.
- **Copilot session identity and names**: `CopilotSessionIdentityResolver`
  reads `%USERPROFILE%\.copilot\session-state\{id}\workspace.yaml` off the UI
  thread. Top-level `client_name: github/autopilot` positively identifies an
  App root; explicit other clients retain independent ownership. Branding is
  separate: `github/cli` → CLI, `vscode`/`vscode-agent-host` → VS Code, unsupported
  markers → Other (never guess from cwd). This is existing-hook VS Code support,
  not built-in Chat ingestion. Every independent Copilot Sessions owner needs
  its own nonblank `workspace.yaml` `name` before a cell is published, across
  App/CLI/VS Code/Other; ID/project/task-name fallbacks do not qualify.
  Unnamed sources still reduce hooks, blockers and details, and metadata
  polling admits them when named. Partial name rewrites retain the last valid
  name. Task children need no independent name to aggregate under a named root.
  This does not change the separate Copilot SDK Shell.
  `name:` and `cwd:` remain source-owned; published
  task nodes preserve their immediate parent and own name. Missing/partial client metadata stays unresolved (no
  cell, no timeout-based guess); reduced source state waits for resolution.
  Independently persisted App sessions, including `create_session` children,
  keep separate cells. App task children never get their own cell: explicit
  lifecycle relationships and incrementally indexed `events.jsonl`
  `subagent.started`/`subagent.completed` top-level `agentId` route them to
  their owning App root, including nested tasks. Early hooks need not contain
  `transcriptPath` or `subagentStart.agentId`. Recent local App roots are
  discovered in the background for mid-session attachment; cwd, trace ID,
  agent name, and absent workspace files are never parentage evidence.
- **Copilot status/lifecycle**: `CopilotSessionState` immediately reduces
  each source's own state. Any active blocker wins over Working, which wins
  over Idle. Working hooks clear only their emitting source's permission;
  `agentStop` makes only that source Idle. `subagentStop.agentId` deactivates
  an App child (not `payload.sessionId`, which is the parent); the identity
  remains for multi-turn resume. Child `sessionEnd` cannot remove a root's
  cell/billboard. App root `sessionEnd(reason=complete)` ends only that source's
  turn and retains
  the cell; absent/other reasons also await authoritative visibility evidence.
  Independent CLI/VS Code/Other roots retain their terminal `sessionEnd` policy.
  App archive and verified owning-process exit, not hooks or transcript shutdown,
  hide App groups. Timestamp ordering rejects older
  unblock/stop delivery; absent timestamps use bridge `ReceivedAt`.
  The existing one-second visual debounce applies to the final aggregate,
  never to source bookkeeping. Sibling events cannot reset a stable blocked
  candidate. Latest details keep the original payload/source; current
  blockers have separate provenance. Bounded payload-free transition
  diagnostics go through the module logger. Tracker Stop invalidates queued
  callbacks, cancels background resolution, stops its timer, and clears state.
  See `docs/Copilot-Sessions.md` for discovery limits and regression coverage.
- **Copilot icon directory cleanup**: every 10 seconds the existing resolver
  worker checks only published owner directories using lightweight attribute
  probes and monotonic scheduling, with no extra DB/transcript reads or UI I/O.
  `CopilotSessionDirectoryReader` reports Missing only for explicit path
  absence beneath an accessible non-reparse session-state root (rechecked on
  absence); errors, root unavailability and unexpected/reparse paths are
  Unknown, retaining visibility with throttled diagnostics. Missing individual
  metadata/transcript/lock files or child directories does not remove a root.
  Confirmed Missing removes that icon/billboard without a grace period or
  sessionEnd requirement. No user files/DB/processes are modified.
  Directory-deleted owners require a fresh own hook plus a restored directory
  and freshly read nonblank own name before a new cell; cached identity cannot
  restore them. Old work is fenced without discarding conversation usage/history.
  Directory evidence versions, owner revisions and tracker generation fence
  stale callbacks. App archive/exit and CLI terminal rules remain independent.
  Multiple Copilot Sessions Shells share one cleanup schedule.
- **Copilot App visibility evidence**: the existing background resolver publishes
  coherent versioned identity/lifecycle batches. `CopilotArchiveReader` opens
  `.copilot\data.db` read-only with Microsoft.Data.Sqlite (WAL-aware, one-second
  lock timeout, parameterized IDs, schema checks and a read transaction).
  Direct workspace session IDs and aliases use `workspaces.archived_at`;
  only unmapped explicit standalone chats use `sessions.archived_at`.
  Missing/conflicting/unsupported evidence is Unknown, never ended.
  `CopilotAppProcesses` validates the owner PID hint against github.exe path,
  GitHub company metadata and process creation time; inuse locks must correlate
  to live SDK processes and verified App ancestry. Retained handles prove exit;
  missing hints, denied queries, SDK detach/survival and UI closure do not.
  Loaded-session evidence bypasses the seven-day metadata cutoff only for IDs
  already observed through hooks; locks, metadata and DB rows alone never
  restore a root. It restores only positively identified, loaded,
  known-nonarchived App roots, never all DB rows. Archive/unarchive and
  exit/restart have monotonic visibility/work fences;
  unarchive requires a new positive load, not a late hook or old lock.
  A new App work epoch resets stale blockers/active tasks/debounce to Idle without
  overwriting fresh hooks or discarding conversation usage/history.
  SQLitePCLRaw 2.1.13 managed/native private assets deploy for x64 and ARM64;
  host-owned assemblies remain excluded. These observed App internals may change;
  fail closed for new restoration, retain existing visibility on Unknown, and
  log bounded reason codes. See
  `docs/IssueHistory/2026-09-11-Copilot-App-Session-Lifecycle.md`.
- **Copilot session presentation/details**: two caption rows (client icon +
  aggregate status, then cwd basename) share a WinUI-measured status-based width;
  finite Grid columns ellipsize project names, and the host tooltip exposes cwd.
  Icons deploy under module `Assets` and use `ModuleAssets`. The billboard
  header aggregates, its tree shows local status, and its breakdown includes
  all retained nodes. Completed/ended nodes count toward disjoint known usage
  but not active status. The shared bounded background transcript reader allows
  2 MiB lines and retains whitelisted telemetry scalars, not prompts. Deduplicate
  call aliases; cumulative session checkpoints are separately labelled and
  never added as own/group usage. Missing is unavailable; incomplete is Partial.
  Context occupancy requires explicit evidence, not last-request input tokens.
  The bottom five hook rows contain only local HH:mm:ss, eventType and toolName
  (`-` when absent). Generation fences cover rows/nodes/details. Clipboard copy
  uses the clicked HWND with checked feedback; never globally activate billboards.
  `tests\Verify-CopilotSessionPresentation.ps1 -Platform x64` uses isolated UI;
  `-Clipboard` additionally replaces clipboard text for roundtrip/contention tests.
- The codebase mirrors `D:\Workspace\gitea_backup\Tea` (`Tea.Gui` project):
  same csproj settings and Reactor version.

## Docs

- `docs/Plugin-Architecture-Handoff.md` — the approved architecture plan this
  code implements. One deliberate implementation deviation: the plan sketched
  cell behavior as virtual members on `ShellCell`; because Reactor owns
  component instances, behavior is instead passed as delegates on
  `ShellCellProps` at `AddCell` time (concepts unchanged).
- `docs/roadmap/System-Metrics-Widgets-Design.md` — metrics cadence rules.
