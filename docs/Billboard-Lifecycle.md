# Billboard preparation and dismissal

Each opening creates a new module `Billboard` component and a new window.
`BillboardSession` owns that opening and its callbacks. The taskbar controller
owns only the current session, owner lookup, and click dispatch.

## Opening

1. Create without auto-activation or SizeToContent using the shared
   window spec from `BillboardSession.CreateSpec`; leave the initial position
   at its default until measured coordinates are available. Reactor validates
   the creation spec before the configure callback, so selecting `Manual`
   without `ManualPosition` here prevents every billboard from opening.
   Install a stable host
   `Border` through Reactor's pre-mount `ContentTarget` hook and set its theme
   from the taskbar before mounting module content. High contrast uses the
   Windows theme. Move to the owner monitor before computing DIP constraints.
2. After a successful render, measure the mounted tree at the requested width,
   capped to the monitor work area, with unconstrained height. Clamp its natural
   height to the work area, then measure and arrange the final viewport. This
   prevents flexible layouts from expanding to fill the monitor. `HeightDip` is
   the fallback for an unusable measurement. Update both the native bounds and
   the spec's first-show position, then arrange content at the final size.
3. Allow XAML to load and render while DWM cloaks the window. Hidden layout
   alone does not create its first compositor surface. Reveal after the first
   `CompositionTarget.Rendered` notification for the loaded tree and a background
   `DwmFlush` that waits for its queued DirectX updates to be presented. Queue
   reveal back onto the UI thread. Do not wait for another dirty XAML frame:
   a static page may never produce one. Rendering listeners are temporary and
   detached on reveal or cancellation.
4. Invoke `OnOpened` after reveal. Hold the outer size for this opening; data
   updates and navigation use the module's scrolling content. Keep theme
   propagation active.

DWM's [cloak and transition attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
allow the window to be composed without being displayed and suppress native
show/hide transition snapshots. [Rendered](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.compositiontarget.rendered)
is a post-render event. [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)
is a desktop presentation fence and runs on a worker thread. A mount, layout,
or HWND size event alone is insufficient.

The two-second preparation watchdog is failure recovery, not a sizing delay.
A missing tree or owner is never revealed. Delayed callbacks check their own
session state so cancellation cannot reopen an old window.

## Dismissal

All host dismissal paths, including switching cells and shutdown, retire the
session, detach callbacks, **hide the intact native window**, and then close it.
Content, acrylic, colors, and dimensions are not reset before hiding. DWM
transitions are disabled for the popup so a teardown snapshot cannot linger as
an empty or white frame. `OnClosed` is delivered once for an opened session;
pre-reveal cancellation delivers neither module lifecycle hook, but mounted
Reactor effects are still disposed.

No SDK signatures or dependency versions change. Compass now derives its
520-DIP page height directly from the host's monitor limit, without writing
transient XamlRoot height back into state.

## Verification

```powershell
dotnet build Pagurian.sln -p:Platform=x64
.\tests\Verify-BillboardLifecycle.ps1
.\tests\Verify-ModuleIsolation.ps1
```

The lifecycle fixture links the production session, reuses the taskbar's
creation-spec factory, validates that spec, and uses isolated windows.
It checks content fitting, overflow scrolling, live theme changes, cancellation
before and during cloaked rendering, missing content timeout, rapid switching,
hidden teardown, and real Hello/CPU/Memory components with seeded metric data.
Placement arithmetic covers 100%, 150%, and 200% scales, negative coordinates,
work-area clamping, and top/bottom anchors.

With a built Compass module, include its real views and list/detail navigation:

```powershell
.\tests\Verify-BillboardLifecycle.ps1 -CompassAssembly '<absolute path to Pagurian.Modules.Compass.dll>'
```

`-Capture` puts a controlled dark window behind the fixture for frame recording;
`-BuildOnly` skips UI execution. No fixture starts the tray, loads user settings,
contacts Compass, or writes the user log. For visual acceptance, record repeated
open/close/switch operations and inspect opening and dismissal frames. Repeat
on physical mixed-DPI monitors and in high contrast; arithmetic checks cannot
establish those compositor behaviors.
