# Blind publishing, observation, and recovery

## Owner decision — September 19, 2026

Production delivery will use **blind publishing**. The project owner clarified
that Historian provides no application-level success message and stored counts
cannot verify submitted counts. User feedback verifies whether the resulting
simulation is what was intended. Automated checks must still look for arriving
data, non-null current values, and expected changes as indicators of success.
We no longer require a durable per-batch acceptance guarantee before implementing
the production adapter. The adapter remains unimplemented; this decision changes
the plan, not the existing simulation-only executable or its database semantics.

The owner also confirmed first-in-wins per tag/timestamp: a duplicate is ignored
even when its value or quality differs. This is owner-supplied contract information;
the linked Atlas validation page is not evidence for this Historian behavior.
Our engine still requires strictly increasing timestamps and prohibits historical
inserts and automatic replay. Duplicate handling is not permission to retry an
uncertain batch or proof that its intended value replaced an existing point.

## Separate the evidence

| Proposed production state/evidence | Meaning |
| --- | --- |
| Pending | Payload and generator checkpoint are durable; no submission intent yet. |
| Sending | Intent committed before HTTP; the request may take effect. |
| Published | The request completed normally under the adapter's HTTP policy, including the expected empty response. This records transport completion, not confirmed per-point acceptance, retention, or server durability. |
| Uncertain | Timeout, disconnect, interrupted Sending, or an outcome that leaves submission ambiguous. Preserve work and stop that session. |
| Arrival observed | Reads found relevant session-tag data in the submitted simulated range. This is partial observational evidence. |
| Intended behavior reviewed | The user reviewed the output/pattern and supplied feedback. This remains separate from transport progress and does not authorize replay. |

`Published` and the observation/review fields are design terms, not implemented
schema states. The current fake-only runtime's `Acknowledged` state retains its
synthetic whole-batch meaning. Do not relabel or reuse that database as production
evidence. A production schema/mode boundary must distinguish the two explicitly.

Persist publish progress before releasing a normally completed request from the
active queue. Preserve its range, hash, attempt outcome, and observational/review
status. Final payload retention must be defined with the adapter implementation;
uncertain payloads must remain available. Completion should say publishing finished
and report observations plus user-review status, never claim every point was stored.

Blind publishing does not mean ignoring HTTP errors. No application success body
is required. Detect authentication, TLS, HTTP, connection, and timeout failures;
use safe diagnostics. An expected empty response is normal, not malformed. Any
unexpected or unsuccessful outcome without a proven no-write guarantee must not
silently advance Published or authorize retry.

## Automated arrival and pattern checks

1. Capture a read-only baseline for the session's own tags before publishing.
   After bounded publish intervals, query those tags for current/latest values
   and for data in the actual submitted simulated range. Historical backfills
   must not be tested solely against the current wall clock or latest endpoint.
2. Confirm relevant records have usable timestamps and non-null values; reject
   empty responses and placeholder records as arrival evidence. A pre-existing
   non-null current value alone does not demonstrate this session's arrival.
3. Compare observed values, types, quality, and representative transitions with
   the generated model where available. Report mismatches with safe context and
   a useful next action. Do not require equal submitted/read-back counts.
4. Expect changes only where the model emits a change. Constants, bounded-ramp
   plateaus, and closed gates can legitimately remain unchanged or emit nothing.
   Allow a bounded observation delay before reporting missing expected activity.
5. Dataset diagnostic write activity can corroborate observations, but other
   sessions may contribute and periodic System tags change independently. Neither
   proves that this session's data arrived. Never treat a Dataset-wide count as
   a batch receipt or use unrelated activity to pass the check.
6. Report publish progress, observations, anomalies, and user-review status
   separately. Persistent missing expected data, nulls, or mismatches should
   surface NeedsAttention and pause further affected-session publishing under
   an explicit monitor policy. They must never cause automatic resubmission.

Monitor cadence, observation grace period, and production status/schema details
must be specified and tested in the next adapter/monitor increment. Observation
reads remain bounded and cannot affect deterministic generation or reorder writes.
User feedback confirms intended process behavior; it is not a server receipt for
omitted repetitions and does not erase an interrupted request's uncertainty.

## Restart and ordering

Retain one local database owner, atomic tag reservations, immutable models, exact
queued payloads, and separate buffered/submitted/published progress. Retain tag
ownership through pause, failure, and uncertainty. External writers and profile/
path aliases remain operational constraints; local ownership cannot exclude them.

Commit Sending before HTTP and never hold a SQLite transaction across network
I/O. A crash after Sending, including after the server acted but before Published
was committed, becomes Uncertain. Stop that session, keep its payload/reservations,
and allow unrelated sessions to proceed within queue limits. Never resend an
uncertain batch or its missing suffix based on read-back. Known-unsent Pending
work can proceed after restart. Logs and arrival indicators are not recovery
checkpoints. User-directed resolution must be explicit; no force-ack shortcut is
introduced by this decision.

## Public documentation reviewed

Reviewed the owner's linked public site on September 19, 2026:

- [Validation Rules Reference](https://flow-software.github.io/Timebase-Docs/#validation-rules-reference)
  concerns Atlas domain compilation, not Historian TVQ write acceptance.
- [Historian sizing guide](https://flow-software.github.io/Timebase-Docs/#historian-data-sizing-guide-and-best-practises)
  describes change-based storage, supporting the decision not to compare counts.
- [Dataset diagnostics](https://flow-software.github.io/Timebase-Docs/#system-tags-dataset-diagnostics)
  describes State, Writes, and Writes.Late as operational signals.
- [Historian REST API](https://flow-software.github.io/Timebase-Docs/#historian-rest-api)
  describes current and time-range reads and multi-tag TVQ publishing. Its statement
  that older timestamps are ignored differs from our earlier development-instance
  observations. Keep our stricter forward-only rule and do not infer identical
  behavior across versions/settings. These pages do not establish per-point receipts.

## Historical documentation review — September 18, 2026

The investigation below explains the former acceptance-contract blocker. The
owner's blind-publish decision above supersedes that requirement; these questions
are optional investigation, not prerequisites to implementing the adapter.

Read the development Historian's `/api/v1.json` over verified TLS. The document
identifies itself as Timebase Historian API, version `v1`; that is an API version,
not an identified server build. The write operation
`POST /api/datasets/{dataset}/data` declares a required JSON object mapping tags
to arrays of TVQ and only this response:

```json
{"200":{"description":"OK"}}
```

The operation declares no response body, receipt, per-point result, or idempotency
parameter. A full specification search found no descriptions containing
idempotency, atomicity, partial acceptance, receipts, durability, or transactions.
This establishes a documentation gap, not proof that the server lacks those
capabilities. Existing successful read-backs demonstrate retained representation
for those test runs; they do not resolve crash durability or partial acceptance.
No writes or failure-injection experiments were performed in this review.

Before production delivery, obtain authoritative answers tied to the server build:

1. Does HTTP 200 mean every submitted point was accepted, subject only to defined
   repeat suppression, or can points/tags be skipped without an error?
2. Is a multi-tag request atomic? Can any error response follow a partial write?
3. At what point is acknowledgement durable across a server/process restart?
4. Can a client supply an idempotency key or query a durable submission receipt?
5. Which failures, if any, guarantee that no points were accepted?

Controlled mixed-validity and restart experiments on fresh test tags can supplement
those answers, but finite tests do not establish a universal atomicity guarantee.
Until the contract is established, retain the conservative policy above and do
not advance a production acknowledged checkpoint based solely on the word "OK".
