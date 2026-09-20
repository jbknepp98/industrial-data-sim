# Bounded simulation worker

This is a finite, foreground backfill runner, not a resident service or production
Historian adapter. It uses a sealed in-memory fake destination and only databases
marked simulation-only. No credentials, URL, or transport selection can enable
production writes. Successful acknowledgements here are synthetic test evidence.

```sh
dotnet run --project src/IndustrialDataSim.Cli -- session start state/simulation.db examples/constant-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- session run-simulated state/simulation.db 100
```

`run-simulated <database> <maximum-rounds> [batch-bytes]` requires an existing
database. Round limits are 1–10000. Optional batch bytes are 128–4194304 and apply
only to this invocation; other runtime limits use their defaults. Repeat compatible
limits when resuming. The first worker supports at most 100 unarchived sessions per
database, including finished sessions, to bound scheduling and response snapshots.
It refuses larger inventories before doing work. Reservations remain local to
one database; do not split overlapping tags among databases to evade ownership.

Each round rotates its first session. Each session receives at most one bounded
generation window followed by at most one batch submission. Delivery immediately
after generation lets other sessions use freed capacity even when the global queue
holds only one batch. Model clocks and seeded values still use simulation time;
scheduling and elapsed wall time do not change the sample grid. The worker yields
between turns for cooperative host cancellation. It does not pace samples in real
time, poll indefinitely, or automatically retry after a no-progress round.

## Outcomes

The CLI returns one bounded JSON envelope with `result.stopReason`, rounds started,
progressing generation turns, acknowledged batch count, message, and up to 100
session snapshots. A round interrupted between turns counts as started, not as a
fully serviced round. `valid: true` means the worker returned an ordinary outcome;
inspect the outcome and exit code to determine whether work finished.

| Stop reason | CLI exit | Meaning/action |
| --- | --- | --- |
| Completed | 0 | Every session is Complete or Cancelled; also applies to an empty database. Cancelled does not mean the original range was generated. |
| Blocked | 4 | A whole round made no generation/delivery progress. Inspect errors, paused/uncertain/failed state, queue/disk limits, and logs. |
| RoundLimit | 4 | The selected limit was reached with unfinished sessions. Inspect state and invoke another bounded run when ready. |
| Stopped | 130 | Graceful stop completed. Checkpoints and queued work remain durable. |

Runtime errors retain the normal CLI error envelope and exit 1; invalid arguments
use exit 2. Storage failures and unexpected programming exceptions are not hidden
as harmless per-session blockage. Configuration-integrity failures stop their own
session while eligible peers continue. Uncertain batches retain payload and
ownership and are never replayed; shared capacity can still block other sessions.
Cancelling sessions drain existing work and end Cancelled. Paused sessions do not
run. The worker does not silently resume or release any session.

## Stop, inspect, control, restart

Use Ctrl+C for a graceful CLI stop. This stops future generation/claims at a turn
boundary, but deliberately lets a currently claimed submission finish through its
normal acknowledgement or uncertainty handling. The worker does not pass the stop
token into an in-flight transport call. For a supplied test fake with a blocked
write hook, graceful stop therefore waits for that hook to finish. A future real
transport must have its own bounded request timeouts.

After the process exits and releases database ownership, use the session CLI to
inspect, pause/resume, cancel, or release sessions. Then invoke run-simulated again
without admitting the existing models again. Live commands cannot open the database
while the worker owns it. This stop/control/restart workflow is the initial control
contract for this finite command. The separate [continuous host](continuous-host.md)
now provides resident foreground scheduling and local live controls; service installation
remains future work. Do not run multiple worker instances against the same runtime object.
A worker object rejects overlapping RunAsync calls.

Forced termination or machine loss uses existing SQLite recovery. Pending work
can proceed after reopen; Sending becomes Uncertain. A second interrupt is not a
force-stop feature. SIGTERM/service-manager integration is not implemented in this
increment. Never delete the owner file or reset uncertainty to force a restart.

## Fake evidence and memory

The default worker fake retains only the latest accepted and retained point per
tag, while still enforcing strictly increasing timestamps during that fake's
lifetime. Memory therefore does not accumulate the entire backfill history.
The library accepts an explicitly supplied FakeHistorian for tests; a caller that
chooses full history remains responsible for that test's memory use. Models,
queued payloads, and batch sizes remain subject to existing runtime limits.

The fake's remote-side evidence is not persisted. A fresh CLI invocation creates
a fresh fake; old local synthetic acknowledgements are not resent or converted
into real evidence. Retain the same supplied fake across runtime reopen when a
test needs to compare remote-side history. This runner exercises local scheduling
and recovery; it cannot establish production exactly-once semantics or serve as
an independent read-back archive across process restarts.

Information logs `worker.started` and `worker.stopped` explain lifecycle and stop
reason. Existing session, pressure, and delivery logs provide troubleshooting.
Abrupt failures may lack a stopped event; SQLite remains authoritative. Logs and
console outage notices remain separate from the CLI's stdout JSON response.

Tests cover one-batch-capacity fairness, round-limit continuation, no-progress
stopping, uncertainty isolation, cancelled/draining sessions, graceful in-flight
stop, already-running rejection, limits, bounded fake history and ordering,
exception propagation, CLI stop/exit behavior, and database reopen. Existing
process-crash tests continue to exercise durable boundaries. Production capacity
and real-time behavior have not been measured by these tests.
