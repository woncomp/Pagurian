# Isolated Copilot Usage presentation fixture

From the repository root:

```powershell
tests\Verify-CopilotUsagePresentation.ps1 -Platform x64
```

Use `mur check <project> -- -p:Platform=x64` instead when `mur` is installed.
The console must reach `PASS`, not just exit zero.

Like the existing presentation fixture, this mounts production components in
isolated windows. It links module source files (excluding `UsageShell`, whose
runtime startup is deliberately not exercised) and the production
`BillboardSession`. No SDK/module/shell startup, auth, hook installation,
user configuration writes, or main Pagurian process are involved.

The fake source supplies September 2026 in China. The in-memory host-style
draft supplies `ShellConfigurationProps` and exercises native UIA toggles,
Save, Revert, discard/reopen, per-shell independence, and disposal.
Other probes cover raw/endpoint marker coordinates, label clamping,
long/scaled labels, Sunday-first padding, measured equal calendar columns,
stable content width on toggles/parent growth/month labels, two reserved text
rows and temporal states (including disabled dates), shared draft tint/saturation,
estimated cycles, zero workdays, day/reset observation, and the Metrics
12-DIP-label baseline in a 48-DIP cell.

Screenshots are written to `artifacts/copilot-usage-ui`. Normal views use
`RenderTargetBitmap`; the isolated billboard uses a screen read of only its
own HWND bounds because acrylic is not captured faithfully by that API.
Rich balance and disabled-date tooltips are opened in controlled native
`ToolTip` wrappers with the production content, not simulated with a second
window. Tests check the whole-row hit area, accessible help, legend colors,
effective Settings theme changes, and open-tooltip disposal on unmount.
Tooltip PNGs capture the rendered content on a transparent background.
The calendar is stressed at 150%, 200%, and 250% local font size; horizontal
overflow remains scrollable rather than squeezing the measured columns.

On locked/restricted desktops a uniform-black screen read is reported as an
unavailable compositor capture and no misleading billboard PNG is retained.

Limitations: UIA toggles exercise the native keyboard-equivalent pattern, not
physical key injection or physical pointer hover. High Contrast tests exercise the system-color branch
without changing OS/user settings. Text-size stress is local to fixture controls;
only the current display's actual rasterization scale is measured. The in-memory
host draft tests do not exercise the real host's confirmation dialog or disk IO.
