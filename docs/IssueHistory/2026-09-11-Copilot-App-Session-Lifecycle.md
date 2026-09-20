# Copilot App session lifecycle separation — 2026-09-11

## Issue and scope

An App session icon disappeared as soon as the agent finished a turn. This was
not an archive action or an App process exit. The reducer treated root
`sessionEnd` as permanent end, removed the cell/billboard, and rejected further
activity until another explicit `sessionStart`. App turns did not satisfy
that assumption.

The correction applies only to positively identified `github/autopilot` roots
and confirmed App task descendants. Independent App `create_session` roots,
CLI/VS Code/Other ownership, nested task relationships and per-source
permission precedence remain separate. No user App process was restarted,
archived or terminated to implement or validate this change. No user session,
database, hook or credential file was modified.

## Observed evidence, not inferred lifecycle

The supplied investigation recorded these sanitized observations. Local times
below are **2026-09-11, UTC+08:00**; the quoted database timestamp is UTC.

| Time | Observation |
| --- | --- |
| 18:29:30.500 | A target root's `sessionEnd(reason=complete)` immediately removed its icon, before actual archive. |
| Around 18:32–18:33 | The target transcript contained routine `session.shutdown`, then resumed without archive. |
| 18:38:39.103 | Root `961b0335-14d0-4af2-9718-9a8db436fc98` emitted `agentStop`. |
| 18:38:39.527 | The same root emitted `sessionEnd(reason=complete)` after normal turn completion. |
| Around 18:41 | The user quit the App. No matching new terminal hook or transcript `session.shutdown` was found. The exact old process exit instant was not established. |
| 18:42:00 | New App `github.exe`, PID 86608, started. |
| 18:42:05 | Its SDK child `copilot.exe`, PID 99384, started. |
| 18:42:09.737 | The current root resumed and had `inuse.99384.lock`. |
| 18:42:55.382 | Root `2b314735-e753-4f3d-a956-1507cec07d45` logged runtime cleanup: “Session closed after last owner detached”. |
| 18:42:56.662 | Mapped workspace `06e9e07e-6271-40e3-9004-072b1e39cbf8` had `workspaces.archived_at=2026-09-11T10:42:56.662Z`. Its `sessions.archived_at` was still null. No Archive hook was observed. |

The SDK process hosted both roots. Its continued existence did not prove that
both remained unarchived.

**Supported conclusions:** normal turn completion cannot be treated as root
archive; archive and process exit cannot rely on receiving hooks; mapped
workspace archive state takes precedence over the session row; SDK detach and
transcript shutdown do not establish durable archive.

**Not established by the evidence:** the exact old App exit timestamp, universal
hook guarantees for other App versions, or a stable public meaning for all
internal schema/lock fields. Missing events were observations, not proof that
the App can never emit such events.

## Earlier flawed assumptions

- One `Ended` flag represented task completion, persistent root visibility and
  the current App run.
- Every root `sessionEnd` was considered terminal, even `reason=complete`.
  The explicit-root-start fence prevented stale resurrection, but also blocked
  perfectly valid next-turn prompts that had no new `sessionStart`.
- `sessions.archived_at` alone could describe project archive. The observed
  mapped workspace contradicted this.
- SDK liveness, a session's runtime cleanup, or transcript `session.shutdown`
  could stand in for archive. A shared SDK and ordinary detach/resume
  contradicted those assumptions.
- Recent metadata discovery was enough for restart attachment. It could miss
  actually loaded older conversations; restoring every unarchived DB row would
  instead create unrelated historical icons.
- A new App run could share the same generation as conversation details, either
  resurrecting old blockers or discarding historical usage during a reset.

## Implemented architecture

### Source work, persistent visibility, process generation

`Pagurian.Modules.Copilot\CopilotSessionState.cs` now keeps App `Hidden`,
`VisibilityFence`, `App`, `Restored` and `WorkEpoch` independently of the
existing conversation/details epoch and per-source state.

- `CopilotHookEvent.Parse` retains `reason`. App root completion reduces only
  that source to Idle. Unknown/other reasons also wait for authoritative
  visibility evidence, rather than guessing archive. Deferred client identity
  receives the same policy after attachment.
- Work remains source-owned: blockers outrank Working/Idle, and parent/sibling
  completion cannot clear another source. Task terminal/start fences remain
  local to the task.
- `ReconcileAppLifecycle` consumes monotonic snapshot versions. Authoritative
  archive hides one root; verified exit hides only roots bound to that App
  instance. Old identity/lifecycle snapshots and hooks cannot reopen a hidden
  root.
- Unarchive requires known Live **and a new positive load timestamp** beyond
  the removal fence. A stale lock, a new hook alone, or a historical live DB
  row cannot reopen the cell.
- A newly verified App instance resets old work, permissions and debounce
  candidates. Fresh current-instance hooks win over initial Idle, including
  already committed status and newly attributed blocker timestamps.
- Hooks arriving while hidden retain tentative reduced per-source state after
  the removal fence, without publishing a cell. Restoration admits only state
  meeting the new App/load fence. Blockers remain independent of latest-hook
  details, and fresh task lifecycle records awaiting identity are not consumed
  by the stale-runtime reset.
- A hook-visible root binds verified loaded-process ownership even when archive
  state is Unknown. A later confirmed Exit therefore still hides it during a
  database outage. Ownership attachment does not authorize restoration, and
  comparison against the previous App instance still triggers runtime reset.
- Conversation nodes, hook summaries and attributable usage survive an App
  process change. Old tasks do not become active. App root transcript shutdown
  no longer renders the visible root as terminal in its tree.

### Read-only local evidence

`CopilotAppLifecycle.cs` defines versioned lifecycle evidence and injectable
archive/process/reader boundaries. `CopilotAppLifecycleReader.cs` takes an
isolated state-directory path and clock, and performs bounded directory/lock
discovery on the existing background worker: 128 known IDs plus 128 resumable
directory entries, at most 16 locks per directory per pass. Positive load
evidence bypasses the seven-day metadata cutoff. It never enumerates every
unarchived database row.

`CopilotArchiveReader.cs` uses **Microsoft.Data.Sqlite**, `Mode=ReadOnly`,
pooling disabled, one-second provider lock waits, parameterized IDs, schema
checks and a single deferred read transaction. It reads the live WAL view; it
does not use immutable mode or copy only the database's main file.

- Direct `workspaces.session_id` or `workspace_session_aliases` mappings use
  `workspaces.archived_at`.
- Only an unmapped explicitly standalone `session_type='chat'` uses
  `sessions.archived_at`.
- Missing rows, dangling/conflicting mappings, malformed values, unsupported
  schema, lock/permission/provider failures return Unknown, never invented
  Live/Archived. `is_running` is not lifecycle evidence.

`CopilotAppProcesses.cs` treats `.copilot\run\single-instance.owner` as a
bounded PID hint (integer or JSON `pid`). It checks a live, fully qualified
`github.exe` path, GitHub company metadata and process creation time, retaining
an instance handle for actual exit detection. Session `inuse.<pid>.lock`
timestamps must be at least as new as the live `copilot.exe` instance, whose
verified ancestry must reach that exact App. Ancestor creation times reject
reused PIDs. Access denial, absent hints and SDK detach do not imply exit.
Once an exit is verified, its handle is released and the identity tombstone is
retained. Credential/token files are not read.

`CopilotSessionIdentityResolver.cs` and `CopilotSessionTracker.cs` deliver
identities plus lifecycle as one worker snapshot through the existing
dispatcher-generation guard. Final Stop invalidates queued callbacks before
cancelling/disposal. Unknown evidence retries with bounded, payload-free
diagnostics; it preserves existing visibility but cannot invent new roots.

### Packaging and tests

`Pagurian.Modules.Copilot.csproj` adds Microsoft.Data.Sqlite 10.0.9 and an
explicit SQLitePCLRaw.bundle_e_sqlite3 2.1.13 override. The provider's older
transitive SQLite minimum produced a NuGet vulnerability warning during the
first build; the patched bundle removes that warning. The private dependency
allowlist now deploys Microsoft.Data.Sqlite, SQLitePCLRaw managed assemblies
and the platform-selected native `e_sqlite3.dll`, without copying shared
SDK/Reactor/WinUI assemblies.

The existing dependency-free `tests\Fixtures\CopilotSessions` fixture links
the lifecycle contract and substitutes only the production factory. Its
obsolete App terminal-root assertions were replaced, while CLI terminal
ordering tests remain. Sanitized fixture files now live under repository
`artifacts`, not the user's session tree.

The new `tests\Fixtures\CopilotAppLifecycle` fixture links the real database,
process policy, resolver and reducer. Fake process handles exercise actual
ancestry/start-time/path/exit policy; generated databases exercise real SQLite.
Optional bundle testing opens the fixture WAL database through SQLite loaded
by the production `ModuleLoadContext`, not just file-existence assertions.

## Validation performed

### Review race corrections

Review identified two reducer ordering gaps: a blanket hidden-hook discard lost
a replacement App's permission request before its positive load snapshot;
requiring known Live archive state for process ownership left hook-created
cells immune to confirmed Exit during SQLite failure.

The added hidden-delivery regression first failed at “Fresh root and child
blockers survive hidden delivery” (**44/45 scenarios**), reproducing the first
finding before the reducer correction. The focused fixture now adds **70
assertions** for root work/permissions, child work/blockers, irrelevant/stale
hooks, archive/reload and Exit/restart fences, task start/stop/resume, child
identity before/with/after restoration, and Unknown-archive process ownership.
The ownership sequence explicitly checks Unknown loaded evidence → confirmed
Exit → Live but unloaded recovery, as well as Unknown replacement-generation
reset and rejection of unknown historical restoration.

Latest corrective runs passed with explicit x64 selection:

| Command/check | Result |
| --- | --- |
| `tests\Verify-CopilotSessions.ps1 -Platform x64` | **45/45 scenarios**, including **144 App lifecycle assertions**, **667 usage assertions**, and existing details/presentation-state checks. |
| `tests\Verify-CopilotAppLifecycle.ps1 -Platform x64` | **41 SQLite/process/resolver assertions** plus the shared **144 App lifecycle assertions**. Optional deployed-bundle checks were not repeated in this corrective run. |
| `dotnet build Pagurian.sln -p:Platform=x64 --nologo -v:minimal` | Full solution passed with **zero warnings and zero errors**, including Reactor analyzers. `mur` was unavailable, so the documented `dotnet build` fallback was used. |
| `git diff --check` | No whitespace errors. |

Only reducer, focused fixture and documentation changed in this corrective
pass. No view changed, so the isolated UI, ARM64 and module-isolation checks
below were not repeated; their results are prior implementation evidence.

### Earlier implementation validation

The earlier implementation runs below passed on Windows with explicit platform
selection. Builds reported **zero warnings and zero errors**. Their shared
App lifecycle fixture had **74 assertions** before the corrective additions.

| Command/check | Result |
| --- | --- |
| `tests\Verify-CopilotSessions.ps1 -Platform x64` | **45/45 scenarios**, including **74 App lifecycle assertions**, existing usage checks and telemetry/presentation state regressions. |
| `tests\Verify-ModuleIsolation.ps1 -Platform x64` | Full x64 solution build passed; private dependency versions **1.0.0 and 2.0.0** remained isolated; Copilot SDK/runtime/SQLite assets and host-owned exclusion passed. The earlier reported Bootstrap.Net environmental blocker did **not** recur. |
| `tests\Verify-CopilotAppLifecycle.ps1 -Platform x64 -BundlePath Pagurian\bin\x64\Debug\net10.0-windows10.0.22621.0\modules\Pagurian.Modules.Copilot` | **44 SQLite/process/resolver assertions** plus the shared **74 App lifecycle assertions**, including real deployed native SQLite resolution. |
| `tests\Verify-CopilotSessionPresentation.ps1 -Platform x64` | **106 UI checks**, isolated nonactivating fixture, scale 1.000. Clipboard tests were explicitly **not requested**. |
| `dotnet build Pagurian.Modules.Copilot\Pagurian.Modules.Copilot.csproj -p:Platform=ARM64` | Compile/deployment passed. Native PE machine checked as **AA64** for ARM64 and **8664** for x64. No ARM64 executable was run. |
| `git diff --check` | No whitespace errors. |

Coverage includes normal completion and next prompt without start, deferred
identity and reordered delivery, independent clients/roots, per-source
blockers and task resume, direct/alias/chat archive, no-hook archive/exit,
shared SDK isolation, routine detach/shutdown, old loaded metadata versus
historical rows, stale-state reset with retained telemetry, fresh-hook
precedence, unarchive/reload and stale snapshot fences, WAL visibility,
uncommitted writer transactions, exclusive read-lock timeout, missing/schema-
changed databases, denied process queries, invalid owner hints, stale locks,
SDK/App PID reuse and queued callbacks after Stop.

## Limits and future diagnosis

- The App schema, company/executable naming and inuse-lock protocol are observed
  internals, not a stable public extension API. An unsupported schema/install
  can conservatively delay restoration or archive detection. GitHub file
  company metadata is an identity sanity check, not cryptographic attestation.
- The current-runtime lock plus verified SDK ancestry is the positive
  per-session attachment signal. No SDK memory is inspected. If a future App
  leaves misleading current-instance locks behind or changes their semantics,
  the adapter needs new positive evidence; never substitute all historical
  unarchived rows, shutdown lines, SDK survival or cwd inference.
- The hook transport does not carry an authenticated App-generation ID.
  Per-source timestamps (or bridge ReceivedAt when absent), process creation
  times and visibility fences are the available ordering evidence. Perfect
  ordering of indistinguishable timestamp-free delayed events is impossible.
- Unknown observations intentionally favor retaining an existing cell over
  false removal, and favor withholding a new cell over historical resurrection.
  Locked/unavailable metadata may delay convergence; large directories and
  transcripts need multiple bounded scans.
- This work did not publish/relaunch the user's running application or verify a
  new live archive action against user data. Built artifacts and isolated
  fixtures validate the implementation; loading it into the user's running
  installation is a separate, user-authorized action.

For future reproduction, first correlate the module's payload-free
`session-transition` reasons (`app-turn-complete`, `app-end-awaiting-evidence`,
`app-archive`, `app-exit`, `app-runtime-restored`,
`app-visibility-or-runtime-fence`) and throttled `app-*-unavailable` codes.
Distinguish actual hooks from transcript lifecycle and process observations.
Check positive client/owner identity before diagnosing visibility.

With separately authorized read-only investigation, inspect only the target
session's mapping/archive columns, the owner hint and relevant inuse locks,
then validate process paths/start times/ancestry. Keep WAL with its live DB;
never use an immutable reader for a running App. Do not inspect token files,
dump prompts, manipulate user archive state or kill processes to simplify a
test. Reproduce changes in the injected fixture boundaries first, then rerun
the commands above. The current contract is documented in
[Copilot-Sessions.md](../Copilot-Sessions.md).
