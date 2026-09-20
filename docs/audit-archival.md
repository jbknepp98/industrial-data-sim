# Explicit audit archival

Schema version 5 introduced archival; schema version 6 adds production progress,
preflight baselines and observations. Upgrade a SQLite-aware backup first; older
executables reject the new schema. Never lower user_version manually.

Stop the host/worker, then choose a local archive directory outside version control:

```text
session archive state/simulation.db completed-session .tools/archives
session archive-info state/simulation.db completed-session
session verify-archive state/simulation.db completed-session .tools/archives
```

Use `production` instead of `session` for a production database. Only Complete or
Cancelled sessions with no outstanding batches qualify. Archival is explicit;
there is no automatic age/count deletion or pruning of Uncertain work.

The operation streams a new UUID-named JSONL file containing a versioned manifest,
immutable model/hash/version, final session and tag progress, preflight evidence,
batch hashes/ranges/counts/positions, attempt outcomes, and production observations
and reviews. A completion record closes the export. Data values and configuration
are intentional archive content; keep the files private and out of Git.
Completed payloads were already pruned by normal delivery and cannot be recovered
from their hashes. Archives are audit records, not instructions to regenerate or replay.

The export is capped at 128 MiB per operation. A new `.partial` file is flushed,
then renamed without replacement. Unix flushes the directory and its ancestors;
Windows uses a [write-through move](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).
Failure to durably publish the filename refuses pruning. The final file is reopened
and compared with the hash computed while writing. Only then does one SQLite
transaction record the receipt and remove batch, attempt and observation rows.
The verified file remains open through commit. Original session/configuration,
preflight, progress and global tag timestamp limits remain in SQLite. Tag
reservations are released. Archived sessions leave active inventory and its
100-session limit, but direct status and archive receipts remain accessible.
Session IDs and earlier timestamps cannot be reused.

A process failure before commit leaves original rows intact; an incomplete or
orphaned export may remain. A failure after commit leaves the durable receipt.
Check archive-info before repeating an operation with a lost reply. Re-archiving
an archived session is refused. Verify an export against its database receipt;
missing or altered files require restoration from backup, never reconstruction
by publishing again. No import/restore command or automatic orphan cleanup exists.

SQLite reuses freed pages; this operation does not VACUUM or promise a smaller
file. Tombstone/configuration metadata still grows with completed session count.
Exports beyond 128 MiB require a later partitioned archival design and currently
fail without pruning. Maintain backups of both the database and archive directory.
Process-interruption tests do not establish power-loss guarantees for a filesystem,
storage controller, external file deletion or loss of the archive volume.
