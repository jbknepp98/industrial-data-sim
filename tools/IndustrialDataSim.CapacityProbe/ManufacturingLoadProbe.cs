using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

internal static class ManufacturingLoadProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int seconds) || seconds is not (60 or 3600 or 259200 or 864000))
        {
            Console.Error.WriteLine("capacity.usage: Supply manufacturing-load and 60|3600|259200|864000 horizon seconds. This four-session synthetic probe has a 20-minute deadline and never uses live credentials.");
            return 2;
        }
        string folder = Path.Combine(Path.GetTempPath(), "manufacturing-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string database = Path.Combine(folder, "state.db");
            var template = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "manufacturing-simulation.json")))!;
            template["manufacturing"]!["execution"] = "windowed";
            template["samplingIntervalMs"] = 1000;
            template["manufacturing"]!["tickMs"] = 1000;
            var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            template["session"]!["startUtc"] = start.UtcDateTime.ToString("O");
            template["session"]!["endUtc"] = start.AddSeconds(seconds).UtcDateTime.ToString("O");
            foreach (var node in template["manufacturing"]!["nodes"]!.AsArray())
                if (node!["accumulator"] is JsonObject accumulator)
                { accumulator.Remove("target"); accumulator.Remove("finalPolicy"); accumulator.Remove("stopOnTarget"); }
            var clock = Stopwatch.StartNew();
            var limits = new RuntimeLimits { SessionQueuePoints = 2000, GlobalQueuePoints = 8000 };
            var fake = new FakeHistorian { KeepHistory = false };
            using var process = Process.GetCurrentProcess();
            var statusTimes = new List<double>();
            long maximumQueue = 0, maximumManaged = 0, maximumWorkingSet = 0, maximumStorage = 0;
            int rounds = 0, reopenCount = 0;
            long generatedPoints = 0;
            var expectedSlots = new Dictionary<string, long>();
            double maximumRoundMs = 0;
            DurableRuntime? runtime = new(database, limits);
            try
            {
                for (int index = 0; index < 4; index++)
                {
                    var model = JsonNode.Parse(template.ToJsonString().Replace("Plant.", $"S{index}.Plant.", StringComparison.Ordinal))!;
                    model["session"]!["sessionId"] = "load-" + index;
                    if (args[0] == "mixed-load") ConfigureMixedModel(model, index, seconds);
                    runtime.AddSession(model.ToJsonString());
                    expectedSlots["load-" + index] = runtime.GetSession("load-" + index).TotalSlots;
                }
                double admissionMs = clock.Elapsed.TotalMilliseconds;
                var delivery = new SimulatedDelivery(runtime, fake);
                while (runtime.ListSessions().Any(session => session.Status != SessionStatus.Complete))
                {
                    if (clock.Elapsed > TimeSpan.FromMinutes(20)) throw new RuntimeFailure("capacity.deadline", "Synthetic load exceeded 20 minutes. Preserve the report and investigate generation/storage costs; do not treat the run as qualified.");
                    var turnClock = Stopwatch.StartNew();
                    var before = runtime.ListSessions();
                    runtime.GenerateRound();
                    var queued = runtime.ListSessions();
                    generatedPoints += queued.Sum(session => session.QueuedPoints) - before.Sum(session => session.QueuedPoints);
                    maximumQueue = Math.Max(maximumQueue, queued.Sum(session => session.QueuedPoints));
                    if (queued.Any(session => session.Status is SessionStatus.Failed or SessionStatus.Uncertain) ||
                        queued.Any(session => session.QueuedPoints > limits.SessionQueuePoints) || maximumQueue > limits.GlobalQueuePoints)
                        throw new RuntimeFailure("capacity.state_failed", "A session failed or exceeded its queue bound. Resolve the state/correctness failure before accepting capacity measurements.");
                    // Delay fake delivery every other turn to exercise backpressure.
                    if (rounds % 2 == 1) await delivery.RunRoundAsync();
                    maximumRoundMs = Math.Max(maximumRoundMs, turnClock.Elapsed.TotalMilliseconds);
                    rounds++;
                    if (rounds % 100 == 0)
                    {
                        var statusClock = Stopwatch.StartNew();
                        runtime.ListSessions(); statusTimes.Add(statusClock.Elapsed.TotalMilliseconds);
                        maximumManaged = Math.Max(maximumManaged, GC.GetTotalMemory(false));
                        process.Refresh(); maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
                        maximumStorage = Math.Max(maximumStorage, Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length));
                    }
                    if (rounds % 2000 == 0)
                    {
                        var saved = runtime.ListSessions();
                        runtime.Dispose(); runtime = new(database, limits, createIfMissing: false);
                        if (!saved.SequenceEqual(runtime.ListSessions())) throw new RuntimeFailure("capacity.restart_mismatch", "Restart changed stored session progress. Stop qualification and investigate checkpoint recovery.");
                        delivery = new(runtime, fake); reopenCount++;
                    }
                }
                var final = runtime.ListSessions();
                if (final.Any(session => session.NextSlot != expectedSlots[session.SessionId] || session.QueuedPoints != 0))
                    throw new RuntimeFailure("capacity.count_mismatch", "Final synthetic cursor/queue does not match the fixed workload. Inspect generation before interpreting throughput.");
                long expectedPoints = args[0] == "mixed-load"
                    ? seconds * 16L + seconds + 7L * (seconds / 2) + expectedSlots["load-3"]
                    : seconds * 32L;
                if (generatedPoints != expectedPoints)
                    throw new RuntimeFailure("capacity.emission_mismatch", "Emitted synthetic points do not match independently calculated gate suppression and target completion. Resolve generation before accepting capacity results.");
                maximumManaged = Math.Max(maximumManaged, GC.GetTotalMemory(false));
                process.Refresh(); maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
                maximumStorage = Math.Max(maximumStorage, Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length));
                Console.WriteLine(JsonSerializer.Serialize(new { outcome = "Measured", horizonSeconds = seconds, sessions = 4, tags = 32,
                    admissionMs, elapsedSeconds = clock.Elapsed.TotalSeconds, generatedPoints, workload = args[0],
                    pointsPerSecond = generatedPoints / clock.Elapsed.TotalSeconds, rounds, reopenCount,
                    maximumRoundMs, maximumQueue, maximumManaged, maximumWorkingSet, maximumStorage,
                    maximumDirectStatusMs = statusTimes.Count == 0 ? (double?)null : statusTimes.Max(),
                    runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    notice = "Accelerated synthetic generation and fake delivery. Counts establish local generation only. No HTTP, live lag, IPC latency or real-time soak is measured." }));
            }
            finally { runtime?.Dispose(); }
            return 0;
        }
        catch (RuntimeFailure failure) { Console.WriteLine(JsonSerializer.Serialize(new { outcome = "Failed", error = failure.Error })); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine("capacity.access_failed: Check the built fixture and temporary storage permissions/free space. No production state was accessed."); return 3; }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("capacity.cleanup_failed: Temporary synthetic state remains; check open handles and temporary-directory access."); }
        }
    }
    private static void ConfigureMixedModel(JsonNode model, int index, int seconds)
    {
        if (index == 2) return; // Fixed-horizon manufacturing at the full requested horizon.
        if (index == 3)
        {
            // Target-bounded equipment keeps the legacy precompiled semantics.
            // Its finite two-minute horizon includes the example's final target.
            var original = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "manufacturing-simulation.json")))!;
            model["manufacturing"] = JsonNode.Parse(original["manufacturing"]!.ToJsonString().Replace("Plant.", "S3.Plant.", StringComparison.Ordinal));
            model["session"]!["endUtc"] = "2026-09-01T00:02:00Z";
            return;
        }
        model.AsObject().Remove("manufacturing");
        var outputs = new JsonArray();
        var generators = new JsonArray();
        for (int tag = 0; tag < 8; tag++)
        {
            string name = $"S{index}.Signal{tag}";
            outputs.Add(new JsonObject { ["name"] = name, ["valueType"] = index == 1 && tag == 0 ? "boolean" : "number" });
            if (index == 0)
                generators.Add(new JsonObject { ["tag"] = name, ["kind"] = "ramp", ["startValue"] = tag, ["ratePerSecond"] = 1, ["maximum"] = 100 });
            else if (tag == 0)
            {
                var steps = new JsonArray();
                for (int step = 0; step < 6; step++)
                    steps.Add(new JsonObject { ["value"] = step % 2 == 1, ["durationMs"] = Math.Max(1000, seconds * 1000L / 6) });
                generators.Add(new JsonObject { ["tag"] = name, ["kind"] = "booleanTimeline", ["afterSteps"] = "holdLast", ["steps"] = steps });
            }
            else
                generators.Add(new JsonObject { ["tag"] = name, ["kind"] = "booleanGate", ["triggerTag"] = "S1.Signal0",
                    ["whenFalse"] = tag % 2 == 0 ? "pauseAndSuppress" : "continueAndSuppress",
                    ["pattern"] = new JsonObject { ["kind"] = "ramp", ["startValue"] = 0, ["ratePerSecond"] = 1 } });
        }
        model["session"]!["outputTags"] = outputs;
        model["generators"] = generators;
    }

}
