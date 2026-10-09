using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Runtime;

/// <summary>
/// Fixed manufacturing baseline. Fresh-process runs separate compilation,
/// durable admission, generation and fake delivery; none measures HTTP capacity.
/// Three turns bound this diagnostic, rather than draining an arbitrary horizon.
/// </summary>
internal static class ManufacturingProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int seconds) ||
            seconds is not (60 or 600 or 3600 or 259200))
        {
            Console.Error.WriteLine("capacity.usage: Supply manufacturing and 60|600|3600|259200 horizon seconds. Use a fresh Release process for each case; only temporary synthetic state is used.");
            return 2;
        }
        string folder = Path.Combine(Path.GetTempPath(), "manufacturing-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "manufacturing-simulation.json")))!;
            var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
            model["session"]!["startUtc"] = start.UtcDateTime.ToString("O");
            model["session"]!["endUtc"] = start.AddSeconds(seconds).UtcDateTime.ToString("O");
            model["samplingIntervalMs"] = 1000;
            model["manufacturing"]!["tickMs"] = 1000;
            // Exercise the entire requested horizon instead of ending at the
            // example's production target and accidentally understating cost.
            foreach (var node in model["manufacturing"]!["nodes"]!.AsArray())
                if (node!["accumulator"] is JsonObject accumulator)
                {
                    accumulator.Remove("target");
                    accumulator.Remove("finalPolicy");
                    accumulator.Remove("stopOnTarget");
                }
            if (args[0] == "manufacturing-windowed") model["manufacturing"]!["execution"] = "windowed";
            string configuration = model.ToJsonString();
            var phases = new List<object>();
            var watch = Stopwatch.StartNew();
            long allocated = GC.GetTotalAllocatedBytes();
            var loaded = SimulationDefinitionLoader.Load(configuration);
            phases.Add(Measurement("compile", watch, allocated));
            if (!loaded.IsValid)
            {
                // A refused 72-hour model is evidence of the current limit,
                // never a successful capacity qualification.
                Write(new { outcome = "Rejected", horizonSeconds = seconds, phases, errors = loaded.Errors });
                return 1;
            }
            Directory.CreateDirectory(folder);
            string database = Path.Combine(folder, "state.db");
            string id = loaded.Definition!.Session.SessionId;
            SessionSnapshot beforeReopen;
            using (var runtime = new DurableRuntime(database))
            {
                watch.Restart(); allocated = GC.GetTotalAllocatedBytes();
                runtime.AddSession(configuration);
                phases.Add(Measurement("admit", watch, allocated));
                var delivery = new SimulatedDelivery(runtime, new FakeHistorian { KeepHistory = false });
                for (int turn = 0; turn < 3 && runtime.GetSession(id).Status != SessionStatus.Complete; turn++)
                {
                    watch.Restart(); allocated = GC.GetTotalAllocatedBytes();
                    var generation = runtime.Generate(id);
                    phases.Add(Measurement("generate", watch, allocated));
                    if (!generation.Progressed) throw new RuntimeFailure("capacity.no_progress", "Synthetic generation did not advance. Inspect queue limits and generation diagnostics before interpreting timing results.");
                    watch.Restart(); allocated = GC.GetTotalAllocatedBytes();
                    if (!await delivery.DeliverOneAsync(id))
                        throw new RuntimeFailure("capacity.delivery_failed", "Synthetic delivery did not complete. Resolve fake-delivery or durable-state failures before measuring performance.");
                    phases.Add(Measurement("fakeDelivery", watch, allocated));
                }
                beforeReopen = runtime.GetSession(id);
                if (beforeReopen.NextSlot == 0 || beforeReopen.TotalSlots != seconds * 8L ||
                    beforeReopen.Status is not (SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Complete))
                    throw new RuntimeFailure("capacity.invalid_progress", "Synthetic progress does not match the fixture horizon. Resolve cursor/state accounting before accepting measurements.");
            }
            watch.Restart(); allocated = GC.GetTotalAllocatedBytes();
            using var reopened = new DurableRuntime(database, createIfMissing: false);
            // Horizon status exercises reconstruction even when the short model
            // already completed. Reopening alone does not measure model loading.
            reopened.HorizonStatus(id);
            if (reopened.GetSession(id) != beforeReopen)
                throw new RuntimeFailure("capacity.reopen_mismatch", "Reopening changed the synthetic session checkpoint. Investigate recovery before accepting measurements.");
            phases.Add(Measurement("reopenAndHorizon", watch, allocated));
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Write(new { outcome = "Measured", horizonSeconds = seconds, tags = 8,
                tickMs = 1000, sampleMs = 1000, phases, session = reopened.GetSession(id),
                managedBytes = GC.GetTotalMemory(false), workingSetBytes = process.WorkingSet64,
                storageBytes = Directory.EnumerateFiles(folder).Sum(path => new FileInfo(path).Length),
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                notice = "Three bounded turns at most; allocation includes garbage, memory is sampled, and no network or live-control latency is measured." });
            return 0;
        }
        catch (RuntimeFailure failure) { Write(new { outcome = "Failed", errors = new[] { failure.Error } }); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine("capacity.probe_access: Could not read the synthetic fixture or temporary state. Build the full Release solution and check temporary-directory permissions/free space; no production state was used.");
            return 3;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("capacity.cleanup_failed: Temporary synthetic files could not be removed. Check temporary-directory access and open handles; measurements are unchanged."); }
        }
    }

    private static object Measurement(string phase, Stopwatch watch, long allocated) => new
    { phase, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated };
    private static void Write(object result) => Console.WriteLine(JsonSerializer.Serialize(result));
}
