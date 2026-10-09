namespace IndustrialDataSim.Core.Configuration;

internal static partial class ManufacturingDefinitionLoader
{
    /// <summary>
    /// One process-clock transition, independent of observation sampling and
    /// transport batching. All mutable state belongs to this execution instance.
    /// The legacy compiler consumes these observations as its reference series.
    /// </summary>
    private sealed partial class ProcessStepper(System.Text.Json.JsonElement spec, SessionDefinition session,
        List<Node> ordered, long tickMs) : ProcessExecution
    {
        internal override long NextStep => nextStep;
        internal override long TickMs => tickMs;
        private long nextStep;
        private readonly Dictionary<string, (bool Value, long Since, long Last)> memory = [];
        private readonly Dictionary<string, object> current = [];
        private Dictionary<string, object> previous = [];
        internal Dictionary<string, (object Value, int Quality)> Observations { get; } = [];
        internal List<ProcessTransition> Trace { get; } = [];
        internal int OmittedTransitions { get; private set; }
        internal long? CompletedAt { get; private set; }
        internal bool HasTarget { get; } = spec.TryGetProperty("stopWhen", out _) || ordered.Any(n =>
            n.Spec.TryGetProperty("accumulator", out var accumulator) &&
            accumulator.TryGetProperty("stopOnTarget", out var stop) && stop.GetBoolean());

        private void RecordTransition(long at, Node node, string from, string to, string reason)
        {
            if (Trace.Count < 1000) Trace.Add(new(at, node.Tag, from, to, reason));
            else OmittedTransitions++;
        }

        internal void AdvanceTo(long elapsedTicks)
        {
            long targetStep = elapsedTicks / (tickMs * TimeSpan.TicksPerMillisecond);
            if (targetStep < nextStep - 1 || targetStep - nextStep > 1000)
                throw new ProcessExecutionFailure(new("manufacturing.checkpoint_required", "$.manufacturing",
                    "Requested sample is outside the current bounded process window. Restore the checkpoint for this cursor before generation; do not seek or replay from a different cursor."));
            try { while (nextStep <= targetStep) Step(); }
            catch (Invalid error) { throw new ProcessExecutionFailure(error.Error); }
            catch (OverflowException)
            { throw new ProcessExecutionFailure(new("manufacturing.arithmetic_limit", "$.manufacturing", "Process arithmetic exceeded its supported range. Reduce rates or durations and use a new model; preserve the failed session checkpoint.")); }
        }

        internal void Step()
        {
            if (CompletedAt is not null) return;
            long step = nextStep;
            long ms = checked(step * tickMs); current.Clear();
            bool stopTarget = false;
            foreach (var node in ordered)
            {
                object value;
                var context = new Context(ms, ms, tickMs, current, previous, memory, node.StateSince, node.Path, node.RandomIdentity);
                if (node.Spec.TryGetProperty("expression", out var expression)) value = Evaluate(expression, node.Path + ".expression", context);
                else if (node.Spec.TryGetProperty("state", out var state))
                {
                    value = EvaluateState(node, state, context, step, ms, RecordTransition);
                }
                else
                {
                    value = EvaluateAccumulator(node, context, step, ms, tickMs, RecordTransition, ref stopTarget);
                }
                string type = session.OutputTags.First(t => t.Name == node.Tag).ValueType;
                if (type == "number" ? value is not double : type == "boolean" ? value is not bool : value is not string)
                    throw Fail(node.Path, "Node result type must match its declared output type; implicit coercion is not allowed.");
                current[node.Tag] = value; // Process dependencies see underlying values, not sensor faults/noise.
                object observed = value;
                if (node.Spec.TryGetProperty("noise", out var noise))
                {
                    double amplitude = noise.GetProperty("amplitude").GetDouble();
                    observed = Numeric(value, node.Path + ".noise") + amplitude * (2 * Random(noise.GetProperty("seed").GetUInt32(), node.RandomIdentity + ".noise", step) - 1);
                    if (noise.TryGetProperty("minimum", out var min)) observed = Math.Max((double)observed, min.GetDouble());
                    if (noise.TryGetProperty("maximum", out var max)) observed = Math.Min((double)observed, max.GetDouble());
                    Numeric(observed, node.Path + ".noise");
                }
                int quality = 192, faultIndex = 0;
                if (node.Spec.TryGetProperty("faults", out var faults)) foreach (var fault in faults.EnumerateArray())
                {
                    long start = fault.GetProperty("startMs").GetInt64(), end = start + fault.GetProperty("durationMs").GetInt64();
                    if (ms >= start && ms < end)
                    {
                        if (fault.GetProperty("kind").GetString() == "freeze")
                        { if (!node.Frozen.ContainsKey(faultIndex)) node.Frozen[faultIndex] = observed; observed = node.Frozen[faultIndex]; }
                        else quality = fault.GetProperty("quality").GetInt32();
                    }
                    faultIndex++;
                }
                Observations[node.Tag] = (observed, quality);
            }
            bool stopExpressionValue = spec.TryGetProperty("stopWhen", out var conditionEnd) && Boolean(Evaluate(conditionEnd, "$.manufacturing.stopWhen", new(ms, ms, tickMs, current, previous, memory, 0)), "$.manufacturing.stopWhen");
            previous = new(current);
            if (stopTarget || stopExpressionValue) CompletedAt = ms;
            nextStep++;
        }
    }
}

internal static partial class ManufacturingDefinitionLoader
{
    private sealed record WindowedSeries(ProcessStepper Execution, string Tag) : GeneratorDefinition
    {
        internal override System.Text.Json.JsonElement? Evaluate(long elapsedTicks)
        {
            Execution.AdvanceTo(elapsedTicks);
            return System.Text.Json.JsonSerializer.SerializeToElement(Execution.Observations[Tag].Value);
        }
        internal override int QualityAt(long elapsedTicks) => Execution.Observations[Tag].Quality;
    }
}
