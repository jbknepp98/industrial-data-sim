# Version 1.0.0 acceptance matrix

Scope baseline: October 9, 2026. Read with the
[release specification](release-1.0.0-specification.md). Phase numbers here are
release phases, not milestones from the original implementation plan.

Evidence states: **baseline** means prior tests exist but exact-release validation
is still required; **open** means release qualification remains. No row is a
production certification merely because unit tests pass.

| ID | Gate / pass condition | Verification and evidence | Phase | State |
| --- | --- | --- | --- | --- |
| V01 | Explicit scope, workload targets, compatibility policy and deferred features | This specification and matrix reviewed against current contracts/code | 1 | Defined |
| V02 | Four sessions / 32 total tags progress fairly; conflicting reservations fail atomically | Deterministic tests plus measured mixed-model workload; publish per-session progress | 2 | Reference load qualified; repeat on RC |
| V03 | 72-hour manufacturing backfill and seven-day live horizon fit safe execution limits | Measure admission/reconstruction/extension cost, memory and deterministic stream digests; resolve current precompilation limit | 2 | Windowed execution qualified; RC soak open |
| V04 | Backpressure bounds point/byte queues without dropping or changing pending payloads | Slow scripted receiver, measured backlog drain, fairness, peak memory and disk measurements | 2 | Reference queues qualified; RC repeat open |
| V05 | Healthy live lag/control targets in specification met | Separate process startup, IPC, round wait and transport timings; investigate known multi-second outliers; record p95/max/sample count | 2 | Reference measurements complete; RC qualification remains |
| V06 | Supported memory, disk headroom and throughput documented | Repeated mixed-model runs on recorded reference hardware and exact stack; include preflight/readback overhead | 2 | Reference measurements complete; RC qualification remains |
| V07 | Per-tag ordering and deterministic behavior survive interruption | Fake/scripted transport and forced-crash tests; compare independently generated per-tag streams across seeds, gates, sequences, faults and targets | 3 | Baseline |
| V08 | Ambiguous publish stays Uncertain with no automatic replay | Fail before/after claim, during request and before local finish; prove retained payload, ownership and attempt evidence | 3 | Baseline |
| V09 | Authentication/TLS/read failures explain corrective action and preserve state | Expired/rejected token, malformed response, unavailable service and trust failure drills; GET refresh only, no POST retry | 3 | Baseline |
| V10 | Retention changes/expiry and external timestamp conflicts prevent unsafe known-unsent publish | Restart/extension/settings-change tests; prove queue preserved and explicit recovery path | 3 | Baseline |
| V11 | One damaged session cannot terminate healthy work; storage damage is not hidden | Ready/Draining/Cancelling integrity cases, resident-host responsiveness, storage failure tests | 3 | Baseline |
| V12 | Backup restoration and upgrades preserve durable evidence and forward-only behavior | Clean backup/restore rehearsal, schema migration and old-binary rejection; account for writes made after a backup before resuming it | 3 | Open |
| V13 | Disk pressure and long-lived audit growth have a safe operational path | Measure active/finished history, verified archive, planned rollover with continuity, interruption during export; no unresolved pruning | 3 | Open |
| V14 | Clean installation/startup/shutdown/restart works on each declared platform | Build release artifacts, checksums, dependency inventory; fresh-machine runbooks and one configured restart owner | 4 | Open |
| V15 | Credential handling and diagnostics meet public-repository standards | Dependency review, permissions/TLS checks, artifact scan, safe actionable errors and bounded logs; no values/configuration in operational logs | 4 | Baseline; release review open |
| V16 | Exact Historian/Pulse build and platform combinations qualified | Record nonsecret versions/settings and test protocol behavior on deployment candidates; publish compatibility table | 4–5 | Open |
| V17 | Backfill becomes live without re-admission or resetting pattern clocks | Fresh synthetic tags, 72-hour backfill, seven-day live run; exclusive routing, speeds, quality, gates and both totalizer modes | 5 | Earlier demos; RC run open |
| V18 | Release candidate survives planned stop/restart and horizon extension | Record cursors/hash identity before/after, forward timestamps and arrival indicators; include host controls under workload | 5 | Earlier tests; RC run open |
| V19 | Intended data pattern accepted by user | Review trigger/secondary tags, SKU routing, speeds, faults and totals; record accepted observations separately from HTTP progress | 5 | Open |
| V20 | Documentation describes shipped behavior and every release failure has a useful diagnosis | Final code/error/docs audit; current handoff and examples agree with executable commands; historical claims clearly dated | 6 | Open |
| V21 | Exact release source and packaged artifacts pass final checks | Windows/Linux/macOS matrix for declared support; clean-source build, installed artifact smoke, checksums and version output | 6 | Baseline CI; RC open |
| V22 | Release approved and reproducible | No unresolved blockers; final notes/limits, evidence index, immutable tag and matching artifacts | 6 | Open |

## Evidence record requirements

For each run record gate IDs, source commit, artifact identity, OS/architecture,
runtime, reference hardware, workload/schema/generator versions, seeds, tick and
sample intervals, batch/queue settings, durations and test outcome. Live runs
also record nonsecret Historian/Pulse versions, retention settings and unique
synthetic tag namespace. Preserve failures and exclusions alongside successes.
Private endpoints, credentials, certificates, database copies, values and raw
logs stay in ignored local storage; publish only sanitized summaries and reusable
synthetic verification logic. Record the evidence location without private paths.

Publish completion, arrival indicators and human pattern review are separate
fields. Local generated counts/digests establish simulator behavior; Historian
count equality is never required and absence is never authority to replay.

## Sequence and completion rule

Complete Phase 2 before setting final capacity claims or beginning the release
soak. Operational and packaging work can proceed independently where it does not
depend on those measurements. Run acceptance against the actual release candidate,
not an older demo binary. Code changes require the affected checks to be repeated.

The current baseline passed all platform CI at `ea29007`; that does not close the
open qualification rows. Phase 1 produces this matrix without changing runtime
limits, upgrading live state or creating production writes.

Phase 2 evidence: [bounded execution and qualification report](phase-2-qualification.md).
Reference p95 targets passed without removing outliers. Operational recovery,
clean-machine compatibility and full live soak remain in their assigned phases.
