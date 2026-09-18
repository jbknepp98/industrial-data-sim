using IndustrialDataSim.Core.Simulation;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed partial class DurableRuntime
{
    /// <summary>
    /// Re-enable generation only after a previously oversized point fits the
    /// current limits. This never resubmits a batch or changes a checkpoint.
    /// Reopen the database with larger limits before calling this method.
    /// </summary>
    public void RetryGeneration(string id) => Access(() =>
    {
        InTransaction(() =>
        {
            var session = ReadSession(id);
            if (session.Status != SessionStatus.Failed || session.ErrorCode != "generation.point_too_large")
                throw new RuntimeFailure("runtime.cannot_retry_generation",
                    "Only a Failed session with generation.point_too_large can retry generation. Inspect its status and error code; use Resume for Paused sessions. Uncertain delivery must not be replayed.");
            if (Scalar("SELECT id FROM batches WHERE session_id=$id AND state IN ('Sending','Uncertain') LIMIT 1", ("$id", id)) is not null)
                throw new RuntimeFailure("runtime.delivery_unresolved",
                    "This session has a Sending or Uncertain batch. Preserve its payload and ownership and investigate acceptance; generation recovery cannot resolve or replay a submission.");

            var model = LoadModel(id); // Verify immutable configuration and generator version.
            if (session.NextSlot >= session.TotalSlots || session.NextSlot < 0 ||
                session.TotalSlots != GenerationWindow.TotalSlots(model))
                throw new RuntimeFailure("runtime.recovery_checkpoint",
                    "The saved checkpoint is inconsistent with an oversized-point failure. Restore verified state; do not reset the cursor.");
            foreach (var tag in model.Session.OutputTags)
            {
                var reservation = Scalar("SELECT owner FROM tags WHERE profile_key=$p AND dataset_key=$d AND tag_key=$t",
                    ("$p", Key(model.Session.ConnectionProfile)), ("$d", Key(model.Session.Dataset)), ("$t", Key(tag.Name)));
                if (reservation is not string currentOwner || currentOwner != id)
                    throw new RuntimeFailure("runtime.recovery_ownership",
                        "A required tag reservation no longer belongs to this session. Restore verified ownership state before recovery; do not reclaim tags from another session.");
            }

            // An oversized-point failure can occur only before a window emits its
            // first point. Earlier candidates in that failed window are suppressed.
            // Use the maximum supported turn length, not the newly configured turn
            // length, so reducing that setting cannot hide the offending candidate.
            // This bounded preview is discarded; no payload or cursor is persisted.
            var preview = GenerationWindow.Generate(model, session.NextSlot, 10000, 1, limits.BatchBytes);
            if (preview.Error?.Code == "generation.point_too_large")
                throw new RuntimeFailure("runtime.generation_limit_unresolved",
                    $"The next emitted point still exceeds the current {limits.BatchBytes}-byte batch limit. Reopen with a larger BatchBytes limit and compatible queue byte limits, then call RetryGeneration again. The session and checkpoint are unchanged.");
            if (preview.Error is not null || preview.PointCount != 1)
                throw new RuntimeFailure("runtime.recovery_preview_failed",
                    "The bounded recovery preview could not reproduce a valid next point. Preserve this session and restore verified configuration/checkpoint state; no recovery change was committed.");

            Execute("UPDATE sessions SET state='Ready',error_code=NULL,error_message=NULL WHERE id=$id", ("$id", id));
            FaultPoint?.Invoke("before_generation_recovery_commit");
        });
        FaultPoint?.Invoke("after_generation_recovery_commit");
        Log(LogLevel.Information, "generation.retry_enabled", "RetryGeneration",
            "The previously oversized point fits the current batch limit; session is Ready at its unchanged checkpoint.",
            "Continue normal generation and delivery. Existing queued batches remain unchanged; queue or disk pressure may still delay generation.", id);
        return true;
    }, sessionId: id);
}
