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
Compare its hash before inspecting a transferred copy. A hash detects damage
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
