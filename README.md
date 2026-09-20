# Pagurian

Pagurian is a WinUI taskbar utility with independently loaded shell modules.

Modules are deployed as self-contained folders and compile against the
versioned `Pagurian.Sdk` package. See
[External modules](docs/External-Modules.md) for the SDK, project, bundle,
dependency-isolation, and installation contract.

## Copilot Usage workday calendar

Each Copilot Usage shell has its own compact, Sunday-first calendar in Settings,
with Monday–Friday selected as workdays by default. Dates keep their month/day
labels; past dates show a check and today's date shows **Today**. Toggle a date
to change whether it is a workday; hover for its full date and state.
Statistics and the shared tinted balance sentence preview the draft immediately;
the existing host **Save** applies it to the taskbar/billboard. **Revert** and
confirmed close/discard keep their usual draft semantics. Only concrete-date
deviations are stored under `settings.workdayOverrides`, for example
`{"2026-09-10":false,"2026-09-12":true}`; unrelated settings are preserved.

The cycle is inferred as one calendar month before the Credits reset instant,
subtracted before local time-zone conversion. A date belongs when its last
local-day instant is within that cycle (reset excluded). Today counts in full;
rest days retain the cumulative count. Missing/expired server resets use the
next UTC month's first day and are explicitly **estimated**.

The Used Percentage card compares elapsed workdays with Credits consumption.
Balance is elapsed days minus used fraction times total working days, rounded
midpoint-away-from-zero. The same integer drives the card and pulse tint:
neutral at zero, red behind, green ahead; only color saturates at six days.
At reset the comparison stays unavailable until a fresh-cycle quota arrives.
The top taskbar gauge and bottom percentage still mean Credits used.

Regression checks: `tests\Verify-CopilotSessions.ps1 -Platform x64` and the
fake-source, in-memory UI harness
`tests\Verify-CopilotUsagePresentation.ps1 -Platform x64`. See its
[fixture notes](tests/Fixtures/CopilotUsagePresentation/README.md) for visual
coverage and desktop limitations.

## Publishing

Pagurian builds are framework-dependent. Run `build-publish.bat` from the
repository root to create the x64 release in `publish`. Building and running
Pagurian requires these x64 components on the machine:

- .NET 10 Runtime
- Microsoft Visual C++ Redistributable
- Windows App Runtime 2.1.3 or a compatible newer 2.x servicing release

Pagurian does not bundle or install these prerequisites. See Microsoft's
[deployment guide for unpackaged framework-dependent apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
for Windows App Runtime installation options.
