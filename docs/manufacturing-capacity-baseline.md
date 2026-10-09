# Manufacturing capacity baseline — October 9, 2026

Release Phase 2, historical first measurement increment. Subsequent bounded
execution and qualification are documented in [the Phase 2 report](phase-2-qualification.md). Runtime behavior and safety limits
are unchanged. This is not completion of the capacity qualification gates.

## Reproduction

Build the full Release solution. In a fresh process, run each case three times:

```text
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing 60
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing 600
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing 3600
dotnet tools/IndustrialDataSim.CapacityProbe/bin/Release/net10.0/IndustrialDataSim.CapacityProbe.dll manufacturing 259200
```

The last case currently returns exit 1 and `Rejected` with the 250000 node-clock
limit diagnostic. That refusal is a release blocker, not a passing benchmark.
Exit 0 means the bounded measurement completed; exit 2 is usage error and exit 3
is fixture/state access failure. Never interpret any arbitrary rejection as proof
of the node-clock limit: inspect its structured errors.

The fixed eight-tag manufacturing example includes routing, readiness, state,
speeds, noise/faults and both totalizer modes. The probe removes the production
target and uses one-second process and observation clocks so early termination
cannot conceal horizon cost. All configuration is synthetic, transport is the
sealed fake, and SQLite state is unique temporary state removed on exit.
There is no credential, URL, production-mode or arbitrary configuration option.

The probe measures standalone compilation, durable admission, at most three
1000-point generation/fake-delivery turns, and reopen plus horizon inspection.
It checks forward progress, expected total slots, valid session state and exact
checkpoint preservation across reopen. The one-minute case completes in one turn;
longer cases intentionally do not drain their full horizon. Horizon inspection
loads the model twice in the current implementation. Reopen alone would not
measure that reconstruction cost.

Allocation measures process-wide cumulative managed allocations during the phase,
including subsequently collected garbage. It is not retained or peak memory.
Managed heap and process working set are end samples only; database storage is
also sampled. Timing excludes process startup. These single-session measurements
do not qualify concurrency, live IPC latency, HTTP throughput or the seven-day
soak. The CI smoke runs only the one-minute case without speed thresholds.

## Local results

Three fresh processes per horizon on macOS 26.2 ARM64, .NET 10.0.12, Release.
The runtime baseline is `ea29007`; only the new measurement tool differs.
The initial probe fixture incorrectly used a +00:00 suffix, was rejected by the
UTC-Z contract, and was corrected before the twelve reported runs. No failing
fixture result is included as capacity evidence.

| Horizon | Compile ms | Admission ms | Generation turn ms | Fake delivery turn ms | Reopen + horizon ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| 60 seconds | 13.3–15.9 | 2.9–3.2 | 15.9–18.1 | 7.0–7.7 | 3.9–4.6 |
| 600 seconds | 25.7–28.3 | 15.2–15.4 | 14.6–37.6 | 15.3–20.5 | 27.2–29.0 |
| 3600 seconds | 95.4–99.7 | 81.0–82.4 | 78.0–95.1 | 84.7–97.4 | 171.7–175.6 |
| 259200 seconds (72 hours) | Rejected in 6.0–6.9 | Not run | Not run | Not run | Not run |

At one hour each generation/fake-delivery turn allocated about 56.8–57.8 million
bytes (54.2–55.1 MiB). Reopen plus horizon allocated about 114.2 million bytes.
End sampled working set was about 88.4–89.4 MiB. Repeated allocation is therefore
not evidence of an equally large leak. Small-run timing includes JIT effects;
these samples are not a calibrated throughput or latency SLA.

All nine accepted measurement runs preserved their checkpoints; all three
72-hour attempts rejected at the node-clock guard. The Release build had zero
warnings/errors and the existing 811 .NET tests passed. Cross-platform execution
of the new CI smoke is pending publication; do not substitute earlier CI evidence.

## Decision and next implementation increments

Do not raise whole-horizon compilation or observation-storage limits to admit
72 hours. Each Generate/Claim currently reconstructs all process history; longer
horizons would multiply that cost even when handling a small transport batch.
A compiled-model cache might reduce repeated work but would not solve bounded
horizon storage or restart reconstruction, so it is not the primary remedy.

1. Extract a deterministic process stepper from the compiler while retaining the
   current compiler as the small-horizon reference. Check independent expected
   vectors and equivalence for state dwell, previous values, timer memory, batch
   totals/random indices, faults and final target observation semantics.
2. Define versioned process checkpoints covering every stateful field. Commit
   checkpoint, generation cursor and queued output atomically; resume from a
   compatible checkpoint, never from submitted values or readback guesses.
   Partial timestamp rows must not advance the process twice.
3. Generate bounded windows without storing the whole process series. Separate
   structural validation from runtime evaluation; report later expression errors
   safely without claiming the entire future horizon was prevalidated. Preserve
   compatibility for existing generator identity/state or provide an explicit
   migration; do not silently reinterpret saved sessions.
4. Re-run the same probes plus the 72-hour target, add four-session measurements,
   independent stream digests, forced-interruption/restart tests and memory/disk
   qualification. Then investigate control outliers with existing phase timing
   diagnostics under the qualified load.

These are reviewable increments inside Phase 2. Gates V03–V06 remain open. No
live demonstration process, production database, runtime limit or data contract
was changed by this measurement increment.
