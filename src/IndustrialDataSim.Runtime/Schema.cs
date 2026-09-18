namespace IndustrialDataSim.Runtime;

internal static class Schema
{
    // Cover only outstanding batches. Acknowledged audit history stays in the
    // table but cannot increase the number of entries scanned for queue totals.
    // SQLite maintains this index in the same transaction as each state change.
    internal const string VersionTwo = """
        CREATE INDEX batch_outstanding ON batches(session_id,point_count,byte_count)
          WHERE state!='Acknowledged';
        PRAGMA user_version=2;
        """;

    // Immutable configuration and batch bodies are written only at creation.
    // Per-tag progress outlives reservations, preventing backward reuse locally.
    internal const string VersionOne = """
        CREATE TABLE runtime_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO runtime_metadata(key,value) VALUES('execution_mode','simulation-only');
        CREATE TABLE sessions(
          id TEXT PRIMARY KEY, profile_key TEXT NOT NULL, dataset_key TEXT NOT NULL,
          config TEXT NOT NULL, config_hash TEXT NOT NULL, generator_version INTEGER NOT NULL,
          state TEXT NOT NULL, next_slot INTEGER NOT NULL, total_slots INTEGER NOT NULL,
          error_code TEXT, error_message TEXT);
        CREATE TABLE tags(
          profile_key TEXT NOT NULL, dataset_key TEXT NOT NULL, tag_key TEXT NOT NULL,
          tag_name TEXT NOT NULL, owner TEXT REFERENCES sessions(id),
          buffered_ticks INTEGER, submitted_ticks INTEGER, acknowledged_ticks INTEGER,
          PRIMARY KEY(profile_key,dataset_key,tag_key));
        CREATE TABLE batches(
          id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL REFERENCES sessions(id),
          state TEXT NOT NULL, start_slot INTEGER NOT NULL, end_slot INTEGER NOT NULL,
          point_count INTEGER NOT NULL, byte_count INTEGER NOT NULL,
          payload TEXT, hash TEXT NOT NULL, positions TEXT NOT NULL);
        CREATE INDEX batch_queue ON batches(session_id,state,id);
        CREATE TABLE attempts(
          batch_id INTEGER PRIMARY KEY REFERENCES batches(id), state TEXT NOT NULL,
          error_code TEXT);
        PRAGMA user_version=1;
        """;
}
