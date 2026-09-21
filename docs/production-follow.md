# Backfill followed by real-time publishing

`production follow <database> <seconds>` runs the real production worker with a
wall-clock fence. The duration must be an integer from 1 through 604800 seconds
(seven days). It catches up as fast as bounded generation and HTTP delivery allow,
then waits for samples to become due on the original model's sampling grid.

The full model, its start, exclusive end and checkpoints remain unchanged across
catch-up, live operation and restart. Generator clocks use simulation time;
wall-clock UTC controls eligibility only. A rollback waits without rewinding.
A forward clock jump catches up the intervening slots. Correct host clock/time
synchronization matters. The invocation duration uses monotonic elapsed time.

Generation never consumes the first future slot. This also works when a batch
ends partway through one timestamp's tag row. Pending batches created earlier
are fenced again before delivery: a batch containing future points waits whole;
it is never split, rehashed or sent early. Already Published work is not replayed.
Independent sessions receive rotating bounded turns. Existing `production run`
remains unpaced and can intentionally publish a future finite model; use `follow`
for this demonstration and its restarts.

At live speed, a newly due partial batch is published immediately. Waiting polls
at one second; actual delivery latency also includes generation, network and
arrival observations. This is not a real-time latency guarantee or an additional
flush-age batching policy. No in-flight write is interrupted by graceful stop.

The process owns the database until it stops. Use Ctrl+C (SIGINT on POSIX) for a
graceful stop; inspect `production status` and `production observations` after
it exits. Start `follow` again against the **same database** to continue. Do not
re-admit the model, create a replacement session or clear uncertainty. Missing
arrival points and HTTP errors never authorize automatic replay.

Exit 0 means all sessions completed; 4 means the duration expired or work is
blocked; 130 means graceful interruption. Failed/Uncertain/Paused-only state
returns Blocked. Ready state with no due work waits. Other ready sessions may
continue while a failed session remains visible in status/logs.

This is foreground execution with a bounded invocation and a finite model end.
It does not install a system service, restart after a reboot, extend `endUtc`, or
provide live production IPC. A background process started by an operator/agent
continues only while the machine/process runs. At the demonstration's scheduled
end, publishing completes. Longer or indefinite service operation needs an
explicit follow-up; do not silently edit a persisted model to extend it.

`scripts/local_production.py --dotnet <path> --environment-file .env <arguments>`
is a convenience launcher for an ignored local environment file. It accepts only
reviewed connection variable names, never evaluates shell syntax or prints their
values, and replaces itself with the CLI so signals reach the publisher. Without
`--environment-file`, only the current environment is used. A 64 KiB file limit
bounds this convenience input. Credential files and runtime artifacts stay ignored.
