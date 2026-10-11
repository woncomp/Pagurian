# Billboard Focus Activation — Design

Status: **proposed** (2026-09-19). This document records a deliberate behavior
change to billboard chrome and the investigation that justifies it. It is not
an implementation plan; a later session expands it into one.

## Summary

Billboards lose their `NoActivate` extended style and become activatable
windows that take keyboard focus when revealed. This restores their acrylic
material on Windows 11 25H2, where SystemBackdrop rendering has become
focus-dependent. Tooltips keep `NoActivate` and remain solid-colored until the
OS regression is fixed — a hover-appearing window must never steal focus.

## Background

Billboard chrome is host-owned and has been unchanged since the prototype:
borderless, rounded corners, thin acrylic (`BackdropKind.AcrylicThin`, i.e.
WinUI's `DesktopAcrylicBackdrop`), `NoActivate`, always-on-top, dismissed by
the controller's cursor polling. Tooltips share the same material choice. The
metrics design (`System-Metrics-Widgets-Design.md`) explicitly states popups
are non-activating, and the Copilot session presentation convention records
"never globally activate billboards" (its clipboard copy acts on the clicked
HWND for that reason). This design revisits both statements for billboards.

## The regression

- Through 2026-09-06 billboards visibly rendered acrylic (the "refresh
  billboards with Fluent acrylic design" commit redesigned content *for*
  acrylic; it was demonstrably working).
- On 2026-09-13 cumulative updates KB5124007 + KB5126052 were installed
  (build 26200.9445, the build still running at investigation time). On
  2026-09-19 the out-of-band KB5129195 landed, servicing the
  `WindowsAppRuntime.2_2.5.1.0` framework binaries minutes later.
- Since then, billboards and tooltips render the solid SystemBackdrop fallback
  color. Pagurian code did not change.
- Root cause: SystemBackdrop materials (`DesktopAcrylicBackdrop`,
  `MicaBackdrop`) now render only while the window holds keyboard focus. A
  `NoActivate` window can never hold focus, so it renders the fallback
  permanently. This is the known upstream issue
  [microsoft-ui-xaml#10570](https://github.com/microsoft/microsoft-ui-xaml/issues/10570),
  closed without a documented fix. A side effect of the same regression: any
  activatable SystemBackdrop window (e.g. Settings' Mica) also falls back to
  solid while unfocused.

## Evidence

Diagnostics used `tests\BackdropProbe`, a standalone probe app opening labeled
windows under a matrix of configurations on the affected machine:

- A plain `DesktopAcrylicBackdrop` window renders **solid while unfocused** and
  gains acrylic **when focused** — focus is the trigger, not window flags.
- A `NoActivate` billboard-equivalent window stays solid in every variant,
  including forcing `SetNoActivate(false)` + `Activate()` after creation (the
  compositor's decision is fixed at creation).
- The undocumented Accent API (`SetWindowCompositionAttribute` with
  `ACCENT_ENABLE_ACRYLICBLURBEHIND`, the TranslucentTB technique) is accepted
  by the HWND but never visible: a 25%-alpha black tint still rendered pure
  white. WinUI 3 owns the window surface with an opaque swapchain; DWM's accent
  layer sits behind it and cannot show through.
- Environmental causes were excluded on the machine: transparency effects on,
  high contrast off, console (non-RDP) session, balanced power, clean DWM
  registry, no third-party translucency tools, GPU driver predating the
  regression window.

Why system surfaces still show acrylic: the Notification Center/Calendar
flyout is a UWP `Windows.UI.Core.CoreWindow` using element-level
`Windows.UI.Composition` backdrop brushes — not SystemBackdrop — and it is
*not* `NoActivate` (it genuinely takes foreground activation when opened).

## Alternatives considered and ruled out

1. **Accent API** (`SetWindowCompositionAttribute`) — proven invisible on
   WinUI 3 windows by the probe above.
2. **`AcrylicBrush.BackgroundSource = Backdrop`** — the UWP property does not
   exist in WinUI 3; desktop-sampling acrylic was moved to SystemBackdrop.
3. **Element-level `CompositionBackdropBrush` + Win2D `GaussianBlurEffect`**
   (the Notification Center's approach) — available in WinUI 3, but requires a
   new dependency and composition plumbing; deferred by owner decision after
   the Accent attempt failed.
4. **Waiting for Microsoft** — the upstream issue is closed without a fix;
   nothing committed to wait for.
5. **Accept solid backgrounds** — the status quo; this document exists because
   the focus-activation alternative is being considered instead.

## Proposed change

- Billboards are created **with** `NoActivate` (so the cloaked preparation
  `Show()` cannot steal focus — without the flag, showing a new top-level
  window self-activates it), then at **reveal** the flag is dropped and the
  window **activated** — after content has been measured, painted, and
  uncloaked, never during cloaked preparation.
- Acrylic then renders for the billboard's entire visible lifetime: on outside
  click the existing light-dismiss polling closes the window within one
  controller tick (~50 ms), so focus loss shows at most one solid fallback
  frame before the window is gone.
- `ShowInTaskbar = false` / `ShowInSwitcher = false` remain, so the billboard
  never pollutes the taskbar or Alt-Tab.
- Tooltips are unchanged: `NoActivate` forever, solid material until the OS
  regression is fixed.
- Tray windows are unchanged: they are `SetParent` children of the taskbar and
  use the Transparent backdrop; nothing about them is in scope.

## Why this is safe

- **The input-queue hazard does not apply.** The historical "wedged taskbar"
  risk comes from implicit `AttachThreadInput` when a window is `SetParent`'d
  across processes (the tray windows into `Shell_TrayWnd`). The billboard is a
  standalone top-level window with no parent or owner; it shares no input queue
  with Explorer. Activation is an ordinary foreground change.
- **Precedent:** the system's own taskbar flyout (Notification Center) takes
  foreground focus when opened from the taskbar; users accept click-opened
  panels taking focus.
- **Activation rights:** the open is a direct response to a real click on
  Pagurian's own tray window, so the foreground-activation lock permits it.
- **Revert path:** the change is one spec flag plus activation at reveal; it
  can be reverted or made configurable without structural work.

## Behavior changes to accept (UX deltas)

- Opening a billboard moves keyboard focus away from the current app: the text
  cursor there stops blinking, other apps' deactivate behaviors may fire, and
  exclusive-fullscreen games may mode-switch. Closing the billboard returns
  foreground to the previous window (to be verified in implementation).
- The billboard receives keyboard input while open. Today billboard content has
  no keyboard handling; the implementation plan defines Esc-to-close.
- The Copilot billboard's clipboard copy currently acts on the clicked HWND
  precisely because billboards never activated; that interaction must be
  re-validated (or simplified) under activation.
- Focus loss reverts acrylic to solid for the frame(s) before dismissal polling
  closes the window; bounded to roughly one controller tick.
- High-contrast behavior is unchanged: HC content already renders opaque by
  design, independent of the window material.

## Open questions for the implementation plan

- Esc-to-close and the exact focus-restoration order on dismiss.
- Activation timing relative to reveal (activate *is* the reveal, or a separate
  step after uncloak), and behavior when the opening click's owner cell moves
  mid-preparation.
- Whether dismissal stays purely cursor-polling or also reacts to focus loss
  (e.g. `WM_ACTIVATE` / Reactor's `Activated`-family events) — retain polling
  unless evidence says otherwise.
- Clicking a cell on another display while a billboard is open elsewhere
  (focus transitions are global).
- Whether the change is unconditional or behind a host setting for users who
  prefer never-stealing focus over acrylic.
- Whether Settings' unfocused-Mica fallback (same OS regression) is addressed
  here or left alone.

## References

- Probe: `tests\BackdropProbe\` (standalone, not in the solution).
- Upstream issue:
  [microsoft-ui-xaml#10570](https://github.com/microsoft/microsoft-ui-xaml/issues/10570).
- Related docs: `docs\Billboard-Lifecycle.md` (cloak/reveal lifecycle that
  activation must join), `docs\Tray-Lifecycle.md` (the SetParent input-queue
  hazard this design avoids), `docs\roadmap\System-Metrics-Widgets-Design.md`
  (whose non-activating-popup statement this design supersedes for billboards).
- Regression window evidence: KB5124007 / KB5126052 (2026-09-13, build
  26200.9445), KB5129195 (2026-09-19, build 26200.9457) servicing
  `WindowsAppRuntime.2_2.5.1.0`.
