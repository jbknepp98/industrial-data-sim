using System.Globalization;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Runtime;

public sealed partial class DurableRuntime
{
    private void RestoreProcess(string id, SimulationDefinition model, long cursor)
    {
        if (!model.HasWindowedProcess) return;
        using var command = Command("SELECT next_slot,payload,hash FROM process_checkpoints WHERE session_id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            if (cursor != 0) throw ProcessIntegrity();
            return; // Tick zero was validated by the loader; nothing was generated yet.
        }
        string payload = reader.GetString(1);
        if (reader.GetInt64(0) != cursor || reader.GetString(2) != ProcessHash(id, cursor, payload)) throw ProcessIntegrity();
        model.RestoreProcessCheckpoint(payload, cursor);
    }

    private void SaveProcess(string id, long cursor, string payload) => Execute("""
        INSERT INTO process_checkpoints(session_id,next_slot,payload,hash) VALUES($id,$cursor,$payload,$hash)
        ON CONFLICT(session_id) DO UPDATE SET next_slot=$cursor,payload=$payload,hash=$hash
        """, ("$id", id), ("$cursor", cursor), ("$payload", payload), ("$hash", ProcessHash(id, cursor, payload)));

    // Bind state to immutable model identity and the flattened sample cursor.
    // This checksum detects accidental edits/corruption, not malicious DB access.
    private string ProcessHash(string id, long cursor, string payload) => Hash(id + "\n" +
        (string)Scalar("SELECT config_hash FROM sessions WHERE id=$id", ("$id", id))! + "\n" +
        cursor.ToString(CultureInfo.InvariantCulture) + "\n" + payload);

    private static ProcessExecutionFailure ProcessIntegrity() => new(new("manufacturing.checkpoint_integrity", "$.manufacturing",
        "Saved process state is missing or does not match its model, cursor or checksum. Restore verified state; do not reset the cursor or regenerate submitted values."));
}
