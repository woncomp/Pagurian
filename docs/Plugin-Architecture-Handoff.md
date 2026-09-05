# Plugin Architecture Refactor — Handoff Document

> Implementation update: modules now use mandatory per-module folders and a
> private `AssemblyLoadContext` backed by `AssemblyDependencyResolver`.
> Pagurian.Sdk/Reactor/WinUI contracts are explicitly shared from the host.
> See `docs/External-Modules.md` for the current deployment contract.

Status: **approved plan, not yet implemented**. This document is the complete,
self-contained specification for refactoring Pagurian from a monolithic WinUI 3
app into a host + plugin (module) architecture. It supersedes the relevant
parts of `AGENTS.md` once implemented.

## 1. Conceptual model

```
Tray (host container) -> Shell (configured instance, persistent 4-digit id, post target)
                             -> ShellCell (display/interaction unit, 0..N per shell)
                                    -> Billboard (details panel, opened on click)
```

- **Tray** — the existing borderless window injected into the Windows taskbar.
  A horizontal stack container that lays out Shells in config order and
  renders each Shell's ShellCells.
- **Shell** — a plain class (NOT a Reactor `Component`). A module feature
  instance plus a container of cells. A regular shell attaches exactly one
  cell; a dynamic shell (Copilot) has zero cells most of the time (occupying
  no space) and adds/removes cells in response to events. The old "ShellButton"
  concept is absorbed into this mechanism — there is no separate ShellButton
  type.
- **ShellCell** — a Reactor `Component` subclass; the display/interaction
  unit. Content, hover highlight, click, tooltip, and billboard factory are
  all per-cell. Cells have **no id and receive no post messages**; the host
  tracks them with private handles (billboard/tooltip ownership) that are
  never exposed to modules.
- **Billboard** — a Reactor `Component` subclass; the panel that pops up when
  a cell is clicked. Content, size, and open/close behavior are defined by the
  module; positioning, mutual exclusion, and outside-click dismissal are
  managed by the host.
- **Module** — a standalone assembly. The module class is marked with
  `[PagurianModule]` (its type FullName IS the module id); Shell subclasses
  are marked with `[Shell]` (the type FullName IS the shell kind id). Modules
  are strictly decoupled from each other.

Key naming decisions (final):

- `Shell.Startup()` / `Shell.Shutdown()` — shell lifecycle (was
  OnAdded/OnRemoving).
- `PagurianModule.Startup()` / `PagurianModule.Shutdown()` — module lifecycle,
  symmetric with Shell.
- The demo module is **hello**, not clock: project `Pagurian.Modules.Hello`,
  the clock cell is its display, the Hello World panel is its billboard.
- `ShellAttribute` (was ShellKindAttribute). There is no separate ShellKind
  record — the attribute instance plus its annotated type IS the kind.

## 2. Solution structure (5 projects, one sln, x64)

| Project | Type | Responsibility |
|---|---|---|
| `Pagurian.Sdk` | class lib | All contracts (section 3); `TextMeasurement`; module asset resolver; `Logger`. Carries the `Microsoft.UI.Reactor` PackageReference (transitively flows to plugins). `[InternalsVisibleTo("Pagurian")]` hides infrastructure members from modules. |
| `Pagurian` (host) | WinExe | Tray window container, taskbar injection/anchoring/blend sampling, theme derivation and broadcast, hover/click hit-test dispatch, Billboard placement, TooltipWindow, module loader, config file, `post` command + message pipe, unified Logger, tray icon and quit sequence. **Contains no concrete shell/cell logic.** |
| `Pagurian.Modules.Hello` | class lib | Demo module: clock cell (two lines + 1s timer) + Hello World billboard. |
| `Pagurian.Modules.Metrics` | class lib | `SystemMetricsTracker` + `SystemMetricsColors` + CPU/MEM shells + their billboards. |
| `Pagurian.Modules.Copilot` | class lib | Hook JSON install/uninstall, session state machine, activator shell + dynamic SessionCells, session billboard, tooltip, hook-events.log. |

Module projects copy their dll (+pdb+assets) into the host output `modules\`
folder via a post-build target, so F5/`dotnet run` just works.

TFM for all projects: `net10.0-windows10.0.22621.0`, `UseWinUI=true`,
same as today. Plugins and the host must share the exact same WinUI/Reactor
types, which is why loading uses the Default `AssemblyLoadContext`
(`Assembly.LoadFrom`) and the same Reactor version. Third-party plugins only
need a reference to `Pagurian.Sdk` and can be developed outside this sln.

## 3. Sdk contract (complete public surface facing modules)

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PagurianModuleAttribute : Attribute
{
    public string DisplayName { get; init; } = "";
}

public abstract class PagurianModule
{
    public string Id { get; }                  // GetType().FullName, cached in base ctor
    public string DisplayName { get; }         // from the attribute
    public Logger Log { get; internal set; }   // injected by host
    public virtual void Startup() { }          // light setup only (heavy resources go in Shell.Startup)
    public virtual void Shutdown() { }         // final cleanup (copilot uninstalls the hook file)
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ShellAttribute : Attribute        // the attribute instance IS the kind
{
    public string DisplayName { get; init; } = "";
    internal Type ShellType { get; set; }      // backfilled by the host at discovery
    public virtual Shell CreateShell() => (Shell)Activator.CreateInstance(ShellType)!;
    // Default requires a public parameterless ctor; modules may subclass this
    // attribute and override CreateShell for custom creation (why it is not sealed).
}

public abstract class Shell
{
    public string InstanceId { get; internal set; }      // the 4-digit id from config
    public JsonElement? Settings { get; internal set; }  // config "settings" passed through verbatim
    public IThemeService Theme { get; internal set; }
    public Logger Log { get; internal set; }

    internal IReadOnlyList<ShellCell> Cells { get; }
    protected T AddCell<T>(T cell) where T : ShellCell;  // binds cell.Owner; triggers full tray re-render
    protected void RemoveCell(ShellCell cell);           // also closes its billboard
    public virtual void Startup() { }                    // heavy resources come online
    public virtual void Shutdown() { }                   // also removes all its cells
    public virtual void OnMessage(ShellMessage message) { }
}

public abstract class ShellCell : Component
{
    public Shell Owner { get; internal set; }
    protected IThemeService Theme => Owner.Theme;
    protected new Logger Log => Owner.Log;

    public virtual string? TooltipText => null;              // read at hover-dwell time; may be dynamic
    public virtual void OnClicked() => ToggleBillboard();    // default: toggle own billboard
    protected virtual Billboard? CreateBillboard() => null;  // the per-cell factory; null = no billboard
    protected void ShowBillboard();
    protected void HideBillboard();
    protected void ToggleBillboard();
}

public abstract class Billboard : Component
{
    public ShellCell Owner { get; internal set; }        // bound by host when shown
    public Shell Shell => Owner.Owner;
    protected new Logger Log => Shell.Log;

    public abstract double WidthDip { get; }
    public abstract double HeightDip { get; }
    public virtual void OnOpened() { }                   // billboard self-reports (e.g. metrics -> 1s sampling)
    public virtual void OnClosed() { }
}

public interface IThemeService
{
    bool IsDark { get; }            // derived from sampled taskbar luminance, always matches the taskbar
    Brush TextBrush { get; }        // shared live brush, mutated in place on theme flips
    event Action? Changed;          // theme-flip broadcast (UI thread); modules subscribe in UseEffect
}

public sealed class Logger                    // thin wrapper over the unified Pagurian log
{
    public void Info(string message);
    public void Warn(string message);
    public void Error(string message, Exception? ex = null);
}

public sealed record ShellMessage(
    string Command, IReadOnlyList<string> Args, string? Payload, DateTimeOffset ReceivedAt);
```

Notes:

- `ShellContext`, `IModuleHost`, `IBillboardService`, and `VisibilityChanged`
  from earlier drafts are all GONE from the module-facing surface. Their
  functionality is inlined into the object graph (Owner chains, member
  functions) or into internal host channels.
- Ownership chain is fully navigable: Billboard -> Owner (ShellCell) ->
  Owner (Shell). A metrics billboard's `OnOpened` can call
  `((CpuShell)Shell).Tracker.SetPopupVisible(...)`, or the billboard simply
  captures the tracker from the cell in `CreateBillboard()`.
- `internal` members (InstanceId/Owner setters, the host's channel for
  invoking `CreateBillboard`, AddCell change notification) work across
  assemblies via `[InternalsVisibleTo("Pagurian")]` on the Sdk.

## 4. Loading and validation rules (ModuleLoader)

Scan `exe\modules\*.dll` and `%LOCALAPPDATA%\Pagurian\modules\*.dll`, per dll:

1. `Assembly.LoadFrom` -> `GetTypes()` (catch `ReflectionTypeLoadException`
   -> log + skip).
2. `[PagurianModule]` count: 0 -> not a module assembly, skip; 2 or more ->
   the whole dll FAILS to load (log lists all marked types); exactly 1 but
   not deriving from `PagurianModule` -> fail.
3. `[Shell]` types must derive from `Shell`. Kind ids (type FullNames) are
   unique by construction; still defensively check (nested/generic edge
   cases) -> violation fails the dll.
4. Module id (module class FullName) is deduplicated globally; conflict ->
   that dll fails.
5. One failing dll never affects the others; the host always starts
   (worst case = empty tray).

Sequence: module `Startup()` -> catalog registration
(`shell FullName -> ShellAttribute instance`) -> instantiate shells from
config.

## 5. Configuration file

`%LOCALAPPDATA%\Pagurian\config.json`:

```json
{ "tray": [
  { "shell": "Pagurian.Modules.Hello.HelloShell",      "id": "3842" },
  { "shell": "Pagurian.Modules.Metrics.CpuShell",      "id": "1077" },
  { "shell": "Pagurian.Modules.Metrics.MemShell",      "id": "9051" },
  { "shell": "Pagurian.Modules.Copilot.CopilotShell",  "id": "6200" }
] }
```

- `shell` = Shell class FullName (globally unique, so no `module` field).
- `id` = 4-digit number, globally unique. **Duplicate ids at startup: log an
  error and ignore the duplicates.** Kind not found -> log + skip.
- Array order = tray order. The same kind configured twice = two instances
  (reserved for future "same kind, different settings" shells).
- `settings` (optional JSON object) is passed verbatim to `Shell.Settings`
  (`JsonElement?`); the host never parses it. Purely reserved — current
  modules do not consume it.
- The host does NOT auto-add discovered shells to the config. A missing file
  is created as `{ "tray": [] }`. During development, hand-write/fake a
  config like the one above.

## 6. `post` command and the message channel (replaces `--hook`)

- Invocation: `Pagurian.exe post {shell_id} <cmd> [args...]`; stdin is read
  as the payload when piped.
- Program.cs's first branch intercepts `post`: packs a single-line structured
  envelope `{id, command, args, payload, receivedAt}`, writes it to the named
  pipe `Pagurian.ShellMessages`, and **always exits 0** (fail-closed, same as
  the old bridge). `CopilotHookBridge.cs` is deleted; its retry/tolerance
  logic moves into this generic bridge.
- The host's `ShellMessageServer` (background thread) ->
  `UIDispatcher.TryEnqueue` -> routes `Shell.OnMessage` by id. Unknown id /
  empty command -> drop + log.
- Fire-and-forget, no response read-back. A future synchronous reply can be
  added on the pipe without breaking the protocol.
- Copilot convention: `Command = "hook"`, `Args = [<event>]`,
  `Payload` = the CLI's stdin JSON.

## 7. Host internals

- **`TrayShells`**: ordered shell list (from config). `AddCell/RemoveCell`
  call back into the host through the internal channel -> the tray window
  **re-renders as a whole** (no fine-grained diff). Cell content changes are
  driven by each cell's own `UseState`.
- **`TaskbarTrayWindow`**: Render iterates `shells.SelectMany(s => s.Cells)`,
  wrapping each cell in host chrome (hover overlay live brush, rounded
  margin, width read-back `Ref`, `WithKey`). All clock/metrics/session
  special-casing is deleted. Kept: blend gradient brush, ContentScale, hover
  brush cache. A 0-cell shell occupies no space.
- **`TaskbarTrayLayout`**: cells come from the flattened sequence; the
  ActualWidth read-back mechanism is unchanged.
- **`TaskbarController`**: injection/anchoring/blend sampling kept as-is.
  Dispatch generalized — hit-testing is per cell (host private handle);
  click -> `cell.OnClicked()`; hover dwell -> non-null `TooltipText` shows
  the host's TooltipWindow. Billboard management is unified (the current
  three parallel popup code paths — hello/session/metrics, ~150 lines —
  collapse into one): placed above the owner cell (below when the taskbar is
  at the screen top), physical-pixel rect hit-testing, outside-click
  dismissal, one billboard at a time, auto-close when the owner cell is
  removed (preserves today's sessionEnd-closes-popup behavior). Every Show
  creates a fresh billboard instance via `CreateBillboard()` (decision #5).
- **Theme**: light/dark derived from sampled luminance (Rec.601, threshold
  140, never registry/UISettings). On flip: `TextBrush` mutated in place +
  `IThemeService.Changed` broadcast (collapses today's two `NotifyChanged()`
  calls). Hover overlay colors stay host-internal.
- **Logger**: `TaskbarDiagnostics` generalized into a unified `PagurianLog`
  (single file, timestamp + tag); all host components and every module share
  it through the `Logger` facade.
- **Quit**: tray Quit menu -> each shell `Shutdown()` -> each module
  `Shutdown()` -> existing close chain unchanged (tray.Close ->
  ReactorApp.Exit(0) -> Environment.Exit(0)).

## 8. Module migration map

**Hello**: clock cell rendering + 1s timer + `NowTime/NowDate` move out of
`TaskbarTrayWindow`; `HoverPopupWindow` becomes `HelloBillboard`;
`HelloShell.Startup()` attaches one clock cell; clicking uses the default
`OnClicked` (`ToggleBillboard`).

**Metrics**: `SystemMetricsTracker` (567 lines incl. P/Invoke),
`SystemMetricsColors`, both popup windows move in as `CpuBillboard` /
`MemBillboard`. `MetricsModule` holds a static `Instance` so both shells can
grab the shared tracker at construction. Tracker Start/Stop hang off
`Shell.Startup/Shutdown` with a refcount (cpu+mem both removed -> Stop).
Sampling cadence switches via billboard `OnOpened/OnClosed` self-reporting
`SetPopupVisible` (replaces `VisibilityChanged`). Gauge colors re-render via
`Theme.Changed` self-subscription.

**Copilot**: `CopilotHookInstaller` stays but the hook command becomes
`post {id} hook <event>` (UTF-8 no BOM, points at the exe not dotnet, all 14
camelCase events unchanged). `CopilotSessionTracker` loses its pipe server
and is fed by `CopilotShell.OnMessage` instead (state machine, status
debounce, workspace.yaml name resolution unchanged). `SessionPopupWindow`
becomes `SessionBillboard`. `CopilotShell` has 0 cells normally;
`sessionStart` -> `AddCell(new SessionCell(session))`, `sessionEnd` ->
`RemoveCell`. SessionCell carries per-session state (GitHub icon + status
text, tooltip = session name, click -> billboard). Status colors re-render
via `Theme.Changed`. The GitHub icon moves into the module's assets (Sdk
asset helper resolves relative to the module assembly location).
`CopilotHookBridge.cs` is deleted.

**Stays in host**: `TaskbarInterop`, `TaskbarDiagnostics` (generalized),
`TooltipWindow`, `AppAssets` (tray icon only), all injection/anchoring/
sampling logic.

## 9. Implementation order

1. Spike: verify Reactor cross-assembly child-component rendering and
   `Assembly.LoadFrom` + attribute reflection work in an unpackaged WinUI
   app.
2. `Pagurian.Sdk` (all of section 3 + `TextMeasurement` + asset helper +
   `Logger`).
3. Host: `TrayShells` / config / `post` pipe / unified Logger; rendering
   switched to the Shell->ShellCell two-level drive (no behavior change).
4. Billboard/Tooltip/click dispatch generalized (no behavior change; the
   three popup paths collapse).
5. Extract the Hello module -> verify.
6. Extract the Metrics module -> verify.
7. Extract the Copilot module (hook JSON switches to `post`, bridge deleted)
   -> verify.
8. Module discovery/loading + post-build copy + config seeding.
9. Update `AGENTS.md` and the sln. Throughout: compile-check on macOS with
   `dotnet build Pagurian.sln -p:EnableWindowsTargeting=true` (the mt.exe
   manifest failure is expected and unrelated to code correctness); real
   verification happens on Windows.

## 10. Risks and accepted trade-offs

- Cross-assembly Reactor component rendering (spike first).
- The copilot hook id depends on config persistence; deleting the config
  entry makes hook messages drop with a log line — safe degradation.
- Session cells appear at their parent shell's config slot (not appended
  globally); putting copilot last in config preserves today's visual order.
- After the theme-flip broadcast, each module subscribes itself; a module
  that forgets only fails to recolor itself (fault isolation).
- `ShellAttribute.CreateShell()` defaults to a public parameterless ctor;
  dependency injection goes through the module singleton; complex creation
  subclasses the attribute and overrides `CreateShell()`.

## 11. Closed questions

- Billboard instance policy (#5): every Show creates a fresh instance via
  `CreateBillboard()`; subscriptions follow the instance lifetime.
