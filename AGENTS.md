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
launch the app and observe the tray icon, taskbar tray, settings, billboards,
and message boxes.

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
into the Windows taskbar (child of `Shell_TrayWnd`) that lays out **Shells**
horizontally, each shell contributing zero or more **ShellCells**, (3) shows
**Billboards** (detail panels) above the tray when cells are clicked,
(4) receives messages for shells via `Pagurian.exe post {shell_id} <cmd>
[args...]` (the Copilot CLI hooks are the primary caller), and (5) has a
single paged **Settings window** for both host configuration and Shell
editing.

Concepts:

- **Tray** — the injected window; a horizontal stack of cells, ordered by the
  config file. Host-owned.
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

Current modules: **Hello** (clock cell + Hello billboard), **Metrics** (CPU
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
  self-contained folder under the host output's `modules\` folder.

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
  .LoadAll()`, `TrayShells.LoadFromConfig(TrayConfig.Load())`,
  `ShellMessageServer.Start()`, then tray icon + tray window + controller.
  The Settings window opens on the Shells page from tray-icon double-click or
  the tray menu's "Edit Shells…" item; "Settings…" opens the same window on
  its General page. Quit
  happens only via the tray menu:
  `TaskbarController.Stop()` → `ShellMessageServer.Stop()` →
  `SettingsWindow.CloseIfOpen()` →
  `TrayShells.ShutdownAll()` →
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
  `{ "tray": [ { "shell": "<FullName>", "id": "3842", "settings": {...}? } ] }`.
  Order = tray order. Ids are 4-digit, globally unique; duplicates are logged
  and skipped. The same kind twice = two instances. `settings` passes through
  to `Shell.Settings` verbatim (reserved). A missing file is seeded with only
  the Hello shell and a random id; an existing file is only ever modified
  through `Save(entries)` (temp file + atomic move, called by the Shell
  editor), and the host never auto-adds discovered shells. `NextId(taken)`
  allocates a fresh 4-digit id.
- `TrayShells.cs` — the ordered shell registry + flattened cell list.
  `Shell.AddCell/RemoveCell` call back through an internal channel; every
  change raises `Changed` → the tray window re-renders as a whole (no diff).
  Also routes `post` messages by `Shell.InstanceId`.
  `ApplyConfig(entries)` reconciles the live set with a config list without
  restarting unchanged shells (kept by id when the kind still matches),
  reorders to the list order, and rebuilds cells once at the end.
- `SettingsWindow.cs` / `SettingsView.cs` — the singleton 1600×900
  Settings window and its `NavigationView` root. The General page owns the
  config-directory row (TextBox + folder picker →
  `HostSettings.SetConfigDir` → config reload → `TrayShells.ApplyConfig`).
  The Shells page keeps the module catalog or per-instance
  `ShellConfiguration` in the scrollable center, with the draft Tray fixed
  at the bottom and horizontally scrollable. The center uses a nested
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
  `TrayShells.ApplyConfig`, and keeps Settings open; Revert reloads the last
  persisted configuration into that draft. Closing with unsaved changes
  discards only after confirmation. Icons:
  `[Shell].PreviewIconPath` with `AppAssets.ModuleFallbackIconPath` fallback.
- `PostBridge.cs` / `ShellMessageServer.cs` — the `post` pipeline. The bridge
  packs `{id, command, args, payload, receivedAt}` (stdin piped → payload,
  embedded verbatim as raw JSON) onto the `Pagurian.ShellMessages` pipe with
  retries and **always exits 0** (callers are fail-closed). The server
  marshals each envelope to the UI thread and routes it; unknown ids / empty
  commands are dropped with a log line.
- `TaskbarTrayWindow.cs` — the Tray. Renders
  `TrayShells.Cells` as keyed cell Borders (host chrome: hover overlay live
  brush, 3×4 DIP hover margins, width read-back `Ref`), each wrapping a
  `ComponentElement(cell.ViewType, cell.Props)`. Owns the sampled taskbar
  blend gradient, the content-scale transform, and the hover-brush cache.
- `TaskbarTrayLayout.cs` — cell widths read back from mounted controls'
  `ActualWidth` through the refs; the window starts at a 1-DIP floor and
  grows within a tick or two.
- `TaskbarController.cs` — the 50 ms poll loop on the UI dispatcher:
  inject/anchor into `Shell_TrayWnd` (re-inject after Explorer restarts),
  background-throttled taskbar color sampling (NEVER on the UI thread — see
  conventions), theme derivation, per-cell hover/pressed overlays, cursor
  hit-testing, click dispatch (custom `OnClicked` delegate wins; otherwise
  the cell's billboard toggles), hover-dwell tooltips, and unified billboard
  management (position above owner cell / below at top-edge taskbars,
  outside-click dismiss, one at a time, auto-close when the owner cell is
  removed; `Billboard.OnOpened/OnClosed` invoked around the window lifetime;
  fresh billboard instance per show via the cell's `CreateBillboard`).
- `ThemeService.cs` — host `IThemeService`: `IsDark` from sampled luminance
  (Rec.601, threshold 140 — always matches the taskbar itself), the shared
  live `TextBrush` (mutated in place on flips), and the `Changed` broadcast.
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
- **Taskbar rect**: `FindWindowW("Shell_TrayWnd")` + `GetWindowRect` first
  (no message sent); `SHAppBarMessage(ABM_GETTASKBARPOS)` is a blocking
  cross-process `SendMessage` — fallback only.
- **Hover/click detection is cursor polling**, not XAML pointer events (the
  windows are NoActivate topmost overlays). Clicks are up→down edges of
  `GetAsyncKeyState(VK_LBUTTON)`. XAML `.ToolTip()` doesn't fire either —
  tooltips are real borderless windows (`TooltipWindow`) after a ~400 ms
  dwell.
- **Theme comes from sampled taskbar pixels**, not app theme APIs.
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
- **Session names** come from
  `%USERPROFILE%\.copilot\session-state\{id}\workspace.yaml`, top-level
  `name:` field, plain line scan; retried on every event until resolved.
- The codebase mirrors `D:\Workspace\gitea_backup\Tea` (`Tea.Gui` project):
  same csproj settings and Reactor version.

## Docs

- `docs/Plugin-Architecture-Handoff.md` — the approved architecture plan this
  code implements. One deliberate implementation deviation: the plan sketched
  cell behavior as virtual members on `ShellCell`; because Reactor owns
  component instances, behavior is instead passed as delegates on
  `ShellCellProps` at `AddCell` time (concepts unchanged).
- `docs/roadmap/System-Metrics-Widgets-Design.md` — metrics cadence rules.
