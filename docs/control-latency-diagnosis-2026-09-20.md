# Control-latency diagnosis — September 20, 2026

The multi-second delay was reproduced and narrowed to specific timing intervals,
but its underlying runtime/operating-system cause is not yet confirmed. Do not
describe it as fixed or attribute it to SQLite, garbage collection or pipe
connection without additional evidence. No deadline was increased and no command
retry was introduced.

## Method and evidence

Used fresh synthetic databases containing 80 completed sessions plus four active
constant/sequence sessions, each observed for sixty seconds. No Historian was
contacted. The new read-only control probe uses the actual wire contract and
separates setup, connection, write and reply wait. Its subprocess residual also
includes launch, pre-Main JIT, output and exit; it is not pure startup latency.

Three initial instrumented runs measured 309 requests:

| Run | Requests | p50 ms | p95 ms | Maximum ms |
| --- | ---: | ---: | ---: | ---: |
| 1 | 107 | 57.9 | 65.4 | 83.5 |
| 2 | 107 | 58.0 | 66.1 | 184.0 |
| 3 | 95 | 59.0 | 278.2 | 3287.6 |

The 3287.6 ms request spent **3219.1 ms waiting for the reply**, with only
3.4 ms connecting and 0.6 ms writing. That establishes a delay after the request
was written; reply wait still includes host waiting/execution, response transport,
and scheduling of the client continuation. It does not isolate server execution.

Another 1951.3 ms request spent **1915.2 ms outside the measured client region**;
reply wait was 10.5 ms and connection 3.8 ms. A third request had 949.5 ms of such
overhead. The observed delays occupy different timing intervals; they may still
share an underlying machine scheduling/resource cause. These measurements do
not establish two independent root causes.

Added bounded `host.slow_operation` warnings for worker rounds, control queue
wait/execution, reply handoff and reply writes exceeding one second. Three further
instrumented runs measured 323 requests with maxima of 98.3, 67.9 and 197.4 ms.
No slow-host warnings were retained. Those runs did not reproduce the earlier
delay, so they cannot clear the host or establish that instrumentation fixed it.
Retained rotating logs can also omit older evidence; absence is not proof.

A separate ordinary-CLI comparison reproduced a **5147.3 ms** maximum among
99 requests (p50 55.8 ms, p95 63.4 ms), with no retained slow-host warnings.
Thus switching diagnostic clients did not explain away the original symptom.
No warning only limits what was observed in the instrumented host phases; client
scheduling and uninstrumented intervals remain possible.

## Bounded native profiling

On macOS, `python3 scripts/profile_control_macos.py --dotnet <sdk-executable>`
runs the same synthetic ordinary-CLI case and arms `/usr/bin/sample` when an
inventory request exceeds 500 ms. It captures at most three one-second pairs of
client/host samples, only for processes created by that case. It waits for and
cleans up its children. Native stacks may not resolve managed method names.
Profiling perturbs timing: these results are diagnostic, not capacity benchmarks.
Reports and traces remain in a uniquely named ignored `.tools/control-profile-*`
directory; native process metadata must be reviewed before any public sharing.
The report distinguishes successful trace writes from failed sampling attempts.
Unsupported platforms receive guidance to use portable `--diagnose` instead.

The first armed profiling run made 108 requests with a 68.9 ms maximum and did
not trigger sampling. The final paired-host/client attempt made another 108
requests with a 73.5 ms maximum and also did not trigger. No native stack capture
is claimed from either run. A clean profiling window does not negate the reproduced
outliers. The collector is saved so the same bounded experiment can be repeated.

## Changes and verification boundary

- Read-only diagnostic executable, built from the current local wire contract.
- Optional `--diagnose` mode with per-request timings and explicit client identity.
- Timing consistency checks and unit coverage, including preservation of outliers.
- Bounded host warning events with phase, milliseconds and troubleshooting guidance.
  No request payloads, values, paths or credentials are recorded.
- A controlled delayed-execution test checks that the normal response still works
  and the warning explains troubleshooting and prohibits inferred replay.

CI exercises the diagnostic load smoke; normal host process verification continues
to exercise the standard CLI. See [verification](verification.md) for the final
test evidence. The [capacity guide](capacity-and-retention.md) provides reproduction
commands, workload details, timing limitations and retained-log truncation rules.

## Next diagnostic decision

Keep the existing timeout and no-replay policy. The next useful evidence is a
runtime scheduling/GC and disk trace captured during a slow reply, correlated with
the phase timings and host warnings. Until one is captured, host round length,
I/O stalls, GC pauses and process scheduling remain hypotheses. Archival does not
resolve this uncertainty and should not be presented as a latency fix.
