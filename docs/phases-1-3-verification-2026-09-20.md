# Operational, archival and production increments — September 20, 2026

## Operational hardening

The repaired native profiler observed 108 requests: median 55.6 ms, p95 60.4 ms,
maximum 84.1 ms. No sample was triggered in that bounded window.

A 900-second synthetic load run with 80 completed and four active constant/sequence
sessions made 1399 inventory requests. Median latency was 57.9 ms, p95 115.7 ms,
maximum 5785.4 ms. All sessions kept advancing, pause/resume checks passed, and
graceful stop completed. Peak sampled SQLite/WAL storage was 146898952 bytes,
inside the 256 MiB experiment budget; final history contained 382900 batches and
382839200 synthetic points with no remaining payloads. These counts describe the
fake runtime, not real Historian retention or ingestion capacity.

The corrected collector read one host log and retained 33 slow-operation events,
including worker rounds up to 6032 ms and control queue waits of 3097 and 2465 ms.
The three largest client delays were mostly outside the measured protocol region;
the largest had 5755.4 ms of process overhead and only 3.1 ms reply wait. This is
better localization, not proof of SQLite, GC or OS scheduling as the cause.
Build/test activity overlapped this run, so it is a mixed-load reliability check,
not an isolated throughput benchmark. Hours/days-long soaks and a root-cause trace
remain pending. No timeout was increased to hide these delays.

The process verifier now tests Windows CTRL_BREAK in an isolated child process
group, allocating a console when a hosted runner lacks one; Unix still tests SIGINT.
A Windows CI result is required before claiming that path verified.

## Audit history

Added schema migration, explicit completed-session export, streamed JSONL history,
write-time SHA-256 comparison after flush/reopen, durable receipts and transactional
pruning. Original identity/configuration, preflight and tag timestamp limits survive.
Tests cover eligible archival, refusal of pending work, changed export detection,
original rows before prune commit, receipts after commit, duplicate-ID rejection,
and backward-range protection after archival. See [archive contract](audit-archival.md)
for export limits and filesystem/backup boundaries.

## Production delivery

The new production mode uses separate Published progress and observations; fake
Acknowledged state cannot cross the execution-mode boundary. Tests cover normal
empty-body success, lost transport response after server action, crash before
local publish commit, read-back failure/mismatch/null current, preflight conflicts,
HTTP 401 uncertainty, token renewal, unsent restart, two disjoint sessions, explicit
preflight retry and connection-identity changes. See [production contract](production-delivery-v1.md).

Live documentation was read over verified TLS. The first live preflight stopped
before any write because querying all-new tags returned 404. The corrected client
uses a successful bounded tag inventory read to confirm absence, rather than
interpreting 404 alone as safe absence.

The subsequent live check published two batches containing 360 generated samples
across two independent sessions and six new Test tags. Each session generated
60 seconds at one-second intervals: a bounded ramp, Boolean constant and string
constant. Both batches are Published with Observed arrival indicators; representative
values/quality and ramp changes matched. No submitted/stored count equality was
used and no user acceptance is claimed. No ambiguous write was retried.

Tag prefix: `Sim.Production.20260920T230523-70290b`, with `.A` and `.B` groups and
`.Temperature`, `.Running` and `.State` suffixes. Requested UTC interval:
2026-09-20 22:55:23 inclusive through 22:56:23 exclusive. Local models, state and
reports remain in the ignored production-smoke directory. User review is NotReviewed.

## Boundaries remaining

- Intermittent multi-second local control latency is reproduced, not resolved.
- Production execution is a bounded foreground worker; resident production live
  controls, wall-clock pacing and time-based flushing are not implemented.
- One connection identity per production database; 32 tags per production session.
- Archive exports are limited to 128 MiB; tombstones remain and no import/restore or
  automatic retention scheduler exists. Process crash tests do not prove power-loss safety.
- Missing observations after an interrupted post-publish check remain visible for
  manual inspection; payloads are not retained solely to replay observations.
- External writers cannot be locked out by local ownership or a read-before-write.

These limitations do not permit weaker ordering, silent checkpoint resets or replay.

## Local verification checkpoint

Release build: zero warnings/errors. All 545 .NET tests and 20 Python tests passed.
Schema verification covered eight examples and six invalid shapes; the independent
gate oracle checked 100 scenarios and 3722 samples. Host process verification
passed on macOS, including SIGINT. Real local TLS tests verified custom-root success,
hostname rejection and untrusted-root rejection. Git diff whitespace and changed
Markdown links passed inspection. Hosted cross-platform verification follows push.

The first hosted run passed Linux/macOS but exposed a Windows-only synthetic TLS
server setup issue: the test's ephemeral private key could not complete the
handshake. The fixture now imports its generated certificate into a temporary
key container compatible with SChannel, retaining the same trust/hostname checks
([.NET issue](https://github.com/dotnet/runtime/issues/23749)). No deployment key
or trust-store change is involved. The final review also added a multi-batch
repeat-suppression test and a distinct ConsistentWithoutNewArrival observation.
