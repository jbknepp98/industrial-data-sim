using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Runtime;

public sealed partial class DurableRuntime
{
    private void InsertSessionProgress(SimulationDefinition model)
    {
        // Session-local positions start empty even when global high-water marks
        // exist for a previous owner. Never inherit another session's progress.
        foreach (var tag in model.Session.OutputTags)
            Execute("INSERT INTO session_tag_progress(session_id,tag_key,tag_name) VALUES($id,$key,$name)",
                ("$id", model.Session.SessionId), ("$key", Key(tag.Name)), ("$name", tag.Name));
    }

    private void SetSessionPosition(string id, string tag, string column, long ticks)
    {
        // Column names come exclusively from internal call sites. Keep updates in
        // the caller's generation/claim/acknowledgement transaction.
        using var command = Command($"""
            UPDATE session_tag_progress SET {column}=CASE
              WHEN {column} IS NULL OR {column}<$ticks THEN $ticks ELSE {column} END
            WHERE session_id=$id AND tag_key=$tag
            """, ("$id", id), ("$tag", Key(tag)), ("$ticks", ticks));
        if (command.ExecuteNonQuery() != 1)
            throw new RuntimeFailure("runtime.progress_integrity",
                "A batch refers to a tag without a session progress entry. Restore verified session and batch metadata; do not reset ownership or replay data.");
    }

    private void MigrateSessionProgress()
    {
        // Old global tag rows may already belong to a later session. Reconstruct
        // each session from its immutable tag declarations and retained batch
        // metadata, never from the current owner or global high-water marks.
        // The caller wraps the entire migration and version change in a transaction.
        Execute(Schema.VersionThree);
        var ids = new List<string>();
        using (var command = Command("SELECT id FROM sessions ORDER BY id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (string id in ids)
        {
            InsertSessionProgress(LoadModel(id));
            using var command = Command("SELECT state,positions FROM batches WHERE session_id=$id ORDER BY id", ("$id", id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string state = reader.GetString(0);
                if (state is not ("Pending" or "Sending" or "Uncertain" or "Acknowledged"))
                    throw ProgressMigrationFailure();
                Dictionary<string, long> positions;
                try { positions = ReadPositions(reader.GetString(1)); }
                catch (RuntimeFailure) { throw ProgressMigrationFailure(); }
                foreach (var (tag, ticks) in positions)
                {
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                        throw ProgressMigrationFailure();
                    SetSessionPosition(id, tag, "buffered_ticks", ticks);
                    if (state != "Pending") SetSessionPosition(id, tag, "submitted_ticks", ticks);
                    if (state == "Acknowledged") SetSessionPosition(id, tag, "acknowledged_ticks", ticks);
                }
            }
        }
        Execute("PRAGMA user_version=3;");
    }

    private static RuntimeFailure ProgressMigrationFailure() => new("runtime.progress_migration",
        "Session progress could not be reconstructed from retained batch metadata. Restore a verified database backup and use a compatible runtime. The upgrade was rolled back; do not edit schema versions or replay batches.");
}
