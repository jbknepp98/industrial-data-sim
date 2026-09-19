namespace IndustrialDataSim.Runtime;

/// <summary>
/// Count Pending, Sending, and Uncertain work directly from the live index.
/// Explicit index selection prevents a planner change from scanning retained
/// history instead. No separate counters can drift from committed batch state.
/// </summary>
internal static class QueueQueries
{
    internal const string GlobalUsage = """
        SELECT COALESCE(SUM(point_count),0),COALESCE(SUM(byte_count),0)
        FROM batches INDEXED BY batch_outstanding WHERE state NOT IN ('Acknowledged','Discarded')
        """;

    internal const string SessionSnapshot = """
            SELECT id,state,next_slot,total_slots,
              COALESCE((SELECT SUM(point_count) FROM batches INDEXED BY batch_outstanding WHERE session_id=s.id AND state NOT IN ('Acknowledged','Discarded')),0),
              COALESCE((SELECT SUM(byte_count) FROM batches INDEXED BY batch_outstanding WHERE session_id=s.id AND state NOT IN ('Acknowledged','Discarded')),0),
              error_code,error_message,cancellation_mode FROM sessions s WHERE id=$id
        """;
}
