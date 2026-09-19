using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

internal sealed record DeliveryWork(BatchSnapshot Batch, string Profile, string Dataset);

public sealed partial class DurableRuntime
{
    private string? deliveryAfter;

    internal IReadOnlyList<string> DeliveryOrder() => Access(() =>
    {
        var ids = Rotate(SessionIds().Where(id => ReadSession(id).Status is SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Cancelling).ToList(), deliveryAfter);
        if (ids.Count > 0) deliveryAfter = ids[0];
        return ids;
    });

    internal DeliveryWork? Claim(string id) => Access<DeliveryWork?>(() =>
    {
        var session = ReadSession(id);
        if (session.Status is not (SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Cancelling)) return null;
        if (Scalar("SELECT id FROM batches WHERE session_id=$id AND state IN ('Sending','Uncertain') LIMIT 1", ("$id", id)) is not null)
            return null;
        BatchSnapshot? batch;
        using (var command = Command("SELECT id,session_id,state,start_slot,end_slot,point_count,byte_count,hash,payload FROM batches WHERE session_id=$id AND state='Pending' ORDER BY id LIMIT 1", ("$id", id)))
        using (var reader = command.ExecuteReader())
            batch = reader.Read() ? ReadBatch(reader) : null;
        if (batch is null) return null;
        if (batch.Payload is null || Hash(batch.Payload) != batch.Hash)
        {
            Execute("UPDATE sessions SET state='Failed',error_code='delivery.payload_integrity',error_message='Queued payload hash does not match. Restore verified state; do not regenerate or submit the altered batch.' WHERE id=$id", ("$id", id));
            Log(LogLevel.Error, "delivery.payload_integrity", "Claim", "Queued payload failed its integrity check; session is Failed.",
                "Restore verified state. Do not regenerate or submit the altered batch.", id, batch);
            return null;
        }
        Core.Configuration.SimulationDefinition model;
        try { model = LoadModelForWork(id); }
        catch (RuntimeFailure failure) when (failure.Error.Code == "runtime.configuration_integrity")
        {
            // No claim or transport call has occurred. The persisted failure is
            // visible in status, while the delivery round can serve other sessions.
            return null;
        }
        ValidateBatchPositions(batch);
        InTransaction(() =>
        {
            Execute("UPDATE batches SET state='Sending' WHERE id=$batch", ("$batch", batch.Id));
            Execute("INSERT INTO attempts(batch_id,state) VALUES($batch,'Sending')", ("$batch", batch.Id));
            UpdatePositions(batch.Id, id, "submitted_ticks");
            FaultPoint?.Invoke("before_sending_commit");
        });
        FaultPoint?.Invoke("after_sending_commit");
        Log(LogLevel.Debug, "delivery.claimed", "Claim", "Submission intent committed; this is not proof of acceptance.", sessionId: id, batch: batch);
        return new(batch with { Status = BatchStatus.Sending }, model.Session.ConnectionProfile, model.Session.Dataset);
    }, sessionId: id);

    internal void Finish(DeliveryWork work, bool acknowledged) => Access(() =>
    {
        long batchId = work.Batch.Id;
        string id = work.Batch.SessionId;
        if ((string?)Scalar("SELECT state FROM batches WHERE id=$batch", ("$batch", batchId)) != "Sending")
            throw new RuntimeFailure("delivery.invalid_transition", "Batch is no longer Sending. Inspect durable state; do not replay or force acknowledgement.");
        bool completed = false;
        bool cancelled = false;
        ValidateBatchPositions(work.Batch);
        InTransaction(() =>
        {
            if (acknowledged)
            {
                UpdatePositions(batchId, id, "acknowledged_ticks");
                Execute("UPDATE batches SET state='Acknowledged',payload=NULL WHERE id=$batch", ("$batch", batchId));
                Execute("UPDATE attempts SET state='Acknowledged' WHERE batch_id=$batch", ("$batch", batchId));
                completed = CompleteIfDrained(id);
                cancelled = CancelIfDrained(id);
                FaultPoint?.Invoke("before_acknowledgement_commit");
            }
            else
            {
                Execute("UPDATE batches SET state='Uncertain' WHERE id=$batch", ("$batch", batchId));
                Execute("UPDATE attempts SET state='Uncertain',error_code='delivery.uncertain' WHERE batch_id=$batch", ("$batch", batchId));
                Execute("""
                    UPDATE sessions SET state='Uncertain',error_code='delivery.uncertain',
                    error_message='Batch acceptance is uncertain. Preserve the payload and tag ownership; investigate transport evidence. Do not resend missing samples.' WHERE id=$id
                    """, ("$id", id));
            }
        });
        if (acknowledged)
        {
            FaultPoint?.Invoke("after_acknowledgement_commit");
            Log(LogLevel.Debug, "delivery.acknowledged", "Finish", "Simulated transport acknowledgement committed; queued payload released.", sessionId: id, batch: work.Batch);
            LogCompletion(id, "Finish", completed);
            if (cancelled) LogCancellationFinished(id, "Finish");
        }
        else
            Log(LogLevel.Warning, "delivery.uncertain", "Finish", "Batch acceptance is uncertain; session and subsequent delivery are stopped.",
                "Preserve payload and tag ownership. Investigate acceptance evidence; do not resend missing samples or force acknowledgement.", id, work.Batch);
        return true;
    }, sessionId: work.Batch.SessionId);

    private void UpdatePositions(long batchId, string session, string column)
    {
        // Column is selected only by the two internal call sites, never input.
        string json = (string)Scalar("SELECT positions FROM batches WHERE id=$batch", ("$batch", batchId))!;
        var positions = ReadPositions(json);
        foreach (var (tag, ticks) in positions)
        {
            SetSessionPosition(session, tag, column, ticks);
            Execute($"UPDATE tags SET {column}=$ticks WHERE owner=$id AND tag_key=$tag",
                ("$ticks", ticks), ("$id", session), ("$tag", Key(tag)));
        }
    }

    private static Dictionary<string, long> ReadPositions(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw PositionIntegrityFailure();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw PositionIntegrityFailure();
            var positions = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (positions.Count >= 1000 || property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt64(out long ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks ||
                    !positions.TryAdd(property.Name, ticks)) throw PositionIntegrityFailure();
            }
            if (positions.Count == 0) throw PositionIntegrityFailure();
            return positions;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw PositionIntegrityFailure(); }
    }

    private void ValidateBatchPositions(BatchSnapshot batch)
    {
        // Position metadata drives durable high-water marks. Verify it against
        // the hash-checked payload before recording intent or submitting anything.
        var positions = ReadPositions((string)Scalar("SELECT positions FROM batches WHERE id=$batch", ("$batch", batch.Id))!);
        try
        {
            using var document = JsonDocument.Parse(batch.Payload!);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw PositionIntegrityFailure();
            int count = 0;
            foreach (var tag in document.RootElement.EnumerateObject())
            {
                if (!positions.Remove(tag.Name, out long saved) || tag.Value.ValueKind != JsonValueKind.Array ||
                    tag.Value.GetArrayLength() == 0) throw PositionIntegrityFailure();
                long previous = -1;
                foreach (var point in tag.Value.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("t", out var timestamp) ||
                        timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTime(out var utc) ||
                        utc.Kind != DateTimeKind.Utc || utc.Ticks <= previous) throw PositionIntegrityFailure();
                    previous = utc.Ticks;
                    if (++count > 10000) throw PositionIntegrityFailure();
                }
                if (saved != previous) throw PositionIntegrityFailure();
            }
            if (positions.Count != 0 || count != batch.PointCount) throw PositionIntegrityFailure();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw PositionIntegrityFailure(); }
    }

    private static RuntimeFailure PositionIntegrityFailure() => new("runtime.progress_integrity",
        "Saved batch positions are malformed or disagree with the queued payload. Preserve the database and restore verified batch metadata; do not submit, reset progress, or replay data.");
}
