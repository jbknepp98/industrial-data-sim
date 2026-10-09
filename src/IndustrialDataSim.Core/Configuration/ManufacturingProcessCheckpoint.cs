using System.Text.Json;

namespace IndustrialDataSim.Core.Configuration;

internal static partial class ManufacturingDefinitionLoader
{
    private sealed partial class ProcessStepper
    {
        private sealed record NodeState(string Tag, string State, long StateSince, long BatchElapsed,
            long BatchDuration, long BatchIndex, double Total, double PreviousRate, bool TargetReached,
            Dictionary<int, object> Frozen);
        private sealed record TimerState(bool Value, long Since, long Last);
        private sealed record ObservationState(object Value, int Quality);
        private sealed record Checkpoint(int Version, long NextStep, NodeState[] Nodes,
            Dictionary<string, object> Previous, Dictionary<string, TimerState> Memory,
            Dictionary<string, ObservationState> Observations);

        internal override string Capture()
        {
            // Trace is an explanation aid, not process state. Never accumulate
            // an unbounded transition history inside a durable checkpoint.
            var state = new Checkpoint(1, nextStep, ordered.Select(node => new NodeState(
                node.Tag, node.State, node.StateSince, node.BatchElapsed, node.BatchDuration,
                node.BatchIndex, node.Total, node.PreviousRate, node.TargetReached, node.Frozen)).ToArray(),
                previous, memory.ToDictionary(pair => pair.Key, pair => new TimerState(pair.Value.Value, pair.Value.Since, pair.Value.Last)),
                Observations.ToDictionary(pair => pair.Key, pair => new ObservationState(pair.Value.Value, pair.Value.Quality)));
            string json = JsonSerializer.Serialize(state);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
                throw new ProcessExecutionFailure(new("manufacturing.checkpoint_limit", "$.manufacturing",
                    "Process checkpoint exceeds 1 MiB. Use shorter strings or fewer retained fault values in a new model; preserve this session's last committed checkpoint and pending work."));
            return json;
        }

        internal override void Restore(string checkpoint)
        {
            try
            {
                if (System.Text.Encoding.UTF8.GetByteCount(checkpoint) > 1024 * 1024) throw CheckpointFailure();
                var state = JsonSerializer.Deserialize<Checkpoint>(checkpoint) ?? throw CheckpointFailure();
                if (state.Version != 1 || state.NextStep < 1 || state.Nodes.Length != ordered.Count ||
                    state.Nodes.Select(node => node.Tag).Distinct().Count() != ordered.Count ||
                    state.Previous.Count != ordered.Count || state.Observations.Count != ordered.Count || state.Memory.Count > 4096)
                    throw CheckpointFailure();
                foreach (var node in ordered)
                {
                    var saved = state.Nodes.Single(item => item.Tag == node.Tag);
                    if (!double.IsFinite(saved.Total) || !double.IsFinite(saved.PreviousRate) ||
                        saved.StateSince < 0 || saved.BatchElapsed < 0 || saved.BatchDuration < 0 || saved.BatchIndex < 0 || saved.Frozen.Count > 32)
                        throw CheckpointFailure();
                    node.State = saved.State; node.StateSince = saved.StateSince;
                    node.BatchElapsed = saved.BatchElapsed; node.BatchDuration = saved.BatchDuration;
                    node.BatchIndex = saved.BatchIndex; node.Total = saved.Total;
                    node.PreviousRate = saved.PreviousRate; node.TargetReached = saved.TargetReached;
                    node.Frozen = saved.Frozen.ToDictionary(pair => pair.Key, pair => RestoreScalar(pair.Value));
                    var observed = state.Observations[node.Tag];
                    if (observed.Quality is < 0 or > 255) throw CheckpointFailure();
                    Observations[node.Tag] = (RestoreScalar(observed.Value), observed.Quality);
                }
                previous = state.Previous.ToDictionary(pair => pair.Key, pair => RestoreScalar(pair.Value));
                memory.Clear();
                foreach (var (key, timer) in state.Memory) memory[key] = (timer.Value, timer.Since, timer.Last);
                current.Clear(); Trace.Clear(); OmittedTransitions = 0;
                nextStep = state.NextStep;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException or Invalid)
            { throw CheckpointFailure(); }
        }

        private static object RestoreScalar(object value) => value is JsonElement json
            ? Scalar(json, "$.manufacturing") : throw CheckpointFailure();
        private static ProcessExecutionFailure CheckpointFailure() => new(new("manufacturing.checkpoint_integrity", "$.manufacturing",
            "Saved process checkpoint is malformed, incompatible, or exceeds 1 MiB. Restore verified state using the matching simulator version; do not edit the checkpoint or regenerate submitted values."));
    }
}
