# Delivery and recovery decision

This is the planned production policy, not an implemented writer. The priority
is preserving per-tag forward ordering without replaying an accepted timestamp.
Availability must yield to uncertainty when server evidence is insufficient.

## Distinct evidence

| State/evidence | Meaning | Permitted action |
| --- | --- | --- |
| Generated | Values calculated in memory | Persist payload and generator checkpoint together |
| Pending | Immutable payload durably buffered; never entered Sending | Submit in per-tag order |
| Sending | Durable intent recorded before issuing HTTP | Treat as Uncertain after restart |
| Acknowledged | Successful acceptance response durably recorded under a verified API contract | Advance acknowledged per-tag position |
| Retained representation checked | Returned records and required transitions match expectations | Record evidence separately; do not infer delivery of omitted repeats |
| Uncertain | Submission may have taken effect, but acknowledgement is not durable or conclusive | Stop affected session, retain ownership and payload; do not replay |

Record generated, buffered, submitted, and acknowledged positions separately.
Keep uncertain batch ranges and hashes. The latest retained timestamp is useful
for detecting conflicts with existing data, but is not a substitute for these
positions. A returned value can be older than the last accepted repeated sample.

## Restart and concurrency

One process owns the local state database. Acquire Dataset/tag reservations
atomically; all sessions, including paused and uncertain sessions, retain their
reservations until pending delivery is safely resolved. Multiple sessions may
share a Dataset only for disjoint tags. Reject overlapping reservations using
the project's conservative case-insensitive comparison. External writers remain
an operational constraint: local reservations cannot exclude them.

Persist immutable payloads, generator progress, and queue state transactionally.
Persist Sending before network I/O; never hold a database transaction over HTTP.
A crash between that commit and the actual request is conservatively Uncertain.
Do not send later batches for a session with an uncertain earlier batch. Keep
unrelated sessions running. Apply normal backpressure while delivery is stopped.

## Ambiguity and operator guidance

A timeout, disconnect, malformed reply, or process crash may happen after server
acceptance. Even a server error may follow partial acceptance. Missing samples,
including an absent suffix, do not prove non-delivery because Historian suppresses
repeats. Therefore Phase 1 will not automatically resend uncertain batches.
Read-back can identify a mismatch or support investigation; it cannot reconstruct
an acknowledgement for omitted repeated values.

Report a stable delivery error with session/batch identity, safe timestamp range,
and the next step: preserve the queue and reservations, inspect server evidence,
and resolve the uncertainty before resuming. Do not expose authorization headers
or raw server response bodies. Do not offer a "mark unsent" shortcut based solely
on missing points. If authoritative evidence is unavailable, leave the session
blocked; starting a separately authorized run on fresh tags avoids replay but
does not resolve the original batch.

## Required before the production writer

Verify and document what a successful write response guarantees for every tag
and point in a request, including partial acceptance and validation failures.
Determine whether the API offers durable request identifiers, receipts, or an
idempotency facility; none is assumed here. Without a reliable success contract,
production delivery remains blocked. More automatic recovery requires additional
server guarantees, not a heuristic based on latest retained data.

Use a fake transport to test crashes at each durable boundary, lost responses,
partial acceptance, repeat suppression, conflicts, concurrent ownership, and
unrelated-session progress. A live success test alone cannot validate recovery.
