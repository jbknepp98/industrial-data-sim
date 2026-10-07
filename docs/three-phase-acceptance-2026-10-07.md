# Extension, production hosting and manufacturing acceptance

The three requested implementation increments are complete. The evidence below
separates implemented behavior, executed checks and remaining operational limits.
No credentials, certificates, databases or raw production artifacts are versioned.

## 1. Safe horizon extension

Simulation and production commands expose guarded inspection and extension.
Revision-aware format-two archives retain horizon history and batch revision
identity. Forced-process termination checks cover either side of commit; retry
uses the original UUID. Original configuration, timestamps and cursor stay intact.
A proposed longer manufacturing horizon is compiled before the revision commits,
so process limits cannot leave an unusable saved extension. Target-bounded models
require a new session rather than an extension.

Fresh Test acceptance used prefix `Sim.Horizon.20261006T235027-1d5cf3`, published
both windows and read back the final changing ramp at 119. Two batches were
archived and the archive verified. One observation was `Observed`, the other
`ConsistentWithoutNewArrival`; no missing/repeated value caused a retry.

## 2. Production host and restart

The resident host supports same-user admission, pause/resume, cancellation,
release, retry-preflight, status and horizon extension. Health reports separate
process responsiveness from failed/uncertain sessions and durable queue pressure.
Explicit startup configuration offers 0–3 process restarts, default zero; it never
resets a failed/uncertain batch. No OS startup service was installed.

Fresh Test acceptance started October 6 at 23:57 UTC with two sessions under
`Sim.Host.20261006T235703-c45e73`. Both backfilled and continued at current time.
Live pause/status/resume succeeded. At 00:06:54 UTC, there were 1,184 published
batches, zero failed/uncertain sessions and empty queues. Status and graceful stop
took 0.289 and 0.252 seconds respectively. After full process exit, database
integrity was `ok`; saved cursors were preserved and the same database restarted.
At 00:13:30 UTC, both cursors had advanced to 3,054, with zero errors/queued work.
Health response took 0.063 seconds. This is one observed workload, not a general
latency guarantee. The acceptance host is independent of the running packaging demo.

Separate read-only inspection on October 6 at 18:38 UTC found both October 2 demo
publishers still current after roughly four days. Their older running state was
not migrated. These observations supplement, rather than replace, forced-crash
and synthetic transport tests. The older intermittent Windows/macOS delay remains
unexplained; fast October controls do not establish that it is fixed.

## 3. Manufacturing process behavior

Added typed comparisons/Boolean expressions, edges and held conditions, same-tick
and explicit previous-tick dependencies, repeating schedules, prioritized state
machines, seeded noise, freeze/quality faults, time and production-batch totals,
and target/condition termination. A bounded explanation command exposes process
transitions. The process clock is explicit and independent of sampling/batching.

Fresh Test acceptance used `Sim.Manufacturing.20261007T000917-870932`:
`Ready`, `SKU`, `State`, `A.Speed`, `B.Speed`, `Temperature`, `TimeTotal` and
`BatchTotal`. It generated 720 TVQ observations, completed one blind publish and
found non-null current values plus changes. The batch target stopped the process.
The versioned helper uses unique tags and never retries writes. User pattern
review is still `NotReviewed`; observed arrivals are not per-point receipts.

## Review and validation

- Warning-free Release compilation and 789 passing .NET tests locally.
- 26 Python tests passed. Nine example/schema pairs passed the offline verifier.
- Independent verification covered 100 gate scenarios / 3,722 emitted samples,
  72-hour exclusive packaging routing and both additive speed patterns.
- New process tests cover restart/window invariance, random declaration-order
  stability, quality, gates, state/timer boundaries, target grid completion,
  trace limits, safe typed errors and rejected-extension atomicity.
- Review fixed an extension-limit commit hazard, a final target falling after the
  last observation slot, random streams depending on declaration order, and
  inactive schedule timers counting unseen time. Regression cases cover these.

Cross-platform CI for this increment is recorded below when available. Earlier
successful hosted runs belong to their earlier commits and are not new evidence.

## Deliberate limits

The interpreter precompiles a bounded discrete clock; maximum node/expression work
and observation storage are enforced. Existing simple-signal capacity results do
not certify large manufacturing-model throughput. Dependencies are local to one
session. Partitioned archives, indefinite mutable schedules, OS-specific service
installation and an explanation of the old latency anomaly remain outside this
increment. The simulator retains blind-publish safety and forward-only ordering.

Reproduce with the Release solution tests, Python unit discovery and
`scripts/verify_offline.py`. Live acceptance requires explicit
`--write-test-dataset` in `verify_horizon_production.py` or
`verify_manufacturing_production.py`, private connection environment and the Test
Dataset. Never substitute a retained failed run for a fresh acceptance session.
