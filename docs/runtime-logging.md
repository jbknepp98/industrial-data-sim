# Runtime logging and troubleshooting

The durable runtime accepts an optional `ILogger<DurableRuntime>`. It emits
plain-language messages and a separate `action` explaining what to check next.
Stable event codes and structured fields let an agent or operator find related
events without interpreting prose. SQLite remains authoritative for recovery;
logs can be missing, rotated, or incomplete and must never drive replay decisions.

## Enable local logs

The runtime library defaults to no logging. A host must explicitly provide a
logger. The existing validation/dry-run CLI is unchanged: its JSON response still
goes to stdout and it does not start the durable runtime.

```csharp
using IndustrialDataSim.Runtime;
using Microsoft.Extensions.Logging;

// Declare the logger first so it outlives runtime disposal.
using var logs = new RuntimeFileLogger("logs", minimumLevel: LogLevel.Information,
    maximumFileBytes: 1024 * 1024, retainedFiles: 5);
using var runtime = new DurableRuntime("state/simulation.db", logger: logs);
```

`runtime.jsonl` contains one JSON object per line, readable with any text editor
or JSON-lines viewer. Numbered archives run from `runtime.1.jsonl` (newest) to
`runtime.4.jsonl` (oldest) with these defaults. Rotation happens before a new record
would exceed the byte limit. The count includes the active file; defaults retain
up to 5 MiB of newly written log records. Changing limits does not retroactively
resize old archives. Use one dedicated, access-controlled local directory per
runtime and keep retention settings consistent. An exclusive owner file prevents
two cooperating logger instances from rotating the same directory. Do not delete
a live owner file or use filesystem aliases to bypass ownership.

Files close after each event; there is no in-memory log queue and no per-sample
logging. Writes are synchronous and can delay the caller on slow storage. This
first increment favors bounded memory and straightforward failure handling over
a background logging service. Logs are not fsynced as recovery evidence. A crash
or storage error can leave a partial final line; a reader should report/skip that
line and inspect SQLite. Do not infer failed delivery from a missing log record.

## Levels and event codes

| Level | Events | Meaning and next action |
| --- | --- | --- |
| Information | `host.started`, `host.progress_state`, `host.stopped` | Continuous host lifecycle and changed worker state; idle polling does not emit repeated events. |
| Information | `worker.started`, `worker.stopped` | Bounded simulation worker lifecycle and stop reason; inspect session state if unfinished. |
| Information | `runtime.opened`, `runtime.closed` | Database lifecycle. Startup recovery has committed before the opened event. |
| Information | `session.admitted`, `session.paused`, `session.resumed`, `session.completed`, `session.tags_released` | Durable lifecycle changes. Completion refers to the simulated transport, not a production Historian receipt. |
| Information | `session.cancellation_requested`, `session.cancelled` | Generation has stopped under the selected drain/discard policy; terminal cancellation retains ownership until explicit release. See [cancellation](session-cancellation.md). |
| Information | `generation.retry_enabled` | Oversized-point recovery committed; generation is Ready at its unchanged checkpoint. Existing queued batches remain unchanged. |
| Information | `generation.unblocked` | Queue and disk checks permit another generation attempt; this does not guarantee the next transaction succeeds. |
| Warning | `generation.queue_full` | Deliver pending work or review queue limits. Inspect Uncertain sessions if their retained work blocks capacity. Cursor is unchanged. |
| Warning | `generation.disk_low` | Free space on the state volume. Cursor is unchanged. |
| Warning | `delivery.uncertain`, `delivery.interrupted` | Preserve payload and ownership; investigate acceptance evidence. Never resend based on missing samples. Startup reports the interrupted batch count; list Uncertain sessions to locate the batches. |
| Error | `generation.point_too_large`, `generation.non_finite_value` | Inspect the output-tag index, sample time, and candidate slot. Session is Failed, with the explanation persisted in SQLite. |
| Error | `delivery.payload_integrity`, `runtime.configuration_integrity` | Stop and restore verified state; do not submit altered payloads or edited configurations. |
| Error | `runtime.storage_failure` and other returned `runtime.*` failures | Follow the event's corrective guidance. Check disk space, permissions, compatible versions, ownership, or configuration as indicated. |
| Debug | `generation.committed`, `delivery.claimed`, `delivery.acknowledged` | Bounded window/batch diagnostics. A claim is submission intent only. Enable Debug when investigating ordering or queue behavior. |

Repeated queue/disk warnings are suppressed while the same condition persists for
a session. A changed condition produces a new warning. Passing those checks emits
`generation.unblocked`; if the condition later recurs, it is reported again.
Configuration-integrity failures are persisted as Failed and logged once with
session and operation context before an eligible round continues with other sessions.
The payload, checkpoint, and reservations remain protected. Errors from nested
runtime calls are logged once as they propagate. Constructor
failures before database initialization and calls on a disposed runtime are
returned to the host; the host must report their safe explanations. The logger
is not a complete audit trail of every method call.

## Reading an event

An uncertain-delivery record includes fields such as:

```json
{
  "timestampUtc": "2026-09-18T12:00:00+00:00",
  "level": "Warning",
  "eventCode": "delivery.uncertain",
  "operation": "Finish",
  "sessionId": "session-a",
  "batchId": 7,
  "startSlot": 300,
  "endSlot": 600,
  "pointCount": 300,
  "byteCount": 17400,
  "message": "Batch acceptance is uncertain; session and subsequent delivery are stopped.",
  "action": "Preserve payload and tag ownership. Investigate acceptance evidence; do not resend missing samples or force acknowledgement."
}
```

This is an illustrative excerpt; fields without context are serialized as null.
`timestampUtc` is the real event time. `sampleUtc`, when present on a generation
failure, is the simulated sample time. `outputTagIndex` is zero-based and maps to
`session.outputTags` in the immutable configuration. `candidateSlot` identifies
the failing candidate, whereas a batch's `[startSlot, endSlot)` is its flattened
candidate range. Suppressed candidates can make this range larger than its point
count. The failed window does not advance the durable cursor.

For a failed generation, inspect `GetSession(id).ErrorCode/ErrorMessage`, the
indicated configuration entry, and `Batches(id)` for already queued work.
`Resume` remains restricted to Paused sessions. An oversized-point failure has a
separate checked recovery: reopen with larger batch/compatible queue byte limits,
then call `RetryGeneration(id)`. See [recovery instructions](durable-runtime-v1.md#recovering-an-oversized-point).
Other Failed sessions and unresolved submissions cannot use this operation.

## Privacy and logger failures

Only selected fields cross the runtime's logging boundary. Logs do not include
configuration documents, dataset/profile names, tag names or values, payloads,
credentials, tokens, file paths, or raw exception details. Session IDs are
intentional correlation data: use neutral identifiers, never secrets. Their
ASCII letters/digits/hyphen/underscore shape and 64-character limit are checked
again before logging. Shape validation cannot recognize a secret deliberately
placed in an otherwise valid session ID.

The file sink accepts only internal runtime events. It ignores arbitrary log
messages, exception arguments, and scopes rather than trying to redact unknown
text after formatting. An injected standard provider receives the same selected
runtime fields; the host is responsible for any extra scopes, enrichers, or
other application logs it adds. Store logs locally and review them before sharing.
Generated file names and the `logs/` directory are ignored by Git.

File access/rotation failures drop the affected event, increment
`RuntimeFileLogger.FailureCount`, and set `IsAvailable` false. A single safe
warning goes to stderr for that failure episode. Subsequent events try again;
success restores availability and emits a safe stderr recovery notice explaining
that earlier events may have been lost. A broken stderr is tolerated. Observe
these health properties in a host instead of assuming silence means success.
The logger does not retry a dropped event. `FailureCount` counts failed attempts,
including initialization, rather than exactly counting lost records.

Exceptions from an injected provider are contained by the runtime;
`DurableRuntime.LoggingFailureCount` counts them and the first failure produces a
safe stderr notice. The file sink handles its own storage failures, so those
appear in the sink's counter. Runtime progress, acknowledgement, and recovery do
not depend on successful logging. No logger can provide observations before it
exists or after a process has terminated.

Tests cover level filtering, lifecycle context, pressure suppression/recovery,
uncertain delivery, startup recovery, rollback, actionable storage errors,
configuration/value exclusion, file rotation/retention, concurrent writes,
exclusive ownership, logging outages/recovery, and throwing providers/stderr.

Uses the standard [.NET logging abstractions](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging).
A future host can supply its own provider without changing the runtime state
contract. The [continuous host](continuous-host.md) uses the same sink. Production
delivery and centralized collection remain future work.
