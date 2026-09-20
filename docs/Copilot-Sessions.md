# Copilot session ownership and status

The Copilot shell publishes one cell per **resolved display owner**, not per
hook `sessionId`. No settings, hook commands, or payload formats change.

## Identity

- Own `workspace.yaml` with `client_name: github/autopilot` identifies an
  independently persisted Copilot App session. Even a `create_session`
  child remains independent; App UI ancestry is not task ancestry.
- Display ownership and client branding are separate. `github/cli` selects
  the CLI icon; `vscode` and `vscode-agent-host` select VS Code. These exact
  markers were checked against the local CLI 1.0.83 native producer
  (`sessionConstantsCliClientName()` and the telemetry client allowlist).
  Unsupported explicit markers select **Other**, not a CLI brand. All explicit
  non-App clients retain independent/CLI ownership behavior. This supports
  only VS Code sessions already sending these hooks, not built-in Chat ingestion.
- App task relationships come from explicit lifecycle fields or the owning
  App session's `events.jsonl` records: `subagent.started` and
  `subagent.completed` carry the child in the **top-level `agentId`**.
  Nested tasks resolve to the independently persisted App owner. Published
  nodes retain their immediate task parent and their **own** name. The
  documented `subagent.started.data.parentId` is a task parent; top-level
  event `parentId` is an event-chain link and is never session ancestry.
  `agentDisplayName` / `agentName` supplies task names; missing names use IDs.
- A `transcriptPath` hint is accepted only for an `events.jsonl` under the
  configured local session-state directory whose owner is a confirmed App
  session. Shared cwd, trace ID, name, and missing workspace files prove
  nothing. A hint identifies an owner, not an immediate parent; it cannot
  flatten a nested tree. Conflicting immediate parents (even within the same
  owner) and cyclic claims stay unresolved.
- Unknown sources wait **without a cell or timeout fallback**. Their latest
  event and reduced status (including permission ownership) are retained,
  not an unbounded queue of raw events. A resolution batch attaches that
  state atomically, so a task agent never flashes an independent cell.

The resolver polls off the UI thread (500 ms idle cadence, wake on hooks).
Observed IDs, explicit parents, and validated transcript hints are checked
for delayed metadata. Background discovery additionally admits local App
roots whose workspace or transcript was written within seven days, allowing
attachment before a root hook arrives. A known/observed older root is still
eligible; an unobserved root inactive for more than seven days may require a
new root hook, hint, or transcript write before its children resolve **unless
positive App loaded-session evidence admits it directly**. That separate
discovery pass has no age cutoff. Discovery of metadata or database rows alone
does not publish unrelated root cells.

Each pass examines up to 256 discovery directories and 256 candidate metadata
files, and advances up to 16 transcripts by at most 256 KiB / 512 lines each.
Transcript offsets retain partial lines and detect truncation/replacement.
Metadata is limited to 64 KiB. Transcript lines can span passes and are now
bounded at **2 MiB**, accommodating current model-call records over 500 KB.
JSON parsing is background-only, depth-limited to 32; the bounded line/document
is released after scalar projection. Prompts, messages and tool results are
not retained in telemetry models. Oversized/malformed records yield throttled
reason codes and Partial coverage, never payload logging. Large histories need
multiple passes. Telemetry is bounded to 512 sources and 65,536 observations
overall (at most 8,192 per source); eviction is explicitly Partial.
cwd is display metadata only, never ownership evidence.
Positive identity evidence is cached for the resolver lifetime, including
across transcript rotation and multi-turn child completion. Client identity
is immutable for a session; partial rewrites cannot downgrade a known App root
to CLI. Incomplete App-marker prefixes remain unknown. A final arbitrary CLI
client scalar must be newline-terminated before it is trusted.

## Source state and lifecycle

Each source owns its current permission blocker independently:

| Hook | Source contribution |
| --- | --- |
| `sessionStart` | Idle; can reopen an ended CLI generation, not an archived App root |
| `userPromptSubmitted`, `preToolUse`, `postToolUse`, `postToolUseFailure` | Working; clears only this source's blocker |
| `permissionRequest` | Blocked; preserves this source's original blocked-since |
| `agentStop` | Idle; clears only this source's blocker, not a session exit |
| App `subagentStart` with `agentId` | Activates that child as Working |
| App `subagentStop` with `agentId` | Deactivates that child; keeps its identity for resume |
| Child `sessionEnd` | Ends that source, never removes the root cell |
| App root `sessionEnd(reason=complete)` | Idle for that source; retains cell and other sources' work |
| App root `sessionEnd` with absent/other reason | Source-local Idle; persistent visibility awaits authoritative evidence |
| Independent CLI/VS Code/Other `sessionEnd` | Removes its cell and ends its generation |
| Other hooks | Received-hook summaries only; no status transition |

For CLI, subagent lifecycle hooks remain details-only; they do not clear or
remove another independently displayed session.

The aggregate is **Blocked > Working > Idle** over active members. The
one-second visual debounce affects only this aggregate; permission ownership
and end bookkeeping are immediate. A sibling cannot clear a blocker or restart
an unchanged Blocked candidate's timer. A newly discovered group starts
visually Idle until its candidate has been stable for one second.

Hook `timestamp` (ISO timestamp or Unix milliseconds) orders each source.
Without it, the bridge's `ReceivedAt` is used. Duplicate/older status and
child lifecycle records do not undo newer work. Independent CLI-style exits
are ordered against explicit session starts, not activity: an exit remains
terminal even when newer work or permission arrived first. This lifecycle
evidence is retained while identity is unresolved. At equal timestamps a stop/idle
update loses to work/blocking; session exit remains terminal. If neither
timestamps nor a unique delivery ID distinguish old from new, perfect ordering
is impossible. Hook permission requests currently lack reliable `toolCallId`,
so ownership is intentionally **per session**, not per tool call.

An ended independent CLI-style root cannot be recreated by child events or late
resolver callbacks. Only a newer explicit root `sessionStart` reopens it; old
member state cannot cross that boundary. An App root instead follows the
archive/process visibility contract below. A child's `subagentStop` is resumable by later activity,
whereas its own `sessionEnd` requires a newer explicit `sessionStart` or App
`subagentStart`. Pending unknown-identity lifecycle evidence and own events
retain child timestamp ordering, so an older child exit cannot hide a newer permission.

Tree snapshots retain completed and ended children for the conversation
(the current explicit generation for independent CLI clients). These nodes continue contributing attributable usage, not active
status. `agentStop` means Idle, never Completed. Hookless known tasks show
Unknown status. Timestamped local transcript completion/shutdown can mark a
confirmed App task node terminal and exclude it from active aggregation when
at least as recent as its hook state. Completion permanently fences the old
permission, even after a later transcript start; transcript activity does not
replay Working/Idle or reopen roots. A fresh hook supersedes older transcript
lifecycle and starts a new blocker history if needed. App root transcript
`session.shutdown` is not archive, process exit, or a terminal root tree node.
An independent CLI root restart still fences old observations and hooks.

## App visibility and runtime generations

Three independent sources now describe a positively identified App root:

1. **Work:** hooks reduce each source's Idle/Working/Blocked state. Normal
   completion does not end the conversation or require another `sessionStart`.
2. **Archive:** `CopilotArchiveReader` reads local `.copilot\data.db` with
   Microsoft.Data.Sqlite, `Mode=ReadOnly`, no pooling, a one-second lock timeout,
   parameterized IDs and one deferred read transaction per batch. This is a
   normal WAL-aware reader, not an immutable connection or a copied main file.
   Schema capability checks require the observed mapping/archive columns.
   Direct `workspaces.session_id` and `workspace_session_aliases` mappings use
   **workspaces.archived_at**, even when `sessions.archived_at` disagrees.
   Only an unmapped, explicitly `session_type='chat'` row uses the session flag.
   Missing/dangling/conflicting mappings, unsupported schema, malformed archive
   values and unavailable reads are Unknown. `is_running=0` is never archive.
3. **App runtime:** `CopilotAppProcesses` reads only the bounded
   `.copilot\run\single-instance.owner` PID hint, validates the live
   `github.exe` full path, GitHub company metadata and creation time, and keeps
   process handles for actual exit observations. Session `inuse.<pid>.lock`
   timestamps must postdate a live `copilot.exe` instance whose ancestry leads
   to that exact App process instance. Ancestor creation times reject PID
   reuse. SDK survival, session detach, missing hints and access denial are
   not archive/exit evidence. No credential files are read.

`CopilotAppLifecycleReader` runs inside the existing resolver worker. Each
pass examines up to 128 known IDs plus 128 resumably enumerated directories,
and at most 16 runtime locks per directory. Loaded IDs go straight to metadata
resolution, including older-than-seven-day roots. It never enumerates all
unarchived database rows. New restoration requires positive App identity,
current load evidence **and** known nonarchived state. Unknown evidence keeps
existing visibility and emits throttled payload-free reason codes, but cannot
create an unobserved root. Routine detach does not remove an existing root.
Verified loaded-process ownership attaches to an already hook-visible root
even when archive state is Unknown, so a later confirmed App Exit can remove
it despite an unavailable database. This binding does not restore hidden or
historical roots, and a replacement process still resets the work generation.

Identity and lifecycle evidence reach the dispatcher as one versioned batch.
Archive hides exactly its root group; verified App exit hides only groups
bound to that process instance. An archive/exit visibility fence rejects older
hooks and snapshots. Newer hooks received while hidden retain reduced
per-source state, not a raw-event queue, without showing a cell. Positive
restoration then admits only work and task lifecycle evidence meeting the new
runtime/load fence; unrelated hook summaries cannot replace a pending blocker.
An unarchived root must have a new positive load timestamp
after its removal fence; neither a new hook nor an old surviving lock suffices.
Process replacement is keyed by PID **and creation time/path**, not PID alone.

The App **work epoch** is distinct from the conversation's telemetry epoch.
Loaded roots in a new process instance initially become Idle; old permissions,
active children and debounce candidates are reset. Fresh timestamped hooks
already delivered from that instance win over initial Idle, including their
blocker provenance and already committed visual status. Historical nodes,
received-hook summaries and attributable usage remain conversation-owned;
they do not replay work or activate old tasks. Final tracker Stop still clears
the in-memory tracker; a new tracker reads bounded transcript history again.

The Copilot module deploys `Microsoft.Data.Sqlite.dll`, SQLitePCLRaw core,
batteries and e_sqlite3 provider plus the matching native `e_sqlite3.dll`.
The explicit bundle dependency is pinned to SQLitePCLRaw 2.1.13 rather than the
provider's vulnerable older transitive minimum. x64 and ARM64 use their own
runtime assets; host SDK/Reactor/WinUI assemblies remain shared.

These database tables, process naming/company metadata and runtime lock files
are observed **internal** App contracts, not a stable public API or a
cryptographic authentication mechanism. Unsupported installations/permissions
fail conservatively to Unknown. A valid current-runtime lock is the positive
per-session attachment signal; this integration does not introspect SDK memory.
If that internal protocol changes, update the adapter and fixtures rather than
guess from transcript shutdown, cwd, UI closure, or process names alone.
See [the dated incident and validation record](IssueHistory/2026-09-11-Copilot-App-Session-Lifecycle.md).

## Local details and accounting

The shared incremental transcript reader projects `session.model_change`,
`model.model_call_started`, `model.model_call_success`, `assistant.usage`,
`session.usage_info`, successful `session.compaction_complete`, and
`session.usage_checkpoint`. Main-session model, reasoning, context tier,
context occupancy and limits remain main-session values, not group sums.
Each node's breakdown uses its own data.

- `modelInfo.capabilities.limits.max_context_window_tokens` and
  `max_prompt_tokens` are limits only. A model switch invalidates stale limits.
  Last-request `responseUsage.prompt_tokens` is input usage, **not current
  context occupancy**. Occupancy requires an explicit usage-info/compaction
  record. Missing values display Unavailable, not numeric zero.
- Input/output and cache read/creation tokens come from per-call usage.
  `copilotUsage.total_nano_aiu` (or SDK `totalNanoAiu`) is retained as an exact
  integer, displayed explicitly in **nano-AIU**.
- Stable provider request, API-call and service-request aliases deduplicate
  mirrored root/child events and overlapping SDK/model-call projections.
  Top-level `agentId` attributes a record to its source, but does not itself
  establish a task relationship. Independent roots reject foreign lifecycle
  projections. Records without sufficient call identity are not guessed.
- `totalNanoAiu` / `totalPremiumRequests` checkpoints are cumulative and can
  include children. The latest checkpoint replaces earlier checkpoints.
  They are shown separately as **reported session counters, not added**;
  they are neither own-agent usage nor attributable group totals. In particular,
  taking the maximum checkpoint is not proof of a disjoint group total.
- Group totals deduplicate the disjoint known call observations of all nodes,
  including completed/ended nodes. Persisted call coverage is conservatively
  **Partial**, not a claim of lifetime accounting. Conflicting mirrors,
  missing fields, eviction, replacement, overflow and unavailable nodes cannot
  silently become complete totals.
- Append/partial-line handling shares the identity cursor. Replacement removes
  projections from that transcript origin before rebuilding. Generation
  filtering for independent CLI generations discards older observations;
  App process restarts preserve conversation observations. A newly timestamped checkpoint alone
  cannot prove a new accounting interval.

There are no network, quota, account or resume calls. `CopilotUsageService`
and the separate account Usage shell are unrelated and unchanged. Unchanged
background detail snapshots reuse their identity publication; hook-only UI
updates reuse filtered details and computed totals.

## Session presentation and diagnostics

The tracker owns stable root `CopilotSession` objects. Shell notifications
concern owners only, so child activity, completion and exit do not replace
the owner's cell or close its billboard.

Cells show two centered rows: packaged client icon plus aggregate status,
then the cwd basename. All statuses/clients use one width measured from the
longest Idle/Working/Blocked caption with WinUI's actual caption style, plus
icon/spacing/padding, rounded to four DIPs. Text-scale changes remeasure.
Project text occupies a finite star column with ellipsis; drive/share roots
have meaningful labels. The host's tooltip callback exposes full name/cwd.
The three icons deploy with the module and resolve with `ModuleAssets`,
never from developer installation/download paths.

The billboard has a 600-DIP width and 640-DIP content ceiling with a bounded
scroll viewport below the header. Its overview/header uses aggregate status;
its keyed expandable tree shows **local** node status and lifecycle. Expansion
survives snapshot updates. One breakdown Card below the tree shows **all**
nodes regardless of expansion. Blocker provenance includes blocked-since and
source. Copy ID buttons copy the complete ID through the clicked window's
HWND-owned native clipboard and show checked success/failure feedback,
including contention. The host remains `NoActivate=true`.

At the very bottom, at most five received-hook rows show exactly
`HH:mm:ss eventType toolName`, local time, newest first, with `-` for missing
tool names. Each is a bounded single line; no JSON, prompts, arguments, dates
or source IDs appear in these rows. Whitespace/control characters in labels
are normalized to keep the three fields on one line. Each unresolved source
retains five summaries; attachment merges member buffers by parsed timestamp,
then arrival sequence for ties. App completion and process changes preserve
conversation summaries without replaying work; CLI generation reset and
tracker teardown fence their histories. These are hooks, not transcript replay.

Raw latest-hook compatibility fields remain internal, and hook-file logging
is unchanged; the billboard no longer renders raw payloads.
The reducer retains at most 256 transitions globally and exposes at most 64
per display owner. The module logger records source-attributed transitions
without payload values; resolver warning reason codes are throttled.

`HookEventLog` writes best-effort JSONL to
`%LOCALAPPDATA%\Pagurian\Copilot\`. Each bridge process lazily creates one
exclusive `hook-events-YYYYMMDD-HHmmss-fff.log`; concurrent processes never
overwrite a run file. When a new run file is created, only matching files
whose filesystem creation date is before today's local date are removed.
Same-day files, unrelated files, the unified `pagurian.log`, Compass logs,
hook installation, and Copilot session files are untouched. The unified
module log uses the host logger. Tests redirect only their isolated fixture
environment.

Tracker consumers share one resolver/timer. Final Stop invalidates queued
callbacks before cancelling the worker, detaches the timer, clears members,
owners, routing and diagnostics, and does not synchronously wait for disk I/O
on the UI thread. No process is killed or relaunched.

## Regression validation

```powershell
.\tests\Verify-CopilotSessions.ps1 -Platform x64
.\tests\Verify-CopilotAppLifecycle.ps1 -Platform x64
dotnet build Pagurian.Modules.Copilot\Pagurian.Modules.Copilot.csproj -p:Platform=x64
.\tests\Verify-CopilotAppLifecycle.ps1 -Platform x64 -BundlePath `
  Pagurian\bin\x64\Debug\net10.0-windows10.0.22621.0\modules\Pagurian.Modules.Copilot
.\tests\Verify-CopilotSessionPresentation.ps1 -Platform x64
# Optional real clipboard roundtrip + contention; replaces clipboard contents:
.\tests\Verify-CopilotSessionPresentation.ps1 -Platform x64 -Clipboard
.\tests\Verify-ModuleIsolation.ps1 -Platform x64
```

Requires .NET SDK 10.0.302+ for the application build. The dependency-free
`net10.0` console fixture links the production identity index/resolver,
reducer and tracker, with a deterministic clock and a fake dispatcher/timer
boundary. Sanitized metadata/transcripts under repository `artifacts` cover App gating,
standalone sessions, unknown waiting, early child hooks, nested agents,
member blockers, stable root lifecycle, resume, duplicate/out-of-order events,
debounce, partial/rotated transcripts, and stop/start callbacks. It does not
read user transcripts, install a test framework, launch a WinUI window,
publish, or replace any active Pagurian bundle.

The separate SQLite fixture links production archive/process/lifecycle adapters,
injects fake process handles and creates local disposable databases under
`artifacts`. It covers direct/alias/standalone archive, WAL visibility and
concurrent writer transactions, bounded exclusive-lock waits, missing/schema-
changed databases, stale locks, PID reuse, ancestry and permission failures,
bounded old-metadata discovery, and no-hook archive/exit. `-BundlePath` also opens
its fixture WAL database through SQLite loaded by the production private
`ModuleLoadContext`, exercising deployed managed **and native** resolution.
The pure fixture covers deferred completion, blockers, archive/reload fences,
hidden root/child hooks before delayed restoration (including deferred child
identity and task start/stop/resume), Unknown-archive process ownership,
process-work reset versus retained telemetry and queued lifecycle callbacks after
Stop. No test reads a user's App database or terminates a real process.

The presentation fixture mounts production cells/billboard through the host
BillboardSession in isolated nonactivating windows, without starting the
Copilot tracker, loading user configuration or installing hooks. It checks
two-row bounds, equal four-DIP-rounded widths for all statuses/clients,
ellipsis, bounded scrolling, local-vs-group state, all-node breakdown,
expansion preservation, exact recent rows, integer display and (when opted
in) clipboard readback/contention without activation. `-BuildOnly` skips UI.
The recorded interactive run used 100% display scale and exercised sampled
Light/Dark changes. High Contrast, other DPI settings and maximum system text
scale still require manual validation; the fixture does not change user
accessibility settings.
