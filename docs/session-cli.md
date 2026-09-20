# Durable session CLI

The `session` command group operates local, simulation-only SQLite state. It does
not connect to Historian or load credentials. The explicit run-simulated command
drives generation and fake delivery in the foreground; other commands do not run work. Existing validate and dry-run commands retain their original behavior.
Run `session help` for the command syntax as a JSON response.

Use the CLI project through `dotnet run --project src/IndustrialDataSim.Cli --`
followed by a command below. Quote paths and arguments containing spaces.

| Command | Behavior |
| --- | --- |
| `session start <database> <model-file>` | Validate and admit an immutable model; create the database if needed. The session becomes Ready but does not execute. |
| `session run-simulated <database> <maximum-rounds> [batch-bytes]` | Drive a bounded foreground worker against the fake Historian. See [worker contract](simulation-worker.md) for limits, graceful stop, and outcomes. |
| `session list <database> [after-session-id]` | Return up to 100 sessions, sorted by ordinal ID. Pass nextCursor to fetch the next page. |
| `session status <database> <session-id>` | Return lifecycle state, candidate cursor, queue counts, safe saved error, cancellation mode, and session-local tag progress as UTC timestamps. |
| `session batches <database> <session-id> [after-batch-id]` | Return up to 100 batch metadata entries. Payloads are neither loaded nor returned. Pass nextCursor to continue. |
| `session pause <database> <session-id>` | Stop future generation and claims for a pausable session. An existing submission may still finish. |
| `session resume <database> <session-id>` | Resume only a Paused session. Failed/Uncertain/cancelled sessions cannot use this command. |
| `session cancel <database> <session-id> drain` | Stop generation and leave queued work eligible for delivery by a caller-driven runtime. It does not launch a worker to drain the queue. |
| `session cancel <database> <session-id> discard-pending` | Permanently discard only safely unsent payloads, subject to the cancellation protections. |
| `session release <database> <session-id>` | Explicitly release reservations for Complete or safely Cancelled sessions. Retain progress and global ordering protection. |
| `session retry-generation <database> <session-id> <batch-bytes>` | Check oversized-point recovery with a byte limit from 128 through 4194304. Never retries delivery. |

The retry limit applies to this invocation's preflight only. Runtime limits are
not persisted; configure matching or larger compatible limits when the next host
opens the database to generate. The library's default queue byte limits cover the
CLI's permitted batch-byte range. Do not interpret Ready as proof that generation
will proceed under different limits or without queue/disk backpressure.

## Ownership and startup effects

Use these commands while no other runtime owns the database. A running owner
produces `runtime.owner_unavailable` with safe instructions. Use [live controls](continuous-host.md) while the continuous host owns the database;
these session commands do not bypass the exclusive database owner.

Every database open performs normal migration and recovery. Status/list/batches
are inspection commands, but opening their database is not a read-only SQLite
operation: old schemas may upgrade and interrupted Sending batches become
Uncertain. Use a SQLite-aware backup before an upgrade. Only start permits creation
of a missing database. Other commands require an accessible existing database
and use SQLite ReadWrite mode, preventing accidental creation after a missing-file
check. Invalid command syntax and invalid start models are rejected before opening
state or creating logs. An existing ID cannot replace its original configuration.

## Response and exit codes

Stdout contains exactly one JSON response, capped at 4 MiB including its final LF.
A successful response has schemaVersion 1, valid true, mode `simulation-only`, a
command name, errors [], and a result. Status and cancellation enums are readable
strings. A successful status command may report a Failed or Uncertain session;
valid describes command success, not job completion.

Example status result excerpt:

```json
{
  "session": {
    "sessionId": "example-job",
    "status": "Cancelling",
    "nextSlot": 300,
    "totalSlots": 900,
    "queuedPoints": 300,
    "queuedBytes": 17400,
    "errorCode": null,
    "errorMessage": null,
    "cancellation": "Drain"
  },
  "progress": [
    {
      "tag": "Example.Temperature",
      "bufferedUtc": "2026-09-01T00:01:39Z",
      "submittedUtc": null,
      "acknowledgedUtc": null
    }
  ]
}
```

List/batch cursors are exclusive. A full page returns its last ID as nextCursor;
the following page can be empty if that full page was the last. A null nextCursor
ends pagination. Status includes up to the runtime's 1000 declared tags. Inspection
contains local identifiers and tag names; treat it as local data, not a public
sanitized report. Models, credentials, and payload values are not returned.

| Exit | Meaning |
| --- | --- |
| 0 | Command succeeded; inspect session status separately. |
| 1 | Model/runtime refusal, missing/inaccessible state, or response-size failure. Read errors[].code/path/message. |
| 2 | Invalid command syntax, mode, or numeric argument; response explains allowed forms. |
| 4 | Worker stopped unfinished: Blocked or RoundLimit. Inspect result.stopReason and session state. |
| 130 | Bounded worker stopped gracefully after Ctrl+C. |
| 3 | Input-file or CLI path/filesystem access failure. Runtime-owned database failures instead use their runtime error code and exit 1. |

An oversized response is replaced by one small failure envelope; no partial JSON
is emitted. A mutation can commit before response serialization, disposal, or
stdout fails. Inspect durable state before repeating a command. In particular,
start is not a way to resume an existing session. Errors do not echo paths,
configuration documents, or raw exception text.

## Logs and next steps

Commands enable bounded rotating logs at `logs/<database-filename>/` beside the
database. Operational events go to those files, with safe logging-outage notices
on stderr. They never mix with the JSON response on stdout. Defaults and health
behavior are described in [runtime logging](runtime-logging.md). A logger failure
does not roll back the command or authorize delivery retries.

See [cancellation](session-cancellation.md) and [durable runtime](durable-runtime-v1.md)
for state transitions, release, generation recovery, and unresolved-delivery rules.
The [bounded worker](simulation-worker.md) drives generation and fake delivery.
The [continuous host](continuous-host.md) supplies resident foreground execution and live controls. Separate [production commands](production-delivery-v1.md) now implement the
[owner-approved blind-publish policy](delivery-recovery.md).

Completed history can be explicitly exported and pruned using `session archive`;
see [audit archival](audit-archival.md) for eligibility, verification and crash behavior.
