# Copilot session ownership and status

The Copilot shell publishes one cell per **resolved display owner**, not per
hook `sessionId`. No settings, hook commands, or payload formats change.

## Identity

- Own `workspace.yaml` with `client_name: github/autopilot` identifies an
  independently persisted Copilot App session. Even a `create_session`
  child remains independent; App UI ancestry is not task ancestry.
- An explicit other client identifies CLI behavior. An explicit relationship
  to a confirmed CLI parent likewise keeps the task source standalone.
- App task relationships come from explicit lifecycle fields or the owning
  App session's `events.jsonl` records: `subagent.started` and
  `subagent.completed` carry the child in the **top-level `agentId`**.
  Nested tasks resolve to the independently persisted App owner.
- A `transcriptPath` hint is accepted only for an `events.jsonl` under the
  configured local session-state directory whose owner is a confirmed App
  session. Shared cwd, trace ID, name, and missing workspace files prove
  nothing. Conflicting/cyclic claims stay unresolved.
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
new root hook, hint, or transcript write before its children resolve.
Discovery alone does not publish unrelated root cells.

Each pass examines up to 256 discovery directories and 256 candidate metadata
files, and advances up to 16 transcripts by at most 256 KiB / 512 lines each.
Transcript offsets retain partial lines and detect truncation/replacement.
Metadata is limited to 64 KiB; transcript lines above 64 KiB are skipped.
Consequently large histories need multiple passes, and an oversized identity
record may need later lifecycle evidence. Only identity metadata is extracted;
prompts, tool results, cwd and trace content are not used for inference.
Positive identity evidence is cached for the resolver lifetime, including
across transcript rotation and multi-turn child completion. Client identity
is immutable for a session; partial rewrites cannot downgrade a known App root
to CLI. Incomplete App-marker prefixes remain unknown. A final arbitrary CLI
client scalar must be newline-terminated before it is trusted.

## Source state and lifecycle

Each source owns its current permission blocker independently:

| Hook | Source contribution |
| --- | --- |
| `sessionStart` | Idle; a newer explicit start can reopen an ended generation |
| `userPromptSubmitted`, `preToolUse`, `postToolUse`, `postToolUseFailure` | Working; clears only this source's blocker |
| `permissionRequest` | Blocked; preserves this source's original blocked-since |
| `agentStop` | Idle; clears only this source's blocker, not a session exit |
| App `subagentStart` with `agentId` | Activates that child as Working |
| App `subagentStop` with `agentId` | Deactivates that child; keeps its identity for resume |
| Child `sessionEnd` | Ends that source, never removes the root cell |
| Root `sessionEnd` | Removes its cell and deactivates the group |
| Other hooks | Latest-event details only |

For CLI, subagent lifecycle hooks remain details-only; they do not clear or
remove another independently displayed session.

The aggregate is **Blocked > Working > Idle** over active members. The
one-second visual debounce affects only this aggregate; permission ownership
and end bookkeeping are immediate. A sibling cannot clear a blocker or restart
an unchanged Blocked candidate's timer. A newly discovered group starts
visually Idle until its candidate has been stable for one second.

Hook `timestamp` (ISO timestamp or Unix milliseconds) orders each source.
Without it, the bridge's `ReceivedAt` is used. Duplicate/older status and
child lifecycle records do not undo newer work. Root and standalone CLI exits
are ordered against explicit session starts, not activity: an exit remains
terminal even when newer work or permission arrived first. This lifecycle
evidence is retained while identity is unresolved. At equal timestamps a stop/idle
update loses to work/blocking; session exit remains terminal. If neither
timestamps nor a unique delivery ID distinguish old from new, perfect ordering
is impossible. Hook permission requests currently lack reliable `toolCallId`,
so ownership is intentionally **per session**, not per tool call.

An ended root cannot be recreated by child events or late resolver callbacks.
Only a newer explicit root `sessionStart` reopens it; old member state cannot
cross that boundary. A child's `subagentStop` is resumable by later activity,
whereas its own `sessionEnd` requires a newer explicit `sessionStart` or App
`subagentStart`. Pending unknown-identity lifecycle evidence and own events
retain child timestamp ordering, so an older child exit cannot hide a newer permission.

## Existing UI and diagnostics

The tracker owns stable root `CopilotSession` objects. Shell notifications
concern owners only, so child activity, completion and exit do not replace
the owner's cell or close its billboard.

Latest-event JSON retains the received payload verbatim in its original
envelope shape, including the child's `sessionId` or the parent's
`sessionId` plus `agentId` on a stop. Current blockers are stored independently
(source, owner, blocked-since, triggering event/time) and shown in the existing
details text, so later sibling details cannot erase the reason for Blocked.
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
dotnet build Pagurian.Modules.Copilot\Pagurian.Modules.Copilot.csproj -p:Platform=x64
.\tests\Verify-ModuleIsolation.ps1 -Platform x64
```

Requires .NET SDK 10.0.302+ for the application build. The dependency-free
`net10.0` console fixture links the production identity index/resolver,
reducer and tracker, with a deterministic clock and a fake dispatcher/timer
boundary. Temporary sanitized metadata/transcripts cover App gating,
standalone sessions, unknown waiting, early child hooks, nested agents,
member blockers, stable root lifecycle, resume, duplicate/out-of-order events,
debounce, partial/rotated transcripts, and stop/start callbacks. It does not
read user transcripts, install a test framework, launch a WinUI window,
publish, or replace any active Pagurian bundle.
