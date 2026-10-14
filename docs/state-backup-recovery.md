# State snapshots and recovery

Phase 3's first increment provides **evidence snapshots**, not automatic restore
or rollback. A snapshot may predate successful or uncertain Historian writes.
Its `Pending` rows are therefore not proof that those values remain unsent.
First-in-wins, current values, counts and user pattern feedback do not authorize
replaying that queue.

## Create a snapshot

Stop the host gracefully and wait for it to exit. Use the database's canonical
local path, never an alias or a copied owner file. With the built CLI:

```text
production backup <database> <private-directory>
session backup <database> <private-directory>
```

Both commands take an offline snapshot without credentials, network requests,
source schema migration or startup recovery. They report the actual source mode.
They require exclusive ownership using the runtime's owner lock. An active owner
causes `backup.owner_unavailable`; do not delete the owner file to bypass it.
Supported source schemas are 1–8. The source must be a simulator database.

SQLite's backup API includes committed WAL content. The destination is checked
with `integrity_check`, closed and flushed before a non-overwriting rename to
`<backupId>.evidence.sqlite`. Save the JSON receipt separately: it contains the
source schema, execution mode, byte length and SHA-256 of the completed file.
Verify a transferred copy before inspecting it:

```text
production verify-backup <snapshot> <trusted-sha256>
session verify-backup <snapshot> <trusted-sha256>
```

Supply the digest from the separately retained receipt, not a newly calculated
hash of the file you are investigating. Both commands run without credentials
or opening the runtime. They check the digest, evidence marker, standalone
journal mode, SQLite integrity, foreign keys and execution mode. Success reports
byte length and actual mode, with an explicit statement that replay is not
authorized. They do not validate every model/checkpoint or prove the snapshot is
the newest state. They support the original October 9 evidence format.

Verification opens the snapshot read-only and creates no logs or owner file.
Keep its directory free of concurrent writers or renames while checking it.
WAL, SHM or rollback-journal sidecars are rejected because their content is not
covered by the snapshot digest. Preserve unexpected sidecars for investigation;
do not delete them to make verification pass. Errors return a nonzero exit with
safe corrective guidance; paths, values and raw SQLite errors are omitted.

A hash detects damage
relative to a trusted receipt; it does not authenticate an untrusted receipt.
A `.partial` file has no completion guarantee. A crash after rename but before
receipt output can leave an orphan completed file; preserve it for investigation
and create a new snapshot rather than treating an absent receipt as success.

Snapshots contain private models, tag names and retained payloads. Store them
outside version control in an access-restricted directory. This command does not
add encryption, enforce a retention policy or guarantee survival of directory
metadata across power loss. Budget disk for a complete additional database copy.

## Restart and recovery boundaries

Normal restart uses the **original** database. Its committed queue, process
checkpoint and delivery history remain authoritative; interrupted Sending becomes
Uncertain through normal startup recovery.

Evidence snapshots intentionally use reserved schema version 1000008. Current
runtimes reject them with `backup.evidence_only` before changing journal mode;
older runtimes reject their unsupported schema. Copying or renaming a snapshot
does not remove this safeguard. Do not edit the schema marker or use an older
binary to bypass recovery policy. SQL inspection should be read-only. The source
schema version in the separately retained receipt describes the copied tables.

If original state is lost, stop all writers for its tags and preserve every
available snapshot, WAL, archive and operational log. Do not resume the snapshot's
queue or infer missing delivery from Historian readback. A supported procedure to
establish a new forward-only continuation is still required; this increment does
not supply one. New simulations can use fresh, disjoint tags after checking that
no other writer owns them.

## Upgrade boundary and remaining qualification

Take evidence before upgrading, then upgrade the original state only with a
compatible runtime. This snapshot command does not change its source version.
Existing migration tests remain relevant, but a complete rollback/restore drill,
recovery approval procedure, disk-pressure drills, active-history retention and
process-continuous rollover remain Phase 3 work. V12 and V13 remain open.

## Verification — October 9, 2026

The complete .NET suite passed 832 tests before the final WAL regression was
added. The final targeted backup suite passed all six tests, including committed
WAL capture without upgrading a schema-7 source, source-byte preservation,
Pending retention, snapshot execution rejection, active-owner rejection,
non-overwriting filenames, both CLI modes without authentication, and safe
missing-source/destination errors. No Historian writes were used. Local build
required a writable temporary socket directory and single-node MSBuild.

## Verification — October 14, 2026

All 848 .NET tests passed with zero build warnings/errors. Added drills cover:

- Schema-7 upgrade preserves the pending payload and session cursor; evidence
  taken before upgrade remains blocked and still matches its trusted receipt.
- A conflicting migration table causes rollback, retaining schema version and
  pending payload rather than deleting evidence or inventing checkpoints.
- Windowed manufacturing snapshots retain the exact serialized process state,
  hash, cursor and queued batch while the original continues independently.
- Production snapshots remain Pending after the original later becomes Published
  or Uncertain. Verification passes, but runtime execution remains prohibited.
  These use a scripted HTTP handler; no real Historian was contacted.
- Both verification CLI forms are read-only. Invalid digests, damaged files,
  missing files, sidecars, missing guards, invalid modes and broken foreign keys
  return controlled errors. A matching hash does not bypass structural checks.

This is incremental V12 evidence, not completed restoration qualification.
No automatic or manual command to reactivate an evidence snapshot was added.
