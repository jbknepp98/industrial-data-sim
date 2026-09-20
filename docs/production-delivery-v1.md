# Production publishing v1

The `production` commands perform real Pulse authentication, read-only preflight,
ordered TVQ publishing and bounded arrival observations. They use a dedicated
SQLite database marked `production`. The existing `session`, `host` and `live`
commands remain simulation-only and refuse production state. There is no mode
conversion and no automatic service startup.

## Configuration and operation

Supply `TIMEBASE_PROFILE`, `TIMEBASE_BASE_URL`, `TIMEBASE_PULSE_URL`,
`TIMEBASE_CLIENT_ID`, `TIMEBASE_CLIENT_SECRET`, and `TIMEBASE_AUDIENCE` through the
process environment. The profile must match each model's `connectionProfile`.
`.env` is an optional local storage convention; the executable does not source it.
URLs must be HTTPS origins, without paths, queries or embedded credentials.
Optional `TIMEBASE_CA_BUNDLE` supplies a PEM root trust bundle. Otherwise system
trust applies. Custom trust still checks certificate validity, server usage and
hostname; revocation lookup is disabled for the local private CA configuration.
Redirects and application-level write retries are disabled. Tokens remain in
memory and renew before expiry; neither tokens nor secrets enter SQLite or logs.

Run the built CLI using these arguments:

```text
production start state/production.db model.json
production run state/production.db 100
production list state/production.db [after-session-id]
production status state/production.db session-id
production observations state/production.db session-id [after-batch-id]
production pause state/production.db session-id
production resume state/production.db session-id
production retry-preflight state/production.db session-id
production cancel state/production.db session-id drain|discard-pending
production review state/production.db session-id accepted|needs-attention
production release state/production.db session-id
```

`start` authenticates and reads Dataset settings, tag inventory and current points.
It rejects an existing timestamp at/after the proposed start, incompatible existing
tag types, case aliases, and a start outside an enabled purge-age window. Settings
and per-tag baseline timestamps commit with admission. The Dataset comes from the
model; `TIMEBASE_DATASET` is used only by the guarded live verification helper.
A confirmed inventory absence permits a new tag. A 404 alone does not: the tested
Historian returns 404 for all-new data queries, so absent tags are excluded only
after a successful inventory read. Both documented arrays and observed User/System
tag groups are supported. Tags are created by the first TVQ write.

`run` serves multiple sessions in rotating order, generating one bounded window
and publishing at most one batch per session per round. Sessions may share a
Dataset only with disjoint tags. The first adapter binds one connection identity
(profile, origins, audience) per database, persists only its digest, and refuses
redirection on restart. Secret rotation does not change that identity. Canonical
paths and coordination across databases/external writers remain operator duties.

The worker supports 100 unarchived sessions, 32 tags per production session, and
1–10000 rounds per invocation. The CLI uses the runtime's default limits: 1000
points/1 MiB per batch and bounded session/global queues; library callers may
supply validated `RuntimeLimits`. Pending data survives reopening. Ctrl+C stops
between operations and allows in-flight publishing/observation to finish. The
production worker is bounded foreground execution, not the resident simulation
host; inspect, pause or cancel after it releases the database. There is no live
production control endpoint or wall-clock pacing/flush-age scheduler yet.

## Publishing and uncertainty

Before each claim, current timestamps are checked again to detect an obvious
external conflict. This is a check, not a lock against racing external writers.
The runtime verifies payload hashes and ordered positions, commits Sending and
submission intent, then performs HTTP outside the database transaction.

Only HTTP 200 with an empty/whitespace body is normal completion under the observed
API contract. It becomes **Published**, updates `published_ticks`, and releases
the completed payload. Batch hash, ranges, positions, attempt outcome and
observations remain. `acknowledged_ticks` stays null in production. Complete means
publishing finished, not that all points were stored or that the user reviewed it.

Timeouts, non-200 responses, malformed/unexpected success bodies and interrupted
Sending become Uncertain. Preserve payloads, ownership and state. The session
stops; unrelated sessions can continue. No missing point, duplicate suppression,
HTTP status or arrival observation authorizes replay. There is no force-ack tool.

A failure during a read-only preflight leaves queued payloads unsent and marks the
session Failed. Correct the reported connection/timeline issue, then explicitly
use `retry-preflight` and run again. That command refuses unresolved submissions;
it does not send data or skip the next preflight. Resume only applies to Paused.

## Arrival and user review

After Published commits, the adapter reads the actual batch time range and current
values. It filters boundary placeholders, compares returned timestamps, values
and quality with generated samples, and recognizes Boolean 0/1 read-back. It looks
for relevant non-null records and changes only when the batch's model values change.
It does not compare submitted/stored point counts or require repeated constants.

Each batch retains Pending, Observed, NotYetObserved, Mismatch or Unavailable
observation status, with counts of matching tags, non-null current tags and tags
with observed changes. These are partial indicators, never acceptance receipts.
A pre-existing current value alone is insufficient. Up to three observation
attempts are separated by one second. Read failure cannot undo Published. If the
process stops after publish and before observation, Pending remains visible for
manual inspection; observation is not automatically reconstructed after payload
release. User review starts NotReviewed and changes only through explicit feedback.
The review command applies to currently retained Published batches, not future work.

Each HTTP operation, including response-body reads, has a 20-second deadline.
Token/publish responses are bounded at 64 KiB; read responses at 4 MiB and data
arrays at 20000 points per tag. Large Dataset inventories may exceed that cap and
require a future filtered/paged inventory implementation. Read errors report safe
codes and next actions without copying service bodies or exception details.
Observation logs report changed conditions; log rotation does not control recovery.

## Verification

Offline tests exercise empty publish responses, transport loss after server action,
crash after publishing before local commit, separate observation failures, renewal,
unsent restart, concurrent sessions, timeline conflicts and mode isolation.
They model HTTP behavior; they do not establish server durability guarantees.

The explicit helper below creates two fresh, uniquely named synthetic sessions in
`Test`. It retains models, state and results under ignored `.tools`. It never retries
writes and is excluded from CI. Set deployment environment variables first:

```sh
python3 scripts/verify_production.py --dotnet dotnet --write-test-dataset
```

The helper additionally requires `TIMEBASE_DATASET=Test`. Review the resulting
patterns before recording user acceptance. Never run it against real process tags.
See [delivery recovery](delivery-recovery.md) and [archive rules](audit-archival.md).
