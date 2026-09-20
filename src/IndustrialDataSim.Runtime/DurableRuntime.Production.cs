using System.Text.Json;
using IndustrialDataSim.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed record ArrivalObservation(long BatchId, string Status, int MatchingTags, int NonNullCurrentTags,
    int ChangedTags, string? ErrorCode, string UserReview);

public sealed partial class DurableRuntime
{
    public void RetryProductionPreflight(string id) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        var session = ReadSession(id);
        if (session.Status != SessionStatus.Failed || session.ErrorCode is null ||
            !(session.ErrorCode.StartsWith("historian.", StringComparison.Ordinal) || session.ErrorCode.StartsWith("connection.", StringComparison.Ordinal)) ||
            Scalar("SELECT id FROM batches WHERE session_id=$id AND state IN ('Sending','Uncertain') LIMIT 1", ("$id", id)) is not null)
            throw new RuntimeFailure("production.cannot_retry_preflight", "Only a failed read-only preflight with no unresolved submission can be retried. Inspect status and correct the connection or timeline issue. Uncertain writes cannot be retried.");
        _ = LoadModel(id);
        Execute("UPDATE sessions SET state=CASE WHEN next_slot=total_slots THEN 'Draining' ELSE 'Ready' END,error_code=NULL,error_message=NULL WHERE id=$id", ("$id", id));
        return true;
    }, sessionId: id);

    internal void AdmitProduction(string configuration, ProductionPreflight preflight) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        // Baseline, immutable model and ownership share the admission transaction.
        AdmitSession(configuration, preflight);
        return true;
    });

    internal SimulationDefinition ProductionModel(string id) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        if (Scalar("SELECT session_id FROM production_preflight WHERE session_id=$id", ("$id", id)) is null)
            throw new RuntimeFailure("historian.preflight_missing", "The admitted session has no durable preflight baseline. Preserve the database and investigate interrupted admission; no write is permitted.");
        return LoadModelForWork(id);
    }, sessionId: id);

    internal void BindProduction(HistorianConnection profile) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        // Only an identity digest is persisted. Secret rotation is permitted;
        // redirecting saved work to another origin/audience/profile is refused.
        string identity = Hash(JsonSerializer.Serialize(new { profile.Profile, historian = profile.Historian.AbsoluteUri,
            pulse = profile.Pulse.AbsoluteUri, profile.Audience }));
        string? saved = Scalar("SELECT value FROM runtime_metadata WHERE key='production_identity'") as string;
        if (saved is not null && saved != identity)
            throw new RuntimeFailure("connection.identity_changed", "Production connection identity differs from this database. Restore the original origins, audience and profile; do not redirect queued work.");
        Execute("INSERT OR IGNORE INTO runtime_metadata(key,value) VALUES('production_identity',$value)", ("$value", identity));
        return true;
    });

    internal BatchSnapshot? PeekPending(string id) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        if (ReadSession(id).Status is not (SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Cancelling)) return null;
        using var command = Command("SELECT id,session_id,state,start_slot,end_slot,point_count,byte_count,hash,payload FROM batches WHERE session_id=$id AND state='Pending' ORDER BY id LIMIT 1", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatch(reader) : null;
    }, sessionId: id);

    internal void ProductionFailed(string id, RuntimeFailure failure) => Access(() =>
    {
        Execute("UPDATE sessions SET state='Failed',error_code=$code,error_message=$message WHERE id=$id", ("$id", id),
            ("$code", failure.Error.Code), ("$message", failure.Message));
        Log(LogLevel.Error, failure.Error.Code, "Preflight", "Production preflight failed before submission; queued data and ownership remain.", failure.Message, id);
        return true;
    }, sessionId: id);

    private readonly Dictionary<string, string> observationStates = new();

    internal void SaveObservation(ArrivalObservation observation) => Access(() =>
    {
        Execute("""
            INSERT INTO observations(batch_id,status,matching_tags,nonnull_current_tags,changed_tags,error_code,user_review)
            VALUES($batch,$status,$matching,$current,$changed,$error,$review)
            ON CONFLICT(batch_id) DO UPDATE SET status=$status,matching_tags=$matching,
              nonnull_current_tags=$current,changed_tags=$changed,error_code=$error
            """, ("$batch", observation.BatchId), ("$status", observation.Status), ("$matching", observation.MatchingTags),
            ("$current", observation.NonNullCurrentTags), ("$changed", observation.ChangedTags),
            ("$error", observation.ErrorCode), ("$review", observation.UserReview));
        string session = (string)Scalar("SELECT session_id FROM batches WHERE id=$batch", ("$batch", observation.BatchId))!;
        if (observation.Status != "Pending" && (!observationStates.TryGetValue(session, out var previous) || previous != observation.Status))
        {
            observationStates[session] = observation.Status;
            Log(observation.Status is "Observed" or "ConsistentWithoutNewArrival" ? LogLevel.Information : LogLevel.Warning, "delivery.observation", "Observe",
                $"Arrival observation changed to {observation.Status}. This is not a per-point receipt.",
                "Inspect the session tags and intended pattern. Missing samples never authorize replay; user review remains separate.", session);
        }
        return true;
    });

    public IReadOnlyList<ArrivalObservation> Observations(string id, long afterBatch = 0) => Access(() =>
    {
        _ = ReadSession(id);
        var result = new List<ArrivalObservation>();
        using var command = Command("""
            SELECT o.batch_id,o.status,o.matching_tags,o.nonnull_current_tags,o.changed_tags,o.error_code,o.user_review
            FROM observations o JOIN batches b ON b.id=o.batch_id
            WHERE b.session_id=$id AND b.id>$after ORDER BY b.id LIMIT 100
            """, ("$id", id), ("$after", afterBatch));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
        return result;
    }, sessionId: id);

    public void RecordUserReview(string id, bool accepted) => Access(() =>
    {
        RequireMode(ExecutionMode.Production);
        _ = ReadSession(id);
        if (Scalar("SELECT b.id FROM batches b JOIN observations o ON o.batch_id=b.id WHERE b.session_id=$id AND b.state='Published' LIMIT 1", ("$id", id)) is null)
            throw new RuntimeFailure("production.no_reviewable_publish", "No retained Published batches are available for review. Inspect status or the completed archive; user review cannot acknowledge uncertain work.");
        Execute("UPDATE observations SET user_review=$review WHERE batch_id IN (SELECT id FROM batches WHERE session_id=$id AND state='Published')",
            ("$id", id), ("$review", accepted ? "Accepted" : "NeedsAttention"));
        return true;
    }, sessionId: id);
}
