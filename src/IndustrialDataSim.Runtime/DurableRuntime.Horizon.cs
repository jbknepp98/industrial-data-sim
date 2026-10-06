using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed record HorizonReceipt(int Revision, DateTimeOffset PreviousEndUtc,
    DateTimeOffset EndUtc, long PreviousTotalSlots, long TotalSlots, long Cursor,
    SessionStatus ResultingState);

public sealed partial class DurableRuntime
{
    // This operation changes only the local horizon. Production preflight and
    // delivery retain their usual conflict, retention and no-replay checks.
    public HorizonReceipt ExtendHorizon(string id, DateTimeOffset endUtc, int expectedRevision, string requestId) => Access(() =>
    {
        if (!Guid.TryParseExact(requestId, "D", out var requestGuid))
            throw new RuntimeFailure("extension.request_id", "Supply a requestId in canonical UUID format and reuse it only for the identical extension request.");
        requestId = requestGuid.ToString("D");
        if (endUtc.Offset != TimeSpan.Zero || expectedRevision < 0)
            throw new RuntimeFailure("extension.invalid_request", "Use a UTC endUtc with zero offset and a nonnegative expectedRevision from current horizon status.");
        HorizonReceipt? receipt = null;
        bool committed = false;
        InTransaction(() =>
        {
            var model = LoadModel(id); // Verifies original definition and every revision.
            var session = ReadSession(id);
            int revision = checked((int)Convert.ToInt64(Scalar("SELECT horizon_revision FROM sessions WHERE id=$id", ("$id", id))));
            using (var existing = Command("SELECT revision,previous_end,new_end,previous_total,new_total,cursor,resulting_state FROM horizon_revisions WHERE session_id=$id AND request_id=$request", ("$id", id), ("$request", requestId)))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetInt64(0) != (long)expectedRevision + 1 || reader.GetInt64(2) != endUtc.UtcTicks)
                        throw new RuntimeFailure("extension.request_conflict", "This requestId belongs to different extension arguments. Inspect its receipt; use a new UUID only for a new request.");
                    receipt = new(reader.GetInt32(0), Utc(reader.GetInt64(1)), Utc(reader.GetInt64(2)), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), ReadState<SessionStatus>(reader.GetString(6)));
                    return; // A retry returns the original receipt even after later revisions.
                }
            }
            if (Scalar("SELECT archive_id FROM sessions WHERE id=$id", ("$id", id)) is string)
                throw new RuntimeFailure("extension.archived", "Archived sessions cannot be extended. Preserve their audit history and use a new session with appropriate tag ownership.");
            if (revision != expectedRevision)
                throw new RuntimeFailure("extension.stale_revision", "expectedRevision is stale. Inspect the current horizon and submit a new request based on that revision.");
            if (revision >= 1000)
                throw new RuntimeFailure("extension.revision_limit", "This session has reached 1000 horizon revisions. Preserve its history and plan a new session; do not delete revisions.");
            if (Scalar("SELECT id FROM batches WHERE session_id=$id AND state NOT IN ('Acknowledged','Published','Discarded') LIMIT 1", ("$id", id)) is not null)
                throw new RuntimeFailure("extension.unresolved_delivery", "Resolve outstanding delivery before extending. Drain known-unsent work; preserve Sending or Uncertain batches and never replay them blindly.");
            if (session.Status is not (SessionStatus.Ready or SessionStatus.Paused or SessionStatus.Complete) || session.Cancellation is not null)
                throw new RuntimeFailure("extension.invalid_state", "Only Ready, Paused or Complete sessions without cancellation may extend. Inspect the session error or lifecycle state; extension cannot clear it.");
            foreach (var tag in model.Session.OutputTags)
                if (Scalar("SELECT owner FROM tags WHERE profile_key=$p AND dataset_key=$d AND tag_key=$t", ("$p", Key(model.Session.ConnectionProfile)), ("$d", Key(model.Session.Dataset)), ("$t", Key(tag.Name))) is not string ownerId || ownerId != id)
                    throw new RuntimeFailure("extension.ownership_released", "A required tag is no longer reserved by this session. Do not reclaim it through extension; inspect ownership and plan a new session.");
            if (endUtc <= model.Session.EndUtc)
                throw new RuntimeFailure("extension.end_not_later", "endUtc must be strictly later than the effective end. Inspect the current horizon; shortening or replacing history is prohibited.");
            long total = CountSlots(model, endUtc);
            if (total <= session.TotalSlots)
                throw new RuntimeFailure("extension.no_new_slots", "endUtc does not include another sample. Choose an end strictly beyond the next timestamp on the original sampling grid.");
            if (session.Status == SessionStatus.Complete && session.NextSlot != session.TotalSlots) throw HorizonIntegrity();
            var resulting = session.Status == SessionStatus.Complete ? SessionStatus.Ready : session.Status;
            Execute("""
                INSERT INTO horizon_revisions(session_id,revision,request_id,previous_end,new_end,previous_total,new_total,cursor,config_hash,prior_state,resulting_state,committed_utc)
                VALUES($id,$revision,$request,$oldEnd,$end,$oldTotal,$total,$cursor,
                  (SELECT config_hash FROM sessions WHERE id=$id),$prior,$result,$utc)
                """, ("$id", id), ("$revision", revision + 1), ("$request", requestId),
                ("$oldEnd", model.Session.EndUtc.UtcTicks), ("$end", endUtc.UtcTicks),
                ("$oldTotal", session.TotalSlots), ("$total", total), ("$cursor", session.NextSlot),
                ("$prior", session.Status.ToString()), ("$result", resulting.ToString()), ("$utc", DateTimeOffset.UtcNow.ToString("O")));
            Execute("UPDATE sessions SET horizon_revision=$revision,total_slots=$total,state=$state WHERE id=$id",
                ("$revision", revision + 1), ("$total", total), ("$state", resulting.ToString()), ("$id", id));
            FaultPoint?.Invoke("before_horizon_commit");
            receipt = new(revision + 1, model.Session.EndUtc, endUtc, session.TotalSlots, total, session.NextSlot, resulting);
            committed = true;
        });
        if (committed)
        {
            FaultPoint?.Invoke("after_horizon_commit");
            Log(LogLevel.Information, "session.horizon_extended", "ExtendHorizon", "Session horizon extended atomically; original patterns and candidate cursor are unchanged.",
                "Finite schedules retain their existing terminal behavior; a longer horizon does not repeat them.", id);
        }
        return receipt!;
    }, sessionId: id);

    public object HorizonStatus(string id) => Access(() =>
    {
        var admitted = LoadModel(id, includeHorizon: false);
        var current = Horizon(id);
        return (object)new { sessionId = id, current.Revision, admittedEndUtc = admitted.Session.EndUtc,
            effectiveEndUtc = current.EndUtc, session = GetSession(id),
            notice = "A later end preserves the original sampling grid and patterns. Finite schedules keep their terminal behavior; they do not repeat automatically." };
    }, sessionId: id);

    internal (int Revision, DateTimeOffset EndUtc) Horizon(string id) => Access(() =>
    {
        var model = LoadModel(id);
        return (checked((int)Convert.ToInt64(Scalar("SELECT horizon_revision FROM sessions WHERE id=$id", ("$id", id)))), model.Session.EndUtc);
    }, sessionId: id);

    private SimulationDefinition ApplyHorizon(string id, string json, string hash, SimulationDefinition admitted)
    {
        try { return ValidateHorizon(id, json, hash, admitted); }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw HorizonIntegrity(); }
    }

    private SimulationDefinition ValidateHorizon(string id, string json, string hash, SimulationDefinition admitted)
    {
        long active = Convert.ToInt64(Scalar("SELECT horizon_revision FROM sessions WHERE id=$id", ("$id", id)));
        if (active is < 0 or > 1000) throw HorizonIntegrity();
        long end = admitted.Session.EndUtc.UtcTicks, total = GenerationWindow.TotalSlots(admitted), priorCursor = 0;
        int count = 0;
        using (var command = Command("SELECT revision,previous_end,new_end,previous_total,new_total,cursor,config_hash,prior_state,resulting_state,request_id,committed_utc FROM horizon_revisions WHERE session_id=$id ORDER BY revision LIMIT 1001", ("$id", id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                count++;
                long nextEnd = reader.GetInt64(2), nextTotal = reader.GetInt64(4), cursor = reader.GetInt64(5);
                string prior = reader.GetString(7), result = reader.GetString(8);
                if (count > 1000 || reader.GetInt64(0) != count || reader.GetInt64(1) != end ||
                    nextEnd <= end || nextEnd > DateTimeOffset.MaxValue.UtcTicks || reader.GetInt64(3) != total ||
                    nextTotal <= total || cursor < priorCursor || cursor > total || reader.GetString(6) != hash ||
                    prior is not ("Ready" or "Paused" or "Complete") || result != (prior == "Complete" ? "Ready" : prior) ||
                    (prior == "Complete" && cursor != total) ||
                    !Guid.TryParseExact(reader.GetString(9), "D", out _) || !DateTimeOffset.TryParse(reader.GetString(10), out _))
                    throw HorizonIntegrity();
                try { if (CountSlots(admitted, Utc(nextEnd)) != nextTotal) throw HorizonIntegrity(); }
                catch (RuntimeFailure) { throw HorizonIntegrity(); }
                end = nextEnd; total = nextTotal; priorCursor = cursor;
            }
        if (count != active || Convert.ToInt64(Scalar("SELECT total_slots FROM sessions WHERE id=$id", ("$id", id))) != total ||
            Convert.ToInt64(Scalar("SELECT next_slot FROM sessions WHERE id=$id", ("$id", id))) < priorCursor)
            throw HorizonIntegrity();
        if (active == 0) return admitted;
        // Reload only the effective end. Never overwrite the original JSON/hash,
        // change the origin, or construct a differently seeded pattern schedule.
        var effective = JsonNode.Parse(json)!.AsObject();
        effective["session"]!["endUtc"] = Utc(end).UtcDateTime.ToString("O");
        var loaded = SimulationDefinitionLoader.Load(effective.ToJsonString());
        if (!loaded.IsValid) throw HorizonIntegrity();
        return loaded.Definition!;
    }

    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    private static long CountSlots(SimulationDefinition model, DateTimeOffset end)
    {
        try { return checked(((end.UtcTicks - model.Session.StartUtc.UtcTicks - 1) /
            ((long)model.SamplingIntervalMs * TimeSpan.TicksPerMillisecond) + 1) * model.Session.OutputTags.Count); }
        catch (OverflowException) { throw new RuntimeFailure("extension.slot_limit", "The requested endUtc exceeds the supported candidate count. Use an earlier end or plan a separate session; the checkpoint is unchanged."); }
    }
    private static RuntimeFailure HorizonIntegrity() => new("extension.integrity", "Saved horizon revisions or totals are inconsistent. Stop this session and restore verified state; do not edit revisions, reset its cursor or replay batches.");
}
