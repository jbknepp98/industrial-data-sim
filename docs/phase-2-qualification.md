# Release Phase 2 — bounded execution and capacity qualification

October 9, 2026. This phase qualifies a reference workload, not arbitrary-load
performance or the seven-day release soak. Private measurements remain under
ignored local tooling; reproducible synthetic tools are versioned.

## Execution changes

Opt-in `manufacturing.execution: "windowed"` separates structural/initial-tick
validation from subsequent process evaluation. It preserves the existing evaluator
and random identities, executes bounded windows and stores no full-horizon series.
The default precompiled mode remains unchanged for existing sessions and
production-target termination. Windowed `stopWhen`/true `stopOnTarget` are rejected;
fixed-horizon models may still cap totals without ending the whole session.

Schema 8 stores a version-1 process checkpoint bound by checksum to the original
model and flattened sample cursor. Generation commits the process state, queued
payload, tag positions and cursor together. Missing/corrupt state fails the session
without discarding work. Extension retains process state; archives include it and
retain it in SQLite. Older binaries reject schema 8. Existing demonstration
state was not opened or migrated by these tests.

Per-window work is limited to 1,000 additional process ticks; an observation gap
may span at most 1,000 ticks. The 4,096-expression, 16-level and 32-node structural
limits remain, and checkpoints are capped at 1 MiB. Late expression errors discard
the provisional window and preserve its prior committed state. This is intentionally
not a promise to validate every future value at admission.

## Reference machine and method

Local macOS 26.2 ARM64, .NET 10.0.12, Release, 14 physical cores and 24 GiB RAM,
local filesystem state. These are reference-machine results, not a minimum-machine
certification. End/high-water samples can miss short peaks. No GC was forced to
make samples appear smaller. Workload timing excludes artifact build time.

The [original baseline](manufacturing-capacity-baseline.md) measured legacy
compilation separately. New runs use the same eight-tag synthetic example and
one-second process/observation clocks, with early termination disabled. The
four-session load caps queues at 2,000 points per session and 8,000 globally,
delays fake delivery on alternate rounds and reopens every 2,000 rounds. It checks
all final cursors, point/queue accounting and preserved checkpoints. The mixed
case uses eight ramps, a Boolean source with seven gated ramps, eight manufacturing
outputs, and an eight-output production-target model with a two-minute horizon.
The target model completes early; other sessions retain the requested horizon.

Do not interpret synthetic emitted counts as Historian receipts or fake delivery
throughput as HTTP ingestion capacity.

## Measurements

| Measurement | Result | Limit of the evidence |
| --- | --- | --- |
| Legacy one-hour generation/fake-delivery allocation | About 54–55 MiB each turn | Cumulative allocations including garbage, not retained memory |
| Windowed one-hour generation, after first turn | About 2.5 MiB allocation, 6.0–8.7 ms | Small fixed fixture, no HTTP |
| Windowed one-hour fake delivery, after first turn | About 0.73 MiB allocation, 2.3–2.8 ms | No HTTP |
| Windowed one-hour reopen plus horizon | 1.5–1.6 ms / about 0.24 MiB allocation | Stored status/model loading, not a full host startup |
| Four manufacturing sessions, 72 hours, three isolated runs | 8,294,400 points in 24.35–25.43 s | Accelerated fake transport; two reopen cycles each |
| Same runs, sampled working set | 97.9–98.0 MiB | Not an OS-enforced memory cap |
| Same runs, storage high-water | About 37.4 MiB | Batch-heavy backfill, not live audit-growth rate |
| Four manufacturing sessions, ten simulated days | 27,648,000 points in 79.02 s; six reopen cycles | Accelerated execution, not ten days of uptime |
| Ten-day sampled working set / storage | About 97.3 MiB / 114.3 MiB | One run |
| Mixed 72-hour workload | 5,314,256 emitted points in 13.94 s; two reopen cycles | Includes suppression and early target completion |
| Four manufacturing sessions, 60-second host load | 106 status samples; p95 76.6 ms, maximum 204.2 ms | Separate CLI processes, local IPC, fake transport |
| Same host run: pause / resume / stop reply | 93.9 / 58.0 / 54.0 ms | One observed run; no slow-host events |

One initial 72-hour run overlapped the test suite and is excluded from the three
isolated timing runs. It is retained as correctness evidence only. The matrix's
72-hour limit blocker is removed for fixed-horizon windowed manufacturing; the
legacy precompiled limit is deliberately unchanged.

## Real-transport sample and responsiveness investigation

Fresh Test tags and fresh state were used for four sessions / 32 tags, five
minutes of historical input followed by 90 seconds live. This is a bounded Phase 2
sample, not the full release acceptance. User pattern review remains NotReviewed.

The initial run completed all sessions, 304 Published batches, with no session
error. Arrival/change indicators were present for every session. Published timestamp
lag p95 was 1.225 seconds; status p95 was 311.2 ms but maximum was 8,308.6 ms.
Retained host timing logs identify 8,262/8,282 ms worker rounds and 6,574/8,223 ms
control queue waits. The corresponding batches remained NotYetObserved after the
bounded observation attempts: four serialized sessions each incurred two one-second
observation waits. Counts/readback gaps did not trigger replay.

The resident production host now returns to its control loop between complete
session turns, rotating fairly. A turn still finishes its publish and observation
operation; no concurrent writer or interrupted delivery transition was introduced.
The foreground batch worker retains its full-round default. The final fresh-tag repeat completed all four sessions with arrival/change
indicators present for each. Across 112 status requests, p95 was 313.0 ms and
maximum 2,072.2 ms. Across 92 live-lag samples, published timestamp lag p95 was
1.228 seconds. No request deadline was exceeded. This meets the reference targets
and reduces the reproduced maximum from 8.3 to 2.1 seconds without relaxing them.
Both runs retain their evidence; neither has been marked user-reviewed.

This explains the delay reproduced here, not the historical September intermittent
startup/control anomaly. Keep its diagnostics and do not claim an OS/antivirus
root cause from this result. Faulted/slow HTTP operations remain subject to their
bounded deadlines and may delay controls; healthy p95 targets are not hard real-time
or fault-window guarantees.

## Operating guidance and qualification boundaries

- Qualified scope: four sessions, eight outputs each, one-second process and
  observation clocks on the reference machine, with the fixed fixtures above.
  The 100-session/32-tags-per-session safety ceilings are not capacity claims.
- Windowed fixed-horizon models can cover the 72-hour-plus-seven-day duration
  without storing all observations. Target-stopping models retain their finite
  precompiled limits. Do not silently convert an admitted session.
- Plan at least 512 MiB available process memory for this reference workload as
  conservative headroom over measured samples, not a validated minimum on smaller
  hardware. Keep existing queue byte bounds; process memory includes much more.
- Disk planning must include live audit growth, backups, archives and WAL. The
  initial 90-second real run retained 544,768 bytes in 304 batches. That short run
  is insufficient to certify ten-day live storage. Budget 10 GiB of free local
  workspace for qualification, monitor growth/free space, and finalize retention
  and rollover in Phase 3. This is a planning allowance, not a storage guarantee.
- The adapter's preflight/observation overhead is included only in the short live
  sample. No sustained maximum Historian ingestion rate is claimed. Production
  compatibility versions, clean installation and the seven-day soak remain gates
  in Phases 4–5. Current observed arrivals are not per-point acceptance evidence.

## Reproduction

After a Release build, use fresh processes and run timing cases sequentially:

```text
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing-windowed 3600
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing-windowed 259200
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing-load 259200
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing-load 864000
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll mixed-load 259200
python scripts/measure_host_capacity.py --history 0 --seconds 60 --diagnose --manufacturing
```

Use `--dotnet <local-sdk>` for Python tools when needed. Real writes require an
explicit `qualify_manufacturing_production.py --write-test-dataset`, a private
connection environment with TIMEBASE_DATASET=Test and TIMEBASE_PROFILE set, and
available services. Each run creates a unique namespace and retains its state;
never use an uncertain run as a fresh input or automatically replay it.

Regression coverage includes reference/window equivalence across sample/batch
sizes and partial rows; timers, previous values, faults and accumulators; atomic
commit exceptions and actual process kills; corruption quarantine; byte limits;
wall-clock fences; extension/archive continuity; and the production adapter path.
Local completion checks: warning-free Release build, 827 .NET tests and 28 Python
tests passed. Ten example/schema pairs, six invalid schema cases, 100 independent
gate scenarios / 3,722 points and the 72-hour packaging/speed oracles passed.
[CI on implementation commit `8db154e`](https://github.com/jbknepp98/industrial-data-sim/actions/runs/37941382840)
passed Linux, macOS and Windows, including the new windowed admission and mixed
load smoke checks. The final real-transport run left four Complete sessions,
328 Published batches and zero Pending/Sending/Uncertain batches. Its test host
exited after graceful stop. These counts describe local durable state, not a
claim that Historian retained every point.


## Phase outcome

Phase 2 engineering and reference-workload qualification are complete. The
whole-horizon manufacturing blocker is resolved through explicit bounded execution,
not a raised compilation limit. Restart/ordering tests pass, measured queues and
memory remain bounded over the accelerated workloads, and healthy real-transport
freshness/control targets pass the short sample. Phase 3 must now establish the
operational recovery, storage/rollover and backup/upgrade procedures. Phase 5 still
requires the actual 72-hour backfill and seven-day live acceptance on a release
candidate, including human pattern review. No claim of arbitrary-scale capacity,
hard real-time control, minimum hardware certification, or solved September OS
latency anomalies is made.
