# Copilot SDK sessions

The **Copilot SDK** shell is a read-only monitor for local Copilot sessions
that are currently held by another process. It is independent from the
**Copilot Sessions** shell: it does not install, read, or consume hooks, and
it does not use the App archive/process lifecycle adapter.

## Membership and polling

Membership comes from the SDK server-scoped `sessions.checkInUse` operation.
An occupied session remains a cell even when its inferred state is Idle. A
successful membership scan that no longer reports the lock removes the cell;
failed scans retain the cell and mark its data stale.

The shell scans membership every five seconds by default and reads each active
session's persisted event journal every three seconds by default. Both
intervals are configurable in the shell settings and are stored as
`polling.discoverySeconds` and `polling.statusSeconds` (1–3600 seconds). A
separate read is started two seconds after a possible blocker is first
observed. The same blocker must be
present after that fresh read before the shell displays **Blocked**. This
delay prevents a transient permission or tool event from flashing as a
blocker; it is not a real-time state guarantee.

## Event and state limits

Each read starts a new backward persisted-event read with a maximum of ten
events. The shell does not resume a target session, attach to its runtime, use
ephemeral event subscriptions, or page through the full history. The Billboard
therefore shows only the latest five persisted event rows, and its tree and
usage values are lower-bound information from the observed window. Missing
history is labeled **Partial** or **Unknown** rather than treated as Idle or
zero usage.

Permission request/completion events can provide a durable request identity,
but the absence of a completion event only means that no completion was
observed in the available journal. `ask_user` and `exit_plan_mode` are shown
as inferred blockers only when a clearly identified tool invocation has
started and has not completed. Their actual request and completion events are
ephemeral, so the shell cannot prove that a user prompt is still visible.

`ClientName` is the client that created or last resumed the session, not proof
of which process currently owns its lock. Known App, CLI, and VS Code markers
use the same three icons as the hook shell. Unknown or other markers remain
visible with a neutral icon and an explicit source label.

The SDK monitor owns a separate runtime shared by all Copilot SDK shell
instances. The existing usage/login runtime and the hook-based Copilot
Sessions shell are not changed or used as fallbacks.
