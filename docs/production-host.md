# Resident production host

`production-host run <database>` owns an existing production database and runs
fair, bounded publish rounds fenced at current UTC. Historical backlog catches
up and then continues live without re-admission or a new model origin. The host
remains available after completion or a blocked session. It does not recreate
Datasets. Initial admission uses the existing `production start` command.

Unlike the simulation host, this command authenticates and writes real data.
It uses the existing connection environment and private CA trust. Stop before
opening the same database with a direct CLI mutation. Same-user local controls
use a distinct production pipe and execute between rounds:

```text
production-host status <database>
production-host stop <database>
production-live start <database> <model.json>
production-live list <database> [after-session-id]
production-live status|pause|resume|release|retry-preflight|horizon <database> <session-id>
production-live cancel <database> <session-id> drain|discard-pending
production-live extend <database> <session-id> <endUtc> <expected-revision> <request-uuid>
```

Only the host needs credentials. Other controls require the same OS user and
exact state path. There are at most 100 unarchived sessions. Tag ownership still
rejects overlap, including when two sessions target the same Dataset. Controls
share the existing bounded protocol, same-user pipe, rotating logs and slow-phase
diagnostics. A pause acknowledgment means the earlier publish round finished.

Health reports last completed round UTC, process-local published-batch count,
failed/uncertain/paused sessions and queued points/bytes. Process responsiveness
and delivery health are separate. Published count resets on process restart;
durable per-session progress does not. Status is not a point receipt. Session status and UTC tag progress remain available
when configuration or horizon integrity prevents optional horizon detail; in that
case `horizon` is null and `horizonError` explains restoration steps. Successful
lifecycle controls remain successful when only this optional detail is unavailable.
Configuration/horizon integrity failures quarantine that session while healthy
sessions continue; storage failures still stop the worker. A long
transport round can delay controls; a timed-out mutation may have committed.
Inspect current state before repeating it; extension retries reuse the same UUID
and arguments. `stop` requests graceful shutdown; wait for process exit before
starting another owner. Sending/Uncertain work is never replayed automatically.

## Explicit startup and restart configuration

`scripts/run_production_host.py <local-host.json>` supervises an existing host.
Store configuration under ignored `.tools` and use absolute executable paths:

```json
{
  "database": "state.db",
  "environmentFile": "../../.env",
  "dotnet": "/absolute/path/to/dotnet",
  "maximumRestarts": 0
}
```

Paths resolve relative to the startup file. JSON contains paths only, no secrets.
The environment file stays private and untracked. `maximumRestarts` is 0–3;
zero is the default. Unexpected process exits may restart after bounded backoff,
using the same state. This does not reset Failed/Uncertain sessions, re-admit a
model, rerun a batch or extend a horizon. Ctrl+C/TERM forwards graceful shutdown
and disables supervisor restart. Logs remain under the database's `logs` folder.

For machine startup, configure a user-owned launchd job (macOS), systemd user
service (Linux), or scheduled task (Windows) to invoke this script with absolute
Python/config paths and a private working directory. Choose **one** restart
owner: do not layer automatic service retries over this bounded supervisor.
Configure output retention and stop deadlines for the expected transport round.
No OS startup service is installed or enabled by the repository or acceptance
scripts. Windows uses the portable host-stop command; supervisor console signals
also depend on the launcher's console/process-group behavior.

## Restart verification

Before an upgrade, gracefully stop and back up the database using the established
shutdown procedure. Preserve immutable models and original batches. Restart the
same database, inspect last round/session cursors and failed/uncertain counts,
and check current Historian values for arrivals/changes. Do not infer that counts
prove complete delivery. Schema version 7 migrates on opening old state: the
older running packaging demonstrations were not migrated during this increment.

The October acceptance report records two concurrent Test sessions, live pause/
resume, graceful restart and observed control timings. This supplements the
existing multi-day demo evidence; it does not resolve the older intermittent
Windows/macOS latency diagnosis or establish arbitrary-load service capacity.
