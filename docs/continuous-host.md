# Continuous simulation host and live controls

The foreground host continuously services the existing simulation worker and
accepts local lifecycle commands. It remains available when all sessions finish
or no session can progress. This is simulation-only: the destination is a sealed
fake, acknowledgements are synthetic, and no credential or URL enables production
writes. There is no OS service installation, automatic startup, or real-time pacing.

## Start and control

```sh
dotnet run --project src/IndustrialDataSim.Cli -- session start state/simulation.db examples/constant-simulation.json
dotnet run --project src/IndustrialDataSim.Cli -- host run state/simulation.db
# In a second terminal:
dotnet run --project src/IndustrialDataSim.Cli -- host status state/simulation.db
dotnet run --project src/IndustrialDataSim.Cli -- live list state/simulation.db
dotnet run --project src/IndustrialDataSim.Cli -- host stop state/simulation.db
```

`host run <existing-database> [batch-bytes]` holds exclusive SQLite ownership until
exit. Batch bytes default to 1048576; allowed range is 128–4194304. Other runtime
limits retain their defaults. Repeat compatible limits on restart; limits are not
persisted. At most 100 unarchived sessions, including finished sessions, may inhabit a
hosted database. Live admission refuses the 101st before reserving its tags.

| Command | Meaning |
| --- | --- |
| `host status <db>` | Confirm the local host can service controls. |
| `host stop <db>` | Request graceful exit. Wait for the host process to exit before opening SQLite directly. |
| `live start <db> <model-file>` | Validate and send the model contents, then admit it for automatic execution. The host never reads a client-supplied file path. |
| `live list <db> [after-session-id]` | Up to 100 session snapshots, with a continuation cursor. |
| `live status <db> <session-id>` | State, queue totals, errors, and per-tag progress. |
| `live batches <db> <session-id> [after-batch-id]` | Up to 100 batch metadata records, excluding payloads. |
| `live pause\|resume\|release <db> <session-id>` | Apply the existing lifecycle rules. |
| `live cancel <db> <session-id> drain\|discard-pending` | Explicit cancellation policy; uncertainty cannot be bypassed. |
| `live retry-generation <db> <session-id>` | Recheck eligibility using the host's current limits. Stop/restart the host to change limits. |

Use the returned session IDs; examples may have finished before a pause arrives.
`session` commands still open SQLite directly and must wait until the host exits.
Live controls do not fall back to a direct database operation when a pipe fails.
Controls cannot edit immutable configurations, force acknowledgement, resume
Uncertain delivery, or choose a network transport.

## Scheduling and stop boundaries

Each round gives each session one bounded generation window and one possible
submission. The first session rotates across rounds, including repeated bounded
runs on the same worker object. Controls execute between complete rounds. Therefore
a successful pause response means the previous round has finished; generation and
submission remain paused until an explicit resume. Work may finish before a queued
pause is serviced; the resulting lifecycle error explains this rather than claiming
that the pause succeeded. There is no hard control-latency guarantee for a round of
100 complex models.

When idle or blocked, the host waits 250 ms between rounds. New commands are
serviced on the next iteration. This allows disk pressure to recover without a
busy loop. Only worker state transitions are logged, not each polling round.
Failed and Uncertain sessions remain stopped while eligible peers progress.
No automatic replay, resume, release, or cancellation occurs.

Ctrl+C or `host stop` stops future work and lets a claimed fake submission finish.
A stop command reply acknowledges the request; process exit confirms ownership has
closed. Ctrl+C returns exit 130; an ordinary control stop returns exit 0. Forced
termination and machine loss use the existing recovery rules: Pending can proceed,
Sending becomes Uncertain. SIGTERM/service-manager integration remains future work.
Restart the host with the same database; do not re-admit existing models. The fake
retains only latest points per tag in memory and has no persistent remote evidence.

## Local control contract and troubleshooting

The pipe uses `CurrentUserOnly` at both endpoints. There is no TCP listener or
password. Processes running as the same OS user are trusted and can issue controls;
this is not isolation from a malicious process already running as that user.
Use identical absolute database path spelling on both sides; relative paths resolve
against each process's current directory. Symlink aliases are not resolved. The pipe
name is a 128-bit prefix of a SHA-256 digest of the user name and absolute path; paths are not logged.
Do not delete owner files or alias the database to bypass ownership.

Protocol version 1 has an 8-byte little-endian header (body length, response exit
code; request code must be zero) followed by UTF-8 JSON. Request bodies are capped
at 8 MiB to accommodate escaping a model of at most 1 MiB. Responses are capped at
4 MiB. There is one connected client and one queued command; oversized lengths are
rejected before allocating a body. JSON depth is bounded. Invalid or abandoned
connections are closed without stopping the host. A connection has a 30-second
server deadline; clients allow 3 seconds to connect and 35 seconds overall.

Commands are never automatically retried. Once queued, a mutation may commit even
if the client times out or disconnects. A missing reply is an unknown command
outcome, not proof of failure. Inspect `live status/list`, or stop the host and use
`session status/list`, before deciding whether to issue another command. There is
no durable command-receipt or exactly-once control protocol in this increment.

Successful live replies use the existing bounded session envelope, with command
`session.live-<action>`; host replies use `session.host-status`, `session.stop`, and
`session.host-run`. Exit 1 denotes validation/runtime/protocol errors, 2 usage,
and 3 local control/access failures. `valid: true` confirms command success, not
completion of the simulated range. If control is unavailable, check host liveness,
same-user execution, matching paths, pipe permissions, and logs. Never infer
Historian acceptance from a successful live command.

Events `host.started`, `host.progress_state`, and `host.stopped` join the existing
bounded rotating logs. Logs do not contain model payloads, input paths, credentials,
or raw exception text. Abrupt failures may lack a final event; SQLite remains the
authority for recovery. All repository tests use local synthetic data.
