# Capacity baseline and retention requirements

This increment measures the existing simulation-only runtime. It does not
measure Historian ingestion, HTTP latency, production service capacity, or the
maximum safe production session count. No archive or deletion command is added.

## Reproduce

With a .NET 10 SDK, from the repository root:

```sh
dotnet build IndustrialDataSim.slnx --configuration Release
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll constant 100
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll constant 1000
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll gate 100
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll gate 1000
```

Run each case in a fresh process and repeat three times. The probe emits JSON
with safe platform metadata and numeric measurements. Temporary databases are
removed on exit. It never accepts deployment configuration or contacts a server.
Each case has four disjoint sessions sharing a dataset, three tags per session,
seven days of one-minute samples, and batches capped at 100 or 1000 points.
The gate fixture is stretched over seven days to exercise both suppression modes.
Expected generated counts are 120960 constant points and 84672 gate points.
These are synthetic local counts, never production delivery evidence.

The probe admits sessions before timing generation/delivery. It delivers one
batch per session every fourth generation round, exercising backlog against
two-batch session queues and an eight-batch global queue. This is scheduling
pressure, not simulated network delay. Completion, cursor positions, point
counts, queue point bounds, fake acknowledgement, and payload pruning must pass
before a report is emitted. Runtime tests separately exercise byte limits,
crashes and uncertainty. Every run is bounded to five minutes/10000 rounds.

## Reading the metrics

- Throughput includes instrumented generation, SQLite transactions, fake delivery
  and queue inspection. It excludes admission, final audit enumeration and cleanup.
- Maximum round duration includes measurement overhead. Status timing measures a
  direct runtime query; neither metric measures live IPC response latency.
- Managed memory and working set are sampled after each round and can miss peaks.
  Process peak working set includes startup and JIT when supported; null means
  the platform did not provide the metric. Managed bytes exclude native SQLite.
- Storage high-water includes the database, WAL and shared-memory file sampled
  each round. Closed size is measured after checkpoint on disposal. Both include
  configuration, indexes, allocated/free pages and audit records. Bytes per batch
  is an amortized database measure, not the size of one audit record.
- Nonprogressing generation turns include queue pressure and finished/draining
  sessions. They are not an error count.

## Initial local results — September 19, 2026

Three fresh-process runs per case on macOS ARM64, .NET runtime 10.0.12 / SDK
10.0.401, Release. Ranges describe these runs only; they are not capacity limits.

| Pattern | Batch points | Emitted points | Batches | Points/s range | Closed DB bytes | Peak sampled working set MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Constant | 100 | 120960 | 1212 | 90428–91184 | 1400832 | 78.9–79.6 |
| Constant | 1000 | 120960 | 124 | 245144–257991 | 663552 | 83.6–84.3 |
| Gate | 100 | 84672 | 848 | 85177–86535 | 983040 | 78.2–78.8 |
| Gate | 1000 | 84672 | 88 | 196236–208469 | 729088 | 85.4–85.5 |

All twelve runs passed their correctness assertions. Global queued points reached
the configured 800/8000 cap without exceeding it. Storage including WAL peaked
at 4.66–5.31 MiB. Sampled managed memory peaked at 9.42–11.16 MiB; the OS process
peak counter was unavailable. Longest measured round was 26.9 ms and longest
direct status query 1.88 ms. Each run lasted only 0.41–1.34 seconds, so this is a
short reproducibility baseline, not a soak test or a live-control latency test.

Larger batches reduced transaction count and total database allocation here while
raising sampled memory. Database bytes per batch increased because allocated
pages/free space are amortized over fewer batches; do not extrapolate that ratio
linearly to long-term audit growth. These measurements support retaining bounded
batching and measuring history growth next, not enabling automatic deletion.

## Sustained host and retained-history probe

After the Release build, run each case separately:

```sh
python3 scripts/measure_host_capacity.py --history 0 --seconds 60
python3 scripts/measure_host_capacity.py --history 20 --seconds 60
python3 scripts/measure_host_capacity.py --history 80 --seconds 60
```

Use `--dotnet <sdk-executable>` for a private SDK. Allowed observation windows are
5, 30 and 60 seconds; CI uses five seconds without pre-existing history. Each
case has a new temporary database. Historical sessions generate seven days of
three constant tags sampled each minute, then retain their completed metadata.
The 20/80-session cases verify exact local counts and payload pruning before
starting the active workload. Zero history reports zero storage because no
database exists at that point; it is not an empty SQLite database measurement.

Four active sessions (two constant, two staircase/random sequences) use a
30-day horizon sampled every 10 ms so they remain active throughout observation.
The probe checks each session advances between inventory samples and tests pause
stability, resume and graceful stop. All requests use separate CLI processes and
the real same-user control endpoint. Reported latency includes process startup,
IPC, host round-boundary wait, response serialization and exit. p50/p95 use
nearest rank; maximum and sample count accompany them. Pause/resume/stop are
single observations, not distributions. Candidate cursor advance is local
generation progress, never a production receipt or a count of retained values.

Storage is sampled after inventory reads with a 256 MiB stop budget, which is a
probe limit rather than a runtime quota. Historical generation has a three-minute
command timeout; individual controls have a 40-second subprocess timeout. Only
read-only host readiness is retried. A failed or lost mutation response aborts
the probe. Failure cleanup can kill this synthetic child and deletes its temporary
state; this is test cleanup, never the production recovery procedure.

Audit inspection waits until the owner exits, refuses a nonempty WAL, and reads
the checkpointed main database immutably without creating sidecars. This fixed
schema query is test tooling, not a supported production database API. Tests
check that inspection leaves files unchanged and refuses outstanding WAL.
After-stop payloads may belong to Pending work; they are reported rather than
mistakenly treated as a pruning failure. These minute-long runs are still not
hours/days-long soak tests or measurements with a slow network transport.

The worker now skips execution turns for Complete/Cancelled sessions already
identified in its round snapshot. Their records still appear in inventory and
count toward the 100-session cap. Terminal states cannot resume, so this avoids
redundant generation/delivery database calls without altering recovery or
ownership. A regression test checks preserved released history and new-session
completion. This optimization does not establish a maximum control latency.

### September 19 sustained observations

Local macOS ARM64 / .NET 10.0.12. One 60-second run per history size before
terminal-turn skipping, then two repeats at 80 finished sessions after it.
Historical payloads were pruned in every case. All active sessions progressed;
pause stability, resume and graceful stop passed.

| Finished sessions | History batches | History DB bytes | Inventory p50 / p95 / max ms | Observed cursor advance |
| ---: | ---: | ---: | --- | ---: |
| 0, before | 0 | 0 (not created) | 54 / 59 / 70 | 44904000 |
| 20, before | 620 | 487424 | 56 / 293 / 6569 | 20648000 |
| 80, before | 2480 | 1724416 | 59 / 129 / 4891 | 17136000 |
| 80, after, repeat 1 | 2480 | 1724416 | 57 / 63 / 73 | 30300000 |
| 80, after, repeat 2 | 2480 | 1724416 | 58 / 164 / 4906 | 28252000 |

Observation windows can overrun while a bounded request completes: the final
repeat lasted 65.2 seconds. These cursor totals span the first-to-last inventory
samples, not precisely the entire window; do not present them as calibrated
throughput. One or two runs cannot separate scheduler effects from machine load.
The targeted optimization removes known redundant calls and progress improved
in both repeats, but **multi-second control outliers remain unresolved**. Next
diagnostics should distinguish client startup, pipe connection, host wait and
storage/GC pauses before changing production timeouts or promising an SLA.

Across the five runs, maximum sampled DB+WAL storage was 13.2–21.6 MiB. The
no-history run finished with 45459 batches/attempts and an 18567168-byte database
after processing 45459000 synthetic points. This demonstrates retained audit
growth despite payload pruning. The 20-to-80 finished-history difference was
1236992 bytes for 1860 additional batches plus session/tag/configuration records;
it is not a universal per-batch storage estimate. Measurements justify monitoring
audit growth and implementing a verified archive, not an automatic deletion age.

## Retention design boundary

Pending, Sending and Uncertain work must retain its payload and recovery context.
Never delete unresolved work to meet a storage budget. Logs cannot substitute for
SQLite state. Preserve per-tag timestamp high-water marks and ownership semantics
even after a finished session is archived; archive must not permit older reuse.

For finished sessions, a future explicit archive operation should preserve the
configuration hash/version, final session and tag positions, immutable batch
hashes/ranges/counts, delivery attempts/outcomes, and user-review/arrival evidence
when production delivery exists. Export a versioned manifest with checksums,
verify it durably, and only then transactionally mark/prune eligible audit rows.
Crash recovery must distinguish incomplete archive from completed pruning.
Failed archive must leave source records usable and return an actionable error.

No age-based or count-based deletion default is justified by these short runs.
Keep the present metadata-retention behavior and 100-total-session host limit
until archive semantics, listing/pagination, restore/inspection tooling, and
failure tests are implemented. Do not manually delete finished sessions to bypass
the cap. Keep log rotation separate from audit retention.

Next measurements should run longer workloads with increasing finished-session
history, richer sequences, and sustained backpressure. Measure real live-control
latency under load and production HTTP behavior once the blind-publish adapter
exists. Use those results to propose explicit storage warnings and archive
budgets. Initial batch-size measurements alone must not change runtime defaults.
