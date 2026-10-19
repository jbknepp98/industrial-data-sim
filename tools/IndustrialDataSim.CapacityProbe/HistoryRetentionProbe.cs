using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

internal static class HistoryRetentionProbe
{
    // Four fixed eight-tag windowed processes. One batch/second/session matches
    // a live cadence, but execution is accelerated and uses only the sealed fake.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int rounds) || rounds is not (1000 or 10000))
        {
            Console.Error.WriteLine("capacity.usage: Use history-load 1000|10000. This accelerated four-session fake-only probe never reads credentials or publishes live data.");
            return 2;
        }
        string folder = Path.Combine(Path.GetTempPath(), "history-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string database = Path.Combine(folder, "state.sqlite");
            string archives = Path.Combine(folder, "archives");
            var limits = new RuntimeLimits { BatchPoints = 8 };
            var clock = Stopwatch.StartNew();
            using var runtime = new DurableRuntime(database, limits);
            var template = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "manufacturing-simulation.json")))!;
            template["manufacturing"]!["execution"] = "windowed";
            template["manufacturing"]!.AsObject().Remove("stopWhen");
            foreach (var node in template["manufacturing"]!["nodes"]!.AsArray())
                if (node!["accumulator"] is JsonObject accumulator)
                { accumulator.Remove("target"); accumulator.Remove("finalPolicy"); accumulator.Remove("stopOnTarget"); }
            template["samplingIntervalMs"] = 1000;
            template["manufacturing"]!["tickMs"] = 1000;
            template["session"]!["startUtc"] = "2026-09-01T00:00:00Z";
            template["session"]!["endUtc"] = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddSeconds(rounds + 1).UtcDateTime.ToString("O");
            for (int index = 0; index < 4; index++)
            {
                var model = JsonNode.Parse(template.ToJsonString().Replace("Plant.", $"S{index}.Plant.", StringComparison.Ordinal))!;
                model["session"]!["sessionId"] = "history-" + index;
                var validation = IndustrialDataSim.Core.Configuration.SimulationDefinitionLoader.Load(model.ToJsonString());
                if (!validation.IsValid)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { outcome = "FixtureInvalid", errors = validation.Errors }));
                    return 1;
                }
                runtime.AddSession(model.ToJsonString());
            }
            var fake = new FakeHistorian { KeepHistory = false };
            var delivery = new SimulatedDelivery(runtime, fake);
            var windows = new List<object>();
            long batches = 0, archiveBytes = 0;
            double maxArchiveMs = 0;
            for (int round = 1; round <= rounds; round++)
            {
                if (clock.Elapsed > TimeSpan.FromMinutes(10))
                    throw new RuntimeFailure("capacity.deadline", "History probe exceeded ten minutes. Investigate local storage latency before interpreting the workload as qualified.");
                if (runtime.GenerateRound().Any(turn => !turn.Progressed))
                    throw new RuntimeFailure("capacity.generation_blocked", "A fixed history fixture did not progress. Inspect its state before accepting storage measurements.");
                if (await delivery.RunRoundAsync() != 4)
                    throw new RuntimeFailure("capacity.delivery_blocked", "The fake receiver did not complete all four batches. Do not treat incomplete work as a retention measurement.");
                batches += 4;
                if (round % 1000 != 0) continue;
                long before = StorageBytes(database);
                for (int index = 0; index < 4; index++)
                {
                    string id = "history-" + index;
                    runtime.Pause(id);
                    var status = runtime.GetSession(id);
                    var progress = runtime.Progress(id);
                    var timer = Stopwatch.StartNew();
                    var receipt = runtime.ArchiveHistory(id, archives, 1000);
                    maxArchiveMs = Math.Max(maxArchiveMs, timer.Elapsed.TotalMilliseconds);
                    runtime.VerifyArchiveSegment(id, receipt.ArchiveId, archives);
                    archiveBytes += receipt.Bytes;
                    if (receipt.Batches != 1000 || status != runtime.GetSession(id) || !progress.SequenceEqual(runtime.Progress(id)) || runtime.Batches(id).Count != 0)
                        throw new RuntimeFailure("capacity.rollover_mismatch", "Rollover changed process progress or failed to retain exactly the expected completed-history receipt. Stop qualification and investigate.");
                    runtime.Resume(id);
                }
                windows.Add(new { round, databaseAndWalBytesBefore = before, databaseAndWalBytesAfter = StorageBytes(database), archiveBytes });
            }
            if (runtime.ListSessions().Any(s => s.NextSlot != rounds * 8L || s.QueuedPoints != 0))
                throw new RuntimeFailure("capacity.cursor_mismatch", "Final fixed-workload cursors or queues differ from expectations. Correct the deterministic state failure before sizing storage.");
            Console.WriteLine(JsonSerializer.Serialize(new { outcome = "Measured", mode = "fake-only", sessions = 4, tags = 32,
                rounds, batches, points = rounds * 32L, segmentBatches = 1000, windows, archiveBytes,
                archiveBytesPerBatch = (double)archiveBytes / batches, maxArchiveMs,
                elapsedSeconds = clock.Elapsed.TotalSeconds, runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                note = "Accelerated live-cadence synthetic history. No production observations, sustained uptime or Historian throughput are measured." }));
            return 0;
        }
        catch (RuntimeFailure failure) { Console.WriteLine(JsonSerializer.Serialize(new { outcome = "Failed", error = failure.Error })); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine("capacity.storage: Cannot read fixtures or write temporary probe state. Check permissions and free space; no production data was used."); return 1; }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static long StorageBytes(string database) => new[] { database, database + "-wal", database + "-shm" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);
}
