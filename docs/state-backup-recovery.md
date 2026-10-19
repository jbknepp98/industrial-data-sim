# State snapshots, restart and disaster recovery

There are two distinct recovery paths. **Normal restart uses the latest original
state database** and continues its exact clocks, queued payloads and tag history.
**Disaster restore uses an older snapshot as evidence**, quarantines every old
session permanently and permits new simulations only with fresh tag names.

A backup may predate successful or uncertain Historian writes. Its Pending rows
cannot prove non-delivery after the snapshot. First-in-wins, counts, current-value
reads and user pattern feedback never authorize replay. Version 1.0 deliberately
provides no same-tag disaster continuation when the latest durable state is lost.
The fresh-namespace policy is the conservative engineering default, not a new
claim about Historian acceptance guarantees.

## Snapshot and verify

Stop the host gracefully and wait for it and any workers to exit. Use canonical
local paths, never aliases or a copied owner file. With the built CLI:

```text
production backup <database> <private-directory>
production verify-backup <snapshot> <trusted-sha256>
```

Both commands also exist under `session` and report the actual source mode.
They require no credentials or network access. Backup uses the runtime owner
lock, reads without migration/startup recovery, includes committed WAL content
through SQLite's backup API, checks integrity and flushes a new file before
renaming it `<backupId>.evidence.sqlite`. Supported backup schemas are 1–10.
Save the JSON receipt separately: it records source schema, byte length and
SHA-256. A snapshot is private data, including models, tags and retained values.
Store it outside version control with suitable local permissions and backups.
Encryption and automatic expiry are not provided.

Verification uses the original trusted receipt's digest, never a new digest of
a suspect file. It checks the hash, execution guard, standalone journal mode,
SQLite integrity, foreign keys and execution mode. It reads without creating
logs or owner files. Keep the evidence directory free of concurrent writers or
renames. Unexpected WAL/SHM/rollback-journal sidecars cause refusal; preserve them
for investigation rather than deleting them to make verification pass.

Verification proves local integrity relative to the trusted digest. It does not
authenticate an untrusted receipt, validate every model/checkpoint, establish the
newest state, or prove Historian acceptance. A `.partial` file is never a completed
snapshot or restore. The runtime rejects paths ending `.partial`; do not rename
them to bypass the guard. Interrupted backup publication may leave an orphan
completed file without a receipt. Preserve it and create a new snapshot from
original state if available; an absent receipt is not a successful backup.

Evidence snapshots retain reserved schema version 1000008. Runtime execution is
blocked, including in older executables that reject unknown schemas. Copying or
renaming the file does not authorize running it. Never edit schema markers.

## Normal restart and upgrade

1. Confirm that the original latest database is available. Keep its WAL and SHM
   files together; do not replace just the main file with an older copy.
2. Stop all writers for the database. Preserve an evidence snapshot and receipt.
3. Run the new executable on the original database. Schema migrations are
   transactional; schema 10 adds recovery quarantine records after schema 9's
   history-segment receipts. Older executables reject migrated state.
4. Inspect status, horizon and recovery errors before starting the host. Ordinary
   interrupted Sending becomes Uncertain. Pending remains eligible only in
   original state, subject to existing preflight/retention/ordering checks.
5. On migration failure, preserve original state and evidence. Diagnose the safe
   error and use a corrected compatible executable. Do not lower the schema or
   overwrite original state with a backup as an upgrade rollback shortcut.

The original session's deterministic continuity is preserved by this path.
Credentials stay external; connection identity checks still apply. Existing
archive files must be retained with their database receipts.

## Disaster restore into a new database

If newest state is lost or unusable, stop all old writers and preserve available
state, WAL, archives, snapshots and logs. Select a trusted snapshot/receipt:

```text
production restore <snapshot> <trusted-sha256> <source-schema> <private-directory>
production recovery-info <new-database>
production list <new-database>
```

The same commands exist under `session`. Restore currently supports source
schemas 8–10; earlier evidence remains verifiable but requires a compatible
migration investigation. Supply the source schema from the original receipt.
The command checks table compatibility, authenticates the copied bytes again,
and creates a uniquely named `recovered-<id>.sqlite`. It never overwrites the
source or another database and never publishes TVQ data. Keep its output receipt,
which identifies the new filename and required `Recovered.<id>.` tag prefix.

Within one transaction, restore records prior session status/error fields,
quarantines every old session and activates schema 10. Original models, cursors,
process checkpoints, payloads, batch states, attempts, observations, progress,
ownership and archive receipts remain. Quarantined Sending is retained as
historical evidence, not processed by ordinary startup recovery. Completed and
previously archived sessions are also quarantined; archived IDs remain directly
inspectable even though normal list hides archived sessions.

All historical-session mutations fail with `backup.session_quarantined`, including
resume, discard, release, re-generation, publishing, review changes and archival.
There is no unquarantine or force-ack command. Status, batches, progress, horizon,
observations and archive verification remain available. `recovery-info` reports
the quarantine count and required namespace. Historical payloads remain on disk
and visible in their session's queue totals, but are excluded from **active**
global queue budgets so they cannot prevent fresh work solely by occupying that
budget. Ordinary Uncertain work is not excluded.

New sessions must use the generated prefix on every output tag and must use new
session IDs. Update their local tag references consistently. Admission/preflight
and connection identity checks still apply. This is a new simulation, with an
explicit new model start/initial state; no continuity on original tags is claimed.
Ensure other databases/external writers do not use the new namespace. The existing
100-unarchived-session host limit includes visible quarantined sessions; for a
full database, preserve it for inspection and start a separate fresh database
with a new, disjoint namespace instead of deleting evidence to fit the limit.

A lost restore response may leave a completed, quarantined database. Inspect the
output directory and `recovery-info` before repeating. Repeating restore creates
a separate database and prefix, never overwrites the first. Crashes before the
transaction leave guarded evidence; crashes after it leave quarantined state.
Only the completed filename is operationally valid. Final publication flushes
file/directory metadata on supported local filesystems; storage must honor those
flushes. No software test proves power-loss survival for every device.

## Failure and storage guidance

- `backup.owner_unavailable`: stop the other owner or check directory access;
  never delete a live owner file.
- `backup.checksum_mismatch`, `backup.integrity`, `backup.sidecar`: preserve all
  evidence, locate an intact copy and original receipt, then verify again.
- `backup.restore_schema`: use the trusted source version; do not guess or edit.
- `backup.restore_storage`, `backup.restore_durability`: inspect partial/completed
  artifacts, permissions and free space before another attempt. No historical
  publishing is permitted by any outcome.
- `backup.fresh_namespace_required`: inspect recovery-info and rename all new
  output tags/references under the required prefix.

Budget a full extra database copy for each snapshot/restore, in addition to WAL,
logs and archives. Snapshot and restore publication flush file contents and directory naming metadata
on supported local filesystems, using the same path as archive pruning. Keep
redundant verified copies; these operations do not replace reliable storage.

## Reproducible verification

`StateBackupTests`, `BackupVerificationTests`, `BackupUpgradeDrillTests`,
`StateRestoreTests` and `ProcessRecoveryTests` cover WAL capture, immutable evidence,
wrong digest/schema, source modes, new-file creation, stale Pending after later
publishing, retained Sending, fresh-tag enforcement, queue isolation and actual
process kills before/after restore commit/publication. The full Phase 3 evidence
and platform results are recorded in [the operational qualification report](phase-3-qualification.md).

For a real old-binary rejection drill, build CLI commit `54de022` separately and
run `scripts/verify_state_upgrade.py --dotnet <dotnet> --legacy-cli <schema-8-cli>`.
It creates synthetic temporary state, checkpoints with the old CLI, upgrades it
with the current Release CLI, compares durable evidence and checks that the old
CLI rejects upgraded state without changing database bytes. It accepts no live
configuration, credentials or existing database.
