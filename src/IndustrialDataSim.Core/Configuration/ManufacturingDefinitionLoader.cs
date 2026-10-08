using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

public sealed record ProcessTransition(long ElapsedMs, string Tag, string From, string To, string Reason);

/// <summary>
/// Bounded deterministic process interpreter. The process clock is explicit and
/// independent of observation sampling, transport batches and wall-clock speed.
/// Rebuilding the same prefix reconstructs states, timers and production batches.
/// No expression can perform I/O or execute arbitrary code.
/// </summary>
internal static class ManufacturingDefinitionLoader
{
    private sealed class Invalid(string code, string path, string message) : Exception(message)
    { internal ValidationError Error => new(code, path, Message); }
    private static Invalid Fail(string path, string message) => new("manufacturing.invalid", path, message);
    private static JsonElement Field(JsonElement obj, string key, string path) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value)
        ? value : throw Fail(path + "." + key, "This field is required; inspect the manufacturing contract and supply its documented type.");
    private static void Fields(JsonElement obj, string path, params string[] allowed)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw Fail(path, "Expected an object.");
        var seen = new HashSet<string>();
        foreach (var property in obj.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw Fail(path, "Object contains unknown or repeated fields. Use only fields documented for this operation.");
    }
    private static string Text(JsonElement obj, string key, string path)
    {
        var v = Field(obj, key, path);
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()) || v.GetString()!.Length > 1024) throw Fail(path + "." + key, "Use a nonempty string of at most 1024 characters.");
        return v.GetString()!;
    }
    private static long Integer(JsonElement obj, string key, string path, long minimum, long maximum = int.MaxValue)
    {
        var v = Field(obj, key, path);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n) || n < minimum || n > maximum)
            throw Fail(path + "." + key, $"Use a whole number from {minimum} through {maximum}.");
        return n;
    }
    private static double Number(JsonElement obj, string key, string path) => Numeric(Scalar(Field(obj, key, path), path + "." + key), path + "." + key);
    private static object Scalar(JsonElement v, string path) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when v.GetString()!.Length <= 1024 => v.GetString()!,
        JsonValueKind.Number when v.TryGetDouble(out double n) && double.IsFinite(n) => n,
        _ => throw Fail(path, "Use a finite number, Boolean, or string of at most 1024 characters.")
    };
    private static double Numeric(object value, string path) => value is double n && double.IsFinite(n) ? n : throw Fail(path, "This expression requires finite numeric operands; implicit conversions are not allowed.");
    private static bool Boolean(object value, string path) => value is bool b ? b : throw Fail(path, "This expression requires Boolean operands; use an explicit comparison.");

    internal static SimulationLoadResult Load(JsonElement spec, SessionDefinition session, int sampleMs)
    {
        try { return new(Compile(spec, session, sampleMs), []); }
        catch (Invalid error) { return new(null, [error.Error]); }
        catch (Exception error) when (error is OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException)
        { return new(null, [new("manufacturing.shape_or_limit", "$.manufacturing", "A manufacturing value has the wrong shape or exceeds a time/arithmetic limit. Check expressions, durations, bounds and references; no partial model was created.")]); }
    }

    private sealed class Node(JsonElement spec, string tag, string path)
    {
        internal JsonElement Spec = spec;
        internal string Tag = tag, Path = path, State = "";
        internal string RandomIdentity = "tag:" + tag;
        internal long StateSince, BatchElapsed, BatchDuration, BatchIndex;
        internal double Total, PreviousRate;
        internal bool TargetReached;
        internal List<object> Values = [];
        internal List<int> Qualities = [];
        internal Dictionary<int, object> Frozen = [];
    }

    private static SimulationDefinition Compile(JsonElement spec, SessionDefinition session, int sampleMs)
    {
        const string path = "$.manufacturing";
        Fields(spec, path, "tickMs", "nodes", "stopWhen");
        long tickMs = Integer(spec, "tickMs", path, 1);
        long tickTicks = checked(tickMs * TimeSpan.TicksPerMillisecond);
        var array = Field(spec, "nodes", path);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32 || array.GetArrayLength() != session.OutputTags.Count)
            throw Fail(path + ".nodes", "Declare exactly one node per output, with 1–32 output tags.");
        long ticks = (session.EndUtc - session.StartUtc).Ticks;
        long count = (ticks - 1) / tickTicks + 1;
        if (count > 250000 / array.GetArrayLength()) throw Fail(path + ".tickMs", "Process compilation supports at most 250000 node-clock evaluations. Increase tickMs, shorten the session or split independent equipment into sessions.");
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        int index = 0, expressionBudget = 4096;
        foreach (var nodeSpec in array.EnumerateArray())
        {
            string p = path + $".nodes[{index++}]";
            Fields(nodeSpec, p, "tag", "expression", "state", "accumulator", "noise", "faults");
            string tag = Text(nodeSpec, "tag", p);
            if (!session.OutputTags.Any(t => t.Name == tag) || !nodes.TryAdd(tag, new(nodeSpec, tag, p))) throw Fail(p + ".tag", "Use each declared output tag exactly once, with exact spelling.");
            if (new[] { "expression", "state", "accumulator" }.Count(k => nodeSpec.TryGetProperty(k, out _)) != 1) throw Fail(p, "Choose exactly one expression, state machine or accumulator for this node.");
        }
        var references = new Dictionary<string, HashSet<string>>();
        foreach (var node in nodes.Values) references[node.Tag] = ValidateNode(node, nodes, tickMs, ref expressionBudget);
        if (spec.TryGetProperty("stopWhen", out var stopExpression)) ValidateExpression(stopExpression, path + ".stopWhen", nodes, new(), tickMs, 0, ref expressionBudget);
        if (count * (4096 - expressionBudget + nodes.Count) > 2000000)
            throw Fail(path, "Process compilation exceeds 2000000 expression-clock evaluations. Increase tickMs, shorten the horizon or simplify expressions.");
        var ordered = new List<Node>();
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(string tag)
        {
            if (visited.Contains(tag)) return;
            if (!visiting.Add(tag)) throw new Invalid("manufacturing.cycle", nodes[tag].Path, "Instantaneous dependency cycle detected. Remove the cycle or use an explicit previous-value reference with its initial value.");
            foreach (string dependency in references[tag]) Visit(dependency);
            visiting.Remove(tag); visited.Add(tag); ordered.Add(nodes[tag]);
        }
        foreach (string tag in nodes.Keys) Visit(tag);
        var memory = new Dictionary<string, (bool Value, long Since, long Last)>();
        var current = new Dictionary<string, object>();
        var previous = new Dictionary<string, object>();
        var trace = new List<ProcessTransition>(); int omitted = 0;
        void Trace(long at, Node node, string from, string to, string reason)
        { if (trace.Count < 1000) trace.Add(new(at, node.Tag, from, to, reason)); else omitted++; }
        bool hasTarget = spec.TryGetProperty("stopWhen", out _) || ordered.Any(n => n.Spec.TryGetProperty("accumulator", out var a) && a.TryGetProperty("stopOnTarget", out var b) && b.GetBoolean());
        long? completedAt = null;
        long compiledBytes = 0;
        for (long step = 0; step < count; step++)
        {
            long ms = checked(step * tickMs); current.Clear();
            bool stopTarget = false;
            foreach (var node in ordered)
            {
                object value;
                var context = new Context(ms, ms, tickMs, current, previous, memory, node.StateSince, node.Path, node.RandomIdentity);
                if (node.Spec.TryGetProperty("expression", out var expression)) value = Evaluate(expression, node.Path + ".expression", context);
                else if (node.Spec.TryGetProperty("state", out var state))
                {
                    value = EvaluateState(node, state, context, step, ms, Trace);
                }
                else
                {
                    value = EvaluateAccumulator(node, context, step, ms, tickMs, Trace, ref stopTarget);
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
                compiledBytes += observed is string text ? checked(text.Length * 2L + 32) : 32;
                if (compiledBytes > 16 * 1024 * 1024)
                    throw Fail(path, "Compiled process observations exceed 16 MiB. Shorten the horizon, reduce string lengths or increase tickMs.");
                node.Values.Add(observed); node.Qualities.Add(quality);
            }
            bool stopExpressionValue = spec.TryGetProperty("stopWhen", out var conditionEnd) && Boolean(Evaluate(conditionEnd, path + ".stopWhen", new(ms, ms, tickMs, current, previous, memory, 0)), path + ".stopWhen");
            previous = new(current);
            if (stopTarget || stopExpressionValue) { completedAt = ms; break; }
        }
        var definitions = ordered.ToDictionary(n => n.Tag, n => (GeneratorDefinition)new ProcessSeries(n.Values.ToArray(), n.Qualities.ToArray(), tickTicks));
        // Hold the final process value until the first observation grid point at
        // or after completion, then drain. Never invent an off-grid final sample.
        if (completedAt is long finish)
        {
            long finalSampleMs = checked(((finish + sampleMs - 1) / sampleMs) * sampleMs);
            if (finalSampleMs * TimeSpan.TicksPerMillisecond >= ticks)
                throw Fail(path, "The production target falls after the last observation grid point. Extend the configured session end or reduce samplingIntervalMs so the final quantity can be observed.");
            var end = session.StartUtc.AddTicks(checked(finalSampleMs * TimeSpan.TicksPerMillisecond + 1));
            if (end < session.EndUtc) session = session with { EndUtc = end };
        }
        return new(session, sampleMs, new ReadOnlyDictionary<string, GeneratorDefinition>(definitions))
        { HasProductionTarget = hasTarget, ProcessTrace = trace.AsReadOnly(), OmittedProcessTransitions = omitted };
    }

    // State transitions use the prior state and a single ordered rule pass.
    // Evaluating every predicate keeps timer histories independent of priority.
    private static object EvaluateState(Node node, JsonElement state, Context context,
        long step, long ms, Action<long, Node, string, string, string> trace)
    {
        if (step == 0) node.State = Text(state, "initial", node.Path + ".state");
        string before = node.State; string? selected = null; int rule = 0, selectedRule = -1;
        foreach (var transition in state.GetProperty("transitions").EnumerateArray())
        {
            string p = node.Path + $".state.transitions[{rule}]";
            bool condition = Boolean(Evaluate(transition.GetProperty("when"), p + ".when", context), p);
            string from = transition.GetProperty("from").GetString()!;
            long after = transition.TryGetProperty("afterMs", out var a) ? a.GetInt64() : 0;
            if (selected is null && (from == "*" || from == before) && ms - node.StateSince >= after && condition)
            { selected = transition.GetProperty("to").GetString()!; selectedRule = rule; }
            rule++;
        }
        if (selected is not null && selected != before)
        { node.State = selected; node.StateSince = ms; trace(ms, node, before, selected, "transition " + selectedRule); }
        return node.State;
    }

    // Both totalizers consume the preceding process interval. Delivery batching
    // and observation frequency cannot advance production independently.
    private static double EvaluateAccumulator(Node node, Context context, long step,
        long ms, long tickMs, Action<long, Node, string, string, string> trace, ref bool stopTarget)
    {
        var a = node.Spec.GetProperty("accumulator"); string p = node.Path + ".accumulator";
        string mode = a.GetProperty("mode").GetString()!;
        double initial = a.GetProperty("initial").GetDouble();
        if (step == 0) node.Total = initial;
        bool enabled = !a.TryGetProperty("enabled", out var enable) || Boolean(Evaluate(enable, p + ".enabled", context), p);
        if (mode == "time")
        {
            if (step > 0 && !node.TargetReached) node.Total += node.PreviousRate * tickMs / 1000.0;
            node.PreviousRate = enabled ? Numeric(Evaluate(a.GetProperty("ratePerSecond"), p + ".ratePerSecond", context), p) : 0;
            if (node.PreviousRate < 0) throw Fail(p + ".ratePerSecond", "A totalizer rate must be nonnegative.");
        }
        else
        {
            if (node.BatchDuration == 0) node.BatchDuration = Duration(a, node.RandomIdentity, tickMs, node.BatchIndex);
            // Active elapsed time belongs to the preceding clock interval.
            // A gate change at this tick affects the next interval only.
            bool wasEnabled = step > 0 && node.PreviousRate > 0;
            if (wasEnabled && !node.TargetReached) node.BatchElapsed += tickMs;
            node.PreviousRate = enabled ? 1 : 0;
            if (node.BatchElapsed >= node.BatchDuration && !node.TargetReached)
            {
                var quantity = a.GetProperty("quantityRange");
                double low = quantity.GetProperty("minimum").GetDouble(), high = quantity.GetProperty("maximum").GetDouble();
                node.Total += low + (high - low) * Random(a.GetProperty("seed").GetUInt32(), node.RandomIdentity + ".quantity", node.BatchIndex);
                node.BatchElapsed = 0; node.BatchIndex++;
                node.BatchDuration = Duration(a, node.RandomIdentity, tickMs, node.BatchIndex);
                trace(ms, node, "batch", "complete", "production batch completed");
            }
        }
        if (!double.IsFinite(node.Total)) throw Fail(p, "Accumulation overflowed. Reduce rates, quantities, initial value or session length.");
        if (a.TryGetProperty("target", out var target) && node.Total >= target.GetDouble())
        {
            if (a.GetProperty("finalPolicy").GetString() == "cap") node.Total = target.GetDouble();
            if (!node.TargetReached) trace(ms, node, "producing", "targetReached", "production target reached");
            node.TargetReached = true;
            stopTarget |= a.TryGetProperty("stopOnTarget", out var stop) && stop.GetBoolean();
        }
        return node.Total;
    }

    private static long Duration(JsonElement accumulator, string path, long tickMs, long batch)
    {
        var range = accumulator.GetProperty("durationRangeMs");
        long low = range.GetProperty("minimum").GetInt64() / tickMs, high = range.GetProperty("maximum").GetInt64() / tickMs;
        return checked((low + (long)Math.Floor(Random(accumulator.GetProperty("seed").GetUInt32(), path + ".duration", batch) * (high - low + 1))) * tickMs);
    }

    private sealed record Context(long Time, long LocalTime, long TickMs, Dictionary<string, object> Current,
        Dictionary<string, object> Previous, Dictionary<string, (bool Value, long Since, long Last)> Memory, long StateSince, string NodePath = "", string RandomIdentity = "process");
    private static double Random(uint seed, string identity, long index)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + identity + ":" + index.ToString(CultureInfo.InvariantCulture)));
        ulong number = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return (number >> 11) * (1.0 / 9007199254740992.0);
    }

    private sealed record ProcessSeries(object[] Values, int[] Qualities, long TickTicks) : GeneratorDefinition
    {
        private int Index(long elapsed) => (int)Math.Min(elapsed / TickTicks, Values.Length - 1);
        internal override JsonElement? Evaluate(long elapsedTicks) => JsonSerializer.SerializeToElement(Values[Index(elapsedTicks)]);
        internal override int QualityAt(long elapsedTicks) => Qualities[Index(elapsedTicks)];
    }

    private static HashSet<string> ValidateNode(Node node, Dictionary<string, Node> nodes, long tick, ref int budget)
    {
        var refs = new HashSet<string>(); var spec = node.Spec; string p = node.Path;
        if (spec.TryGetProperty("expression", out var expression)) ValidateExpression(expression, p + ".expression", nodes, refs, tick, 0, ref budget);
        if (spec.TryGetProperty("state", out var state))
        {
            string sp = p + ".state"; Fields(state, sp, "initial", "transitions"); Text(state, "initial", sp);
            var transitions = Field(state, "transitions", sp);
            if (transitions.ValueKind != JsonValueKind.Array || transitions.GetArrayLength() is < 1 or > 64) throw Fail(sp + ".transitions", "Use 1–64 prioritized transitions; at most one transition is selected per clock tick.");
            int i = 0;
            foreach (var transition in transitions.EnumerateArray())
            {
                string tp = sp + $".transitions[{i++}]"; Fields(transition, tp, "from", "to", "when", "afterMs");
                Text(transition, "from", tp); Text(transition, "to", tp);
                if (transition.TryGetProperty("afterMs", out _)) Aligned(transition, "afterMs", tp, tick, 0);
                ValidateExpression(Field(transition, "when", tp), tp + ".when", nodes, refs, tick, 0, ref budget);
            }
        }
        if (spec.TryGetProperty("accumulator", out var a))
        {
            string ap = p + ".accumulator";
            Fields(a, ap, "mode", "initial", "enabled", "ratePerSecond", "quantityRange", "durationRangeMs", "seed", "target", "finalPolicy", "stopOnTarget");
            string mode = Text(a, "mode", ap);
            if (mode is not ("time" or "batch")) throw Fail(ap + ".mode", "Choose time or batch accumulation.");
            if (Number(a, "initial", ap) < 0) throw Fail(ap + ".initial", "Initial production quantity must be nonnegative.");
            if (a.TryGetProperty("enabled", out var enabled)) ValidateExpression(enabled, ap + ".enabled", nodes, refs, tick, 0, ref budget);
            if (mode == "time")
            {
                if (a.TryGetProperty("quantityRange", out _) || a.TryGetProperty("durationRangeMs", out _) || a.TryGetProperty("seed", out _)) throw Fail(ap, "Time accumulation uses ratePerSecond; batch quantity/duration/seed fields do not apply.");
                ValidateExpression(Field(a, "ratePerSecond", ap), ap + ".ratePerSecond", nodes, refs, tick, 0, ref budget);
            }
            else
            {
                if (a.TryGetProperty("ratePerSecond", out _)) throw Fail(ap, "Batch accumulation uses randomized quantity and duration ranges, not ratePerSecond.");
                var q = Field(a, "quantityRange", ap); Fields(q, ap + ".quantityRange", "minimum", "maximum");
                double lo = Number(q, "minimum", ap + ".quantityRange"), hi = Number(q, "maximum", ap + ".quantityRange");
                if (lo <= 0 || hi < lo || !double.IsFinite(hi - lo)) throw Fail(ap + ".quantityRange", "Use positive finite quantity bounds with maximum >= minimum and a finite span.");
                var d = Field(a, "durationRangeMs", ap); Fields(d, ap + ".durationRangeMs", "minimum", "maximum");
                long low = Aligned(d, "minimum", ap + ".durationRangeMs", tick, tick), high = Aligned(d, "maximum", ap + ".durationRangeMs", tick, tick);
                if (high < low) throw Fail(ap + ".durationRangeMs", "Maximum duration must be at least minimum; both must be multiples of tickMs.");
                Integer(a, "seed", ap, 0, uint.MaxValue);
            }
            if (a.TryGetProperty("target", out _))
            {
                double target = Number(a, "target", ap);
                if (target <= 0 || target < Number(a, "initial", ap)) throw Fail(ap + ".target", "Target must be positive and not below initial quantity.");
                string policy = Text(a, "finalPolicy", ap);
                if (policy != "cap" && (mode != "batch" || policy != "wholeBatch")) throw Fail(ap + ".finalPolicy", "Use cap, or wholeBatch for batch accumulation when final-batch overshoot is intended.");
            }
            else if (a.TryGetProperty("finalPolicy", out _) || a.TryGetProperty("stopOnTarget", out _)) throw Fail(ap, "finalPolicy and stopOnTarget require a target.");
            if (a.TryGetProperty("stopOnTarget", out var stop) && stop.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(ap + ".stopOnTarget", "Use an explicit Boolean.");
        }
        if (spec.TryGetProperty("noise", out var noise))
        {
            Fields(noise, p + ".noise", "amplitude", "seed", "minimum", "maximum");
            if (Number(noise, "amplitude", p + ".noise") < 0) throw Fail(p + ".noise.amplitude", "Noise amplitude must be nonnegative.");
            Integer(noise, "seed", p + ".noise", 0, uint.MaxValue);
            double lo = noise.TryGetProperty("minimum", out _) ? Number(noise, "minimum", p + ".noise") : double.MinValue;
            double hi = noise.TryGetProperty("maximum", out _) ? Number(noise, "maximum", p + ".noise") : double.MaxValue;
            if (hi < lo) throw Fail(p + ".noise", "Noise maximum must be at least minimum.");
        }
        if (spec.TryGetProperty("faults", out var faults))
        {
            if (faults.ValueKind != JsonValueKind.Array || faults.GetArrayLength() > 32) throw Fail(p + ".faults", "Use an array of at most 32 timed faults.");
            int i = 0;
            foreach (var fault in faults.EnumerateArray())
            {
                string fp = p + $".faults[{i++}]"; Fields(fault, fp, "kind", "startMs", "durationMs", "quality");
                string kind = Text(fault, "kind", fp); Aligned(fault, "startMs", fp, tick, 0); Aligned(fault, "durationMs", fp, tick, tick);
                if (kind == "quality") Integer(fault, "quality", fp, 0, 255);
                else if (kind != "freeze" || fault.TryGetProperty("quality", out _)) throw Fail(fp, "Use freeze without quality, or quality with a byte-valued quality code.");
            }
        }
        return refs;
    }

    private static long Aligned(JsonElement obj, string key, string path, long tick, long minimum)
    {
        long result = Integer(obj, key, path, minimum);
        if (result % tick != 0) throw Fail(path + "." + key, "Duration or offset must be a multiple of manufacturing.tickMs.");
        return result;
    }

    private static void ValidateExpression(JsonElement expression, string path, Dictionary<string, Node> nodes,
        HashSet<string> references, long tick, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 16) throw Fail(path, "Expressions exceed 4096 nodes or 16 nesting levels. Simplify the process model.");
        if (expression.ValueKind != JsonValueKind.Object) { Scalar(expression, path); return; }
        if (expression.TryGetProperty("tag", out _) || expression.TryGetProperty("previous", out _))
        {
            bool previous = expression.TryGetProperty("previous", out _);
            Fields(expression, path, previous ? ["previous", "initial"] : ["tag"]);
            string tag = Text(expression, previous ? "previous" : "tag", path);
            if (!nodes.ContainsKey(tag)) throw Fail(path, "Reference an existing local manufacturing node with exact spelling.");
            if (previous) Scalar(Field(expression, "initial", path), path + ".initial"); else references.Add(tag);
            return;
        }
        string op = Text(expression, "op", path);
        string[] fields = op switch
        {
            "elapsedMs" or "stateElapsedMs" => ["op"],
            "not" or "rising" or "falling" => ["op", "arg"],
            "held" => ["op", "arg", "durationMs"],
            "and" or "or" => ["op", "args"],
            "if" => ["op", "when", "then", "else"],
            "eq" or "ne" or "lt" or "lte" or "gt" or "gte" or "add" or "subtract" or "multiply" or "divide" => ["op", "left", "right"],
            "between" => ["op", "value", "minimum", "maximum"],
            "uniform" => ["op", "minimum", "maximum", "seed", "holdMs"],
            "ramp" => ["op", "start", "ratePerSecond", "minimum", "maximum"],
            "schedule" => ["op", "repeat", "steps"],
            _ => throw Fail(path + ".op", "Unknown expression operation. Use a documented typed comparison, Boolean/timer, arithmetic or pattern operation.")
        };
        Fields(expression, path, fields);
        foreach (string child in new[] { "arg", "left", "right", "when", "then", "else", "value" })
            if (fields.Contains(child)) ValidateExpression(Field(expression, child, path), path + "." + child, nodes, references, tick, depth + 1, ref budget);
        if (op is "and" or "or")
        {
            var args = Field(expression, "args", path);
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() is < 1 or > 32) throw Fail(path + ".args", "Use 1–32 Boolean expressions.");
            int i = 0; foreach (var arg in args.EnumerateArray()) ValidateExpression(arg, path + $".args[{i++}]", nodes, references, tick, depth + 1, ref budget);
        }
        if (op == "held") Aligned(expression, "durationMs", path, tick, 0);
        if (op is "uniform" or "between")
        {
            double lo = Number(expression, "minimum", path), hi = Number(expression, "maximum", path);
            if (hi < lo || !double.IsFinite(hi - lo)) throw Fail(path, "Use ordered finite bounds with a finite span.");
        }
        if (op == "uniform") { Integer(expression, "seed", path, 0, uint.MaxValue); Aligned(expression, "holdMs", path, tick, tick); }
        if (op == "ramp")
        {
            Number(expression, "start", path); Number(expression, "ratePerSecond", path);
            double lo = Number(expression, "minimum", path), hi = Number(expression, "maximum", path);
            if (hi < lo) throw Fail(path, "Ramp maximum must be at least minimum.");
        }
        if (op == "schedule")
        {
            var repeat = Field(expression, "repeat", path);
            if (repeat.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(path + ".repeat", "Use an explicit Boolean.");
            var steps = Field(expression, "steps", path);
            if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 1000) throw Fail(path + ".steps", "Use 1–1000 explicit schedule steps.");
            long total = 0; int i = 0;
            foreach (var step in steps.EnumerateArray())
            {
                string sp = path + $".steps[{i++}]"; Fields(step, sp, "durationMs", "value");
                total = checked(total + Aligned(step, "durationMs", sp, tick, tick));
                ValidateExpression(Field(step, "value", sp), sp + ".value", nodes, references, tick, depth + 1, ref budget);
            }
        }
    }

    private static object Evaluate(JsonElement expression, string path, Context context)
    {
        if (expression.ValueKind != JsonValueKind.Object) return Scalar(expression, path);
        if (expression.TryGetProperty("tag", out var tag)) return context.Current[tag.GetString()!];
        if (expression.TryGetProperty("previous", out var prior)) return context.Previous.TryGetValue(prior.GetString()!, out var old) ? old : Scalar(expression.GetProperty("initial"), path + ".initial");
        string op = expression.GetProperty("op").GetString()!;
        object Child(string field) => Evaluate(expression.GetProperty(field), path + "." + field, context);
        switch (op)
        {
            case "elapsedMs": return (double)context.LocalTime;
            case "stateElapsedMs": return (double)(context.Time - context.StateSince);
            case "not": return !Boolean(Child("arg"), path);
            case "and":
            case "or":
                // Evaluate every operand so edge/timer memory cannot depend on
                // short-circuit order. There are no expression side effects/I/O.
                var results = expression.GetProperty("args").EnumerateArray().Select((a, i) => Boolean(Evaluate(a, path + $".args[{i}]", context), path)).ToArray();
                return op == "and" ? results.All(v => v) : results.Any(v => v);
            case "rising":
            case "falling":
            case "held":
                bool value = Boolean(Child("arg"), path);
                var memory = context.Memory.TryGetValue(path, out var saved) ? saved : (Value: false, Since: context.Time, Last: context.Time - context.TickMs);
                if (memory.Last != context.Time - context.TickMs) memory = (false, context.Time, memory.Last);
                long since = value != memory.Value ? context.Time : memory.Since;
                context.Memory[path] = (value, since, context.Time);
                return op == "rising" ? value && !memory.Value : op == "falling" ? !value && memory.Value : value && context.Time - since >= expression.GetProperty("durationMs").GetInt64();
            case "if":
                bool condition = Boolean(Child("when"), path);
                object yes = Child("then"), no = Child("else");
                if (yes.GetType() != no.GetType()) throw Fail(path, "Both conditional branches must return the same scalar type.");
                return condition ? yes : no;
            case "eq":
            case "ne":
                object left = Child("left"), right = Child("right");
                if (left.GetType() != right.GetType()) throw Fail(path, "Equality requires operands of the same scalar type.");
                bool equal = left.Equals(right); return op == "eq" ? equal : !equal;
            case "lt":
            case "lte":
            case "gt":
            case "gte":
            case "add":
            case "subtract":
            case "multiply":
            case "divide":
                double leftNumber = Numeric(Child("left"), path + ".left");
                double rightNumber = Numeric(Child("right"), path + ".right");
                if (op == "lt") return leftNumber < rightNumber;
                if (op == "lte") return leftNumber <= rightNumber;
                if (op == "gt") return leftNumber > rightNumber;
                if (op == "gte") return leftNumber >= rightNumber;
                if (op == "divide" && rightNumber == 0)
                    throw new Invalid("manufacturing.division_by_zero", path + ".right",
                        "Divide requires a nonzero denominator. Configure the right operand to remain nonzero; conditional branches are evaluated eagerly and cannot guard an invalid division.");
                double result = op switch {
                    "add" => leftNumber + rightNumber, "subtract" => leftNumber - rightNumber,
                    "multiply" => leftNumber * rightNumber, _ => leftNumber / rightNumber };
                if (!double.IsFinite(result))
                    throw new Invalid("manufacturing.arithmetic_overflow", path,
                        $"The {op} operation produced a nonfinite result. Reduce operand magnitudes or, for division, increase the denominator magnitude; keep results within the finite numeric range.");
                return result;
            case "between":
                double number = Numeric(Child("value"), path);
                return number >= expression.GetProperty("minimum").GetDouble() && number <= expression.GetProperty("maximum").GetDouble();
            case "uniform":
                double low = expression.GetProperty("minimum").GetDouble(), high = expression.GetProperty("maximum").GetDouble();
                long bin = context.LocalTime / expression.GetProperty("holdMs").GetInt64();
                return low + (high - low) * Random(expression.GetProperty("seed").GetUInt32(), context.RandomIdentity + path[context.NodePath.Length..], bin);
            case "ramp":
                double ramp = expression.GetProperty("start").GetDouble() + expression.GetProperty("ratePerSecond").GetDouble() * context.LocalTime / 1000.0;
                return Math.Clamp(Numeric(ramp, path), expression.GetProperty("minimum").GetDouble(), expression.GetProperty("maximum").GetDouble());
            case "schedule":
                var steps = expression.GetProperty("steps").EnumerateArray().ToArray();
                long duration = steps.Sum(s => s.GetProperty("durationMs").GetInt64());
                long time = expression.GetProperty("repeat").GetBoolean() ? context.LocalTime % duration : Math.Min(context.LocalTime, duration - 1);
                int index = 0;
                foreach (var step in steps)
                {
                    long length = step.GetProperty("durationMs").GetInt64();
                    if (time < length) return Evaluate(step.GetProperty("value"), path + $".steps[{index}].value", context with { LocalTime = time });
                    time -= length; index++;
                }
                throw Fail(path, "Schedule did not resolve a step; check durations.");
            default: throw Fail(path, "Unknown operation; validate the manufacturing model before generation.");
        }
    }
}
