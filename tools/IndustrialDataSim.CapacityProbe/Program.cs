using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

// Fixed synthetic fixtures make measurements comparable without accepting a
// production configuration. Run each case in a fresh process: peak working set
// includes startup/JIT and must not inherit a previous case's high-water mark.
if (args.Length != 2 || args[0] is not ("constant" or "gate") ||
    !int.TryParse(args[1], out int batchPoints) || batchPoints is not (100 or 1000))
{
    Console.Error.WriteLine("capacity.usage: Supply constant|gate and 100|1000 batch points. Build in Release first. This probe uses temporary fake state only.");
    return 2;
}

string folder = Path.Combine(Path.GetTempPath(), "sim-capacity-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(folder);
    string database = Path.Combine(folder, "state.db");
    var limits = new RuntimeLimits
    {
        BatchPoints = batchPoints,
        SessionQueuePoints = batchPoints * 2,
        GlobalQueuePoints = batchPoints * 8
    };
    double elapsedSeconds, maximumRoundMs = 0, maximumStatusMs = 0;
    long maximumQueuedPoints = 0, maximumQueuedBytes = 0, maximumStorageBytes = 0;
    long points = 0, batches = 0, emptyPayloadBatches = 0, initialStorageBytes;
    long maximumSampledManagedBytes = 0, maximumSampledWorkingSetBytes = 0;
    using var process = Process.GetCurrentProcess();
    int rounds = 0, nonprogressingGenerationTurns = 0;
    using (var runtime = new DurableRuntime(database, limits))
    {
        string fixture = args[0] == "constant" ? "constant-simulation.json" : "boolean-gate-simulation.json";
        for (int sessionIndex = 0; sessionIndex < 4; sessionIndex++)
        {
            var model = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", fixture)))!;
            var session = model["session"]!;
            session["sessionId"] = "capacity-" + sessionIndex;
            session["startUtc"] = "2026-09-01T00:00:00Z";
            session["endUtc"] = "2026-09-08T00:00:00Z";
            model["samplingIntervalMs"] = 60000;
            foreach (var tag in session["outputTags"]!.AsArray())
                tag!["name"] = "S" + sessionIndex + "." + tag["name"]!.GetValue<string>();
            foreach (var generator in model["generators"]!.AsArray())
            {
                generator!["tag"] = "S" + sessionIndex + "." + generator["tag"]!.GetValue<string>();
                if (generator["triggerTag"] is JsonNode trigger)
                    generator["triggerTag"] = "S" + sessionIndex + "." + trigger.GetValue<string>();
                // Stretch the 20-second gate fixture across the entire seven-day
                // backfill so both suppression policies are exercised throughout.
                if (generator["kind"]!.GetValue<string>() == "booleanTimeline")
                    foreach (var step in generator["steps"]!.AsArray())
                        step!["durationMs"] = step["durationMs"]!.GetValue<long>() * 30240;
            }
            runtime.AddSession(model.ToJsonString());
        }
        initialStorageBytes = StorageBytes(database);
        var delivery = new SimulatedDelivery(runtime, new FakeHistorian { KeepHistory = false });
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Require(clock.Elapsed < TimeSpan.FromMinutes(5) && rounds < 10000,
                "capacity.deadline: Probe exceeded five minutes or 10000 rounds. Inspect local disk load and rerun the smaller batch case independently.");
            var roundClock = Stopwatch.StartNew();
            var generation = runtime.GenerateRound();
            nonprogressingGenerationTurns += generation.Count(turn => !turn.Progressed);
            var statusClock = Stopwatch.StartNew();
            var sessions = runtime.ListSessions();
            maximumStatusMs = Math.Max(maximumStatusMs, statusClock.Elapsed.TotalMilliseconds);
            long queuedPoints = sessions.Sum(session => session.QueuedPoints);
            maximumQueuedPoints = Math.Max(maximumQueuedPoints, queuedPoints);
            maximumQueuedBytes = Math.Max(maximumQueuedBytes, sessions.Sum(session => session.QueuedBytes));
            Require(queuedPoints <= limits.GlobalQueuePoints && sessions.All(session => session.QueuedPoints <= limits.SessionQueuePoints),
                "capacity.queue_bound: Observed queue exceeded configured point limits. Stop capacity tuning and inspect runtime queue accounting.");
            Require(sessions.All(session => session.Status is SessionStatus.Ready or SessionStatus.Draining or SessionStatus.Complete),
                "capacity.session_failed: A synthetic session stopped unexpectedly. Reproduce with runtime tests before interpreting performance results.");
            // Deliver only every fourth generation round to exercise bounded
            // backlog. This is scheduling pressure, not a model of network delay.
            rounds++;
            if (rounds % 4 == 0) await delivery.RunRoundAsync();
            maximumStorageBytes = Math.Max(maximumStorageBytes, StorageBytes(database));
            maximumSampledManagedBytes = Math.Max(maximumSampledManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
            process.Refresh();
            maximumSampledWorkingSetBytes = Math.Max(maximumSampledWorkingSetBytes, process.WorkingSet64);
            maximumRoundMs = Math.Max(maximumRoundMs, roundClock.Elapsed.TotalMilliseconds);
            if (runtime.ListSessions().All(session => session.Status == SessionStatus.Complete)) break;
        }
        elapsedSeconds = clock.Elapsed.TotalSeconds;
        foreach (var session in runtime.ListSessions())
        {
            Require(session.NextSlot == session.TotalSlots && session.QueuedPoints == 0 && session.QueuedBytes == 0,
                "capacity.incomplete: Finished session retained work or an unfinished cursor. Inspect durable completion tests before using these measurements.");
            long after = 0;
            while (true)
            {
                var page = runtime.Batches(session.SessionId, after, 1000, includePayload: true);
                if (page.Count == 0) break;
                Require(page.All(batch => batch.Status == BatchStatus.Acknowledged),
                    "capacity.delivery_state: Fake delivery left an unacknowledged batch. Investigate delivery state before accepting the baseline.");
                batches += page.Count;
                points += page.Sum(batch => (long)batch.PointCount);
                emptyPayloadBatches += page.Count(batch => string.IsNullOrEmpty(batch.Payload));
                after = page[^1].Id;
            }
        }
        Require(batches > 0 && points > 0 && emptyPayloadBatches == batches,
            "capacity.payload_retention: Completed fake batches were missing or retained payload. Inspect pruning behavior before estimating audit storage.");
        Require(points == (args[0] == "constant" ? 120960 : 84672),
            "capacity.point_count: Synthetic workload emitted an unexpected number of points. Check fixture timing and suppression semantics before comparing measurements.");
    }
    // Closing SQLite checkpoints WAL. This is total database size, not an exact
    // row-size estimate: pages, indexes, configuration and free pages all count.
    long closedDatabaseBytes = new FileInfo(database).Length;
    process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1, mode = "simulation-only", pattern = args[0], batchPoints,
        sessions = 4, tagsPerSession = 3, days = 7, samplingIntervalMs = 60000,
        os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Linux",
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = Environment.Version.ToString(),
        elapsedSeconds, points, batches, pointsPerSecond = points / elapsedSeconds,
        rounds, nonprogressingGenerationTurns, maximumQueuedPoints, maximumQueuedBytes,
        maximumRoundMs, maximumStatusMs, maximumSampledManagedBytes,
        maximumSampledWorkingSetBytes = maximumSampledWorkingSetBytes > 0 ? (long?)maximumSampledWorkingSetBytes : null,
        // Some platforms return zero for this unsupported process metric. Null
        // means unavailable; never report zero as measured memory consumption.
        peakProcessWorkingSetBytes = process.PeakWorkingSet64 > 0 ? (long?)process.PeakWorkingSet64 : null,
        initialStorageBytes, maximumStorageBytes, closedDatabaseBytes,
        closedDatabaseBytesPerBatch = (double)closedDatabaseBytes / batches
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (RuntimeFailure failure)
{
    Console.Error.WriteLine($"{failure.Error.Code}: {failure.Error.Message}");
    return 1;
}
catch (InvalidOperationException failure) when (failure.Message.StartsWith("capacity.", StringComparison.Ordinal))
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine("capacity.environment: Could not read fixtures or temporary state. Check the Release build, temporary directory permissions and free disk space; rerun in a fresh process.");
    return 1;
}
finally
{
    try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("capacity.cleanup: Temporary synthetic state could not be removed. Close processes holding temporary files and use your operating system's temporary-file cleanup.");
    }
}

static long StorageBytes(string database) => new[] { database, database + "-wal", database + "-shm" }
    .Where(File.Exists).Sum(path => new FileInfo(path).Length);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
