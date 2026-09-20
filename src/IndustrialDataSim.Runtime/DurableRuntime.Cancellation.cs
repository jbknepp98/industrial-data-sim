using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

public sealed partial class DurableRuntime
{
    /// <summary>
    /// Stop future generation. Drain sends existing queued work; DiscardPending
    /// permanently removes only provably unsent payloads. Neither mode undoes writes.
    /// The caller must choose a mode; cancellation cannot be resumed or reversed.
    /// </summary>
    public void Cancel(string id, CancellationMode mode) => Access(() =>
    {
        if (mode is not (CancellationMode.Drain or CancellationMode.DiscardPending))
            throw new RuntimeFailure("runtime.invalid_cancellation_mode",
                "Choose Drain to deliver queued work, or DiscardPending to permanently discard unsent payloads. There is no default cancellation mode.");
        bool finished = false;
        bool changed = false;
        InTransaction(() =>
        {
            var session = ReadSession(id);
            if (session.Cancellation is not null)
            {
                if (session.Cancellation == mode && session.Status is SessionStatus.Cancelling or SessionStatus.Cancelled)
                    return; // Repeating the same accepted request is safe after a lost caller response.
                throw new RuntimeFailure("runtime.cancellation_locked",
                    "Cancellation is already recorded. Its mode cannot change. Inspect status and retained work; failed or uncertain delivery requires investigation, not another cancellation.");
            }
            if (session.Status is not (SessionStatus.Ready or SessionStatus.Paused or SessionStatus.Draining or SessionStatus.Failed) ||
                (mode == CancellationMode.Drain && session.Status == SessionStatus.Failed))
                throw new RuntimeFailure("runtime.cannot_cancel",
                    "Drain cancellation requires Ready, Paused, or Draining status. DiscardPending also permits Failed sessions with no unresolved submissions. Complete and Uncertain sessions cannot be cancelled; inspect status first.");
            if (Scalar("SELECT id FROM batches WHERE session_id=$id AND state='Uncertain' LIMIT 1", ("$id", id)) is not null ||
                (mode == CancellationMode.DiscardPending && Scalar("SELECT id FROM batches WHERE session_id=$id AND state='Sending' LIMIT 1", ("$id", id)) is not null))
                throw new RuntimeFailure("runtime.cancellation_unresolved",
                    "Cancellation cannot discard or bypass Sending or Uncertain work. Wait for an in-flight result or investigate acceptance evidence. Preserve payloads and ownership; do not replay.");
            if (mode == CancellationMode.DiscardPending)
            {
                // A Pending label alone is not enough if an attempt already exists.
                // Reject inconsistent state rather than deleting possible delivery evidence.
                if (Scalar("""
                    SELECT b.id FROM batches b JOIN attempts a ON a.batch_id=b.id
                    WHERE b.session_id=$id AND b.state='Pending' LIMIT 1
                    """, ("$id", id)) is not null)
                    throw new RuntimeFailure("runtime.cancellation_attempt_exists",
                        "A Pending batch has submission-attempt evidence. Preserve the state database and investigate; its payload cannot be safely discarded.");
                Execute("UPDATE batches SET state='Discarded',payload=NULL WHERE session_id=$id AND state='Pending'", ("$id", id));
            }
            Execute("UPDATE sessions SET state='Cancelling',cancellation_mode=$mode WHERE id=$id", ("$id", id), ("$mode", mode.ToString()));
            finished = CancelIfDrained(id);
            changed = true;
            FaultPoint?.Invoke("before_cancellation_commit");
        });
        FaultPoint?.Invoke("after_cancellation_commit");
        if (changed)
        {
            blockedReasons.Remove(id);
            Log(LogLevel.Information, "session.cancellation_requested", "Cancel",
                mode == CancellationMode.Drain
                    ? "Generation stopped; existing queued work will drain before cancellation finishes."
                    : "Generation stopped; unsent payloads were permanently discarded. Audit metadata and progress remain.",
                "Inspect status before releasing tags. Cancellation does not undo submitted or acknowledged values.", id);
            if (finished) LogCancellationFinished(id, "Cancel");
        }
        return true;
    }, sessionId: id);

    private bool CancelIfDrained(string id)
    {
        using var command = Command("""
            UPDATE sessions SET state='Cancelled' WHERE id=$id AND state='Cancelling'
            AND NOT EXISTS(SELECT 1 FROM batches INDEXED BY batch_outstanding
              WHERE session_id=$id AND state NOT IN ('Acknowledged','Discarded','Published'))
            """, ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    private void LogCancellationFinished(string id, string operation) =>
        Log(LogLevel.Information, "session.cancelled", operation,
            "Session cancellation finished; no outstanding batches remain. Tag reservations are retained.",
            "Inspect progress and discarded batch metadata. Call ReleaseCancelled only when ready to release reservations.", id);

    public void ReleaseCancelled(string id) => Access(() =>
    {
        var session = ReadSession(id);
        if (session.Status != SessionStatus.Cancelled ||
            Scalar("SELECT id FROM batches INDEXED BY batch_outstanding WHERE session_id=$id AND state NOT IN ('Acknowledged','Discarded','Published') LIMIT 1", ("$id", id)) is not null)
            throw new RuntimeFailure("runtime.cannot_release_cancelled",
                "Only a Cancelled session with no outstanding work can release tags. Finish draining or investigate failed/uncertain delivery first.");
        Execute("UPDATE tags SET owner=NULL WHERE owner=$id", ("$id", id));
        Log(LogLevel.Information, "session.tags_released", "ReleaseCancelled",
            "Cancelled session tag reservations released; session progress and global ordering protection remain.", sessionId: id);
        return true;
    }, sessionId: id);
}
