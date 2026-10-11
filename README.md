# Pagurian

Pagurian is a WinUI taskbar utility with independently loaded shell modules.

Modules are deployed as self-contained folders and compile against the
versioned `Pagurian.Sdk` package. See
[External modules](docs/External-Modules.md) for the SDK, project, bundle,
dependency-isolation, and installation contract.

## Shell configuration transitions

The Shell editor keeps its Modules catalog fixed beneath configuration pages.
Each Shell configuration slides in from the right as an independent opaque
overlay and exits to the right. Selecting another Shell while an overlay is
still entering reverses that overlay from its current position immediately,
so rapid selections do not queue stale pages or make an old configuration
flash back later. Windows Reduce Motion disables these transitions.

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
midpoint-away-from-zero. The same integer drives the card and pulse tint.
The Usage configuration also exposes Copilot account details in a collapsible
Account section, collapsed by default. When the shared CLI login is available,
its header keeps **Account** on the left, shows the signed-in account on the
right, and omits the Login button.
The expanded details contain only the status, host, authentication type, and
the login action when authentication is unavailable; the signed-in login is
not repeated in a nested card.
opaque blue at rounded zero (the same blue in Light and Dark), red behind,
green ahead; only color saturates at six days. Nonzero colors still blend from
the theme's neutral color, never from blue; unavailable remains neutral.
Settings and Billboard balance sentences share a leading tinted pulse icon.
The Settings tooltip previews all 13 colors from +6 through zero to -6, followed
by Surplus, On track, and Over budget explanations. High Contrast retains system
colors rather than the ordinary palette.
At reset the comparison stays unavailable until a fresh-cycle quota arrives.
The top taskbar gauge and bottom percentage still mean Credits used.

## Copilot Usage history

The Usage billboard shows the signed-in username alone at the far right of
its title row, rather than in a separate Account card. No username placeholder
appears before the account is known.

Each successful authenticated premium-interactions quota read records that
local day's first and latest used-request counts, together with its reset cycle
and entitlement.
The versioned JSON history is kept per GitHub host/account under
`%LOCALAPPDATA%\Pagurian\Copilot\usage-history-<account-hash>.json`, independently
of `config.json`. Older samples stay in the file; the billboard's final
**History** card shows the last 60 days across three 20-day bar-chart pages,
opening on the most recent page with today on the right. A WinUI PipsPager
provides the arrows and three page dots centered on the same compact row as
the History heading, without reserving extra height above the chart. It uses
the first sample of the next consecutive day to calculate a completed day's
usage, and today's latest sample (or the last sample before a missing day)
otherwise. Missing workdays between two observations in the *same* reset
cycle split their unobserved difference evenly; yellow `?` bars and hover
text identify these as estimates. Missing rest days, unknown spans and
cross-cycle gaps remain without data, shown as short neutral placeholders
instead of green zero-use bars. Workday overrides in each shell are
applied to these estimates when rendering, without modifying the samples.

Observed bars stay green through 5% of that cycle's monthly entitlement,
then blend to red at 10% (and saturate above it); bar height also saturates
at 10%. A cycle without a known entitlement uses 75,000 Credits for the chart.
Each bar has a 14-DIP, single-color vector weekday symbol beneath it:
Sun, Moon, Mars, Mercury, Jupiter, Venus and Saturn from Sunday to Saturday.
The symbols use a shared 24-unit grid with 2-unit round strokes, not emoji
or font glyphs. Their foreground follows the billboard theme and uses the
system text brush in High Contrast; accessible names retain the weekday and
celestial body. A short tick points to
month-and-day labels (for example, `Sep 21`) on the 1st, 11th and 21st.
On the newest page a two-line `Today` / month-and-day label occupies the
right side of the History heading, inset from the card's top edge while the
heading stays in place. The chart sits lower so its longer arrow still points
to the chart top, independent of the day's bar height.
The centered PipsPager sits beside the arrow above the chart without moving
the History heading or the chart.
Neither enters a day's hover
rectangle.
The full-height column, its weekday symbol and date label share a hover
target with an immediate softly rounded outline covering the plot and weekday symbol,
but not the date label below. Its tooltip opens above the column
without a dwell delay and gives the full date, weekday, observed or estimated use
(including zero), and total used Credits at the day's first sample. For estimated
workdays the starting total follows the same gap allocation and is marked
estimated; days with no baseline show Unavailable. On a local day spanning a reset,
the starting total belongs to the earliest cycle, not the sum of both counters.
There is no separate color legend beneath the chart.
History file errors and the no-history message appear below the chart at the
bottom of the card. File errors are also logged in `pagurian.log`; live Credits
queries continue to work.

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
