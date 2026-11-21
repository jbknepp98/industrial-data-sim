using System.Globalization;
using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Validation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string PlantUsage =
        "Usage: plant validate <model> [anchorUtc]; plant preview <model> <output.json> [hoursAfterAnchor 1-720, default 24] [anchorUtc]; " +
        "plant build <model> [anchorUtc]; plant sync <simulation> [throughUtc]; plant status [simulation]; plant remove <simulation> --confirm; plant run <simulation>. " +
        "build, sync, status, remove and run use FLOWSIM_PG_CONNECTION; run also uses the TIMEBASE_* settings. " +
        "The anchor is \"now\" unless given (UTC ending in Z): backfill and incident times are relative to it. Preview runs offline and writes nothing else.";

    /// <summary>
    /// Plant models: offline validation and preview (milestone 1). The preview
    /// file can be large, so it is written to the given path and only a small
    /// summary is printed.
    /// </summary>
    private static int RunPlantCommand(string[] args, TextWriter output, CancellationToken stop = default)
    {
        string action = args.Length > 1 ? args[1] : "";
        if (action is "build" or "sync" or "status" or "remove" or "run") return RunPlantDatabaseCommand(args, output, stop);
        bool validate = action == "validate" && args.Length is 3 or 4;
        bool preview = action == "preview" && args.Length is >= 4 and <= 6;
        if (!validate && !preview)
        {
            WriteResult(output, [new("cli.usage", "$", PlantUsage)]);
            return 2;
        }
        int hours = 24;
        string? anchorText = validate ? (args.Length == 4 ? args[3] : null) : (args.Length == 6 ? args[5] : null);
        if (preview && args.Length >= 5 && (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours is < 1 or > 720))
        {
            WriteResult(output, [new("cli.usage", "$", "hoursAfterAnchor must be a whole number from 1 through 720. " + PlantUsage)]);
            return 2;
        }
        var anchor = DateTimeOffset.UtcNow;
        if (anchorText is not null && (!anchorText.EndsWith('Z') ||
            !DateTimeOffset.TryParse(anchorText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out anchor)))
        {
            WriteResult(output, [new("cli.usage", "$", "anchorUtc must be an ISO 8601 UTC time ending in Z. " + PlantUsage)]);
            return 2;
        }
        // Whole seconds keep generated record timestamps tidy and reproducible.
        anchor = new DateTimeOffset(anchor.UtcTicks - anchor.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var input = ReadPlainFile(args[2]);
        if (input.Error is not null)
        {
            WriteResult(output, [input.Error]);
            return input.ExitCode;
        }
        var loaded = PlantModelLoader.Load(input.Json!, anchor);
        if (!loaded.IsValid)
        {
            WriteResult(output, loaded.Errors);
            return 1;
        }
        var model = loaded.Model!;
        if (validate)
        {
            return WriteResponse(output, new
            {
                schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), simulation = model.Simulation,
                tablePrefix = model.TablePrefix, startUtc = model.StartUtc, endUtc = model.EndUtc, anchorUtc = model.AnchorUtc,
                lines = model.Lines.Count, tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Count
            }, new("cli.output_limit", "$", "Response too large.")) ? 0 : 1;
        }

        var end = model.AnchorUtc.AddHours(hours);
        if (model.EndUtc is { } modelEnd && modelEnd < end) end = modelEnd;
        var run = PlantEngine.Run(model, end);
        var document = PlantPreview.Build(run, sampleSeconds: 60);
        try
        {
            using var file = new FileStream(args[3], FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, document, JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteResult(output, [new("plant.preview_unwritable", "$", "Could not write the preview file. Check that its folder exists and that you have write permission.")]);
            return 3;
        }
        var tables = PlantPreview.FinalTables(run);
        return WriteResponse(output, new
        {
            schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), simulation = model.Simulation,
            startUtc = run.StartUtc, endUtc = run.EndUtc, anchorUtc = model.AnchorUtc,
            rows = tables.ToDictionary(t => model.TablePrefix + t.Key, t => t.Value.Count),
            breakdowns = run.Failures.Count, changes = run.Changes.Count,
            notice = "Preview written. Nothing was written to Historian or Postgres."
        }, new("cli.output_limit", "$", "Response too large.")) ? 0 : 1;
    }

    private static int RunPlantDatabaseCommand(string[] args, TextWriter output, CancellationToken stop)
    {
        string action = args[1];
        bool shape = action switch
        {
            "build" => args.Length is 3 or 4,
            "sync" => args.Length is 3 or 4,
            "status" => args.Length is 2 or 3,
            "remove" => args.Length == 4 && args[3] == "--confirm",
            "run" => args.Length == 3,
            _ => false
        };
        if (!shape)
        {
            WriteResult(output, [new("cli.usage", "$", action == "remove" ? "plant remove needs the simulation name and --confirm. It drops that simulation's tables and registration; nothing else is touched. " + PlantUsage : PlantUsage)]);
            return 2;
        }
        object result;
        try
        {
            using var store = PlantStore.FromEnvironment();
            switch (action)
            {
                case "build":
                {
                    var anchor = ParseAnchor(args.Length == 4 ? args[3] : null);
                    if (anchor is null) { WriteResult(output, [new("cli.usage", "$", "anchorUtc must be an ISO 8601 UTC time ending in Z. " + PlantUsage)]); return 2; }
                    var input = ReadPlainFile(args[2]);
                    if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
                    var loaded = PlantModelLoader.Load(input.Json!, anchor.Value);
                    if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
                    var registration = store.Admit(loaded.Model!, input.Json!);
                    result = new { registration.Simulation, registration.TablePrefix, registration.AnchorUtc, registration.StartUtc, tables = PlantSchema.Tables.Count,
                        message = "Plant admitted and its tables created. No rows or tags were written; run plant sync or plant run." };
                    break;
                }
                case "sync":
                {
                    var (model, registration) = LoadRegistered(store, args[2]);
                    var through = args.Length == 4 ? ParseAnchor(args[3]) : DateTimeOffset.UtcNow;
                    if (through is null) { WriteResult(output, [new("cli.usage", "$", "throughUtc must be an ISO 8601 UTC time ending in Z. " + PlantUsage)]); return 2; }
                    if (through > DateTimeOffset.UtcNow) through = DateTimeOffset.UtcNow; // never write the future
                    var run = PlantEngine.Run(model, through.Value.AddSeconds(1));
                    int applied = store.ApplyChanges(model, run.Changes, through.Value);
                    result = new { simulation = model.Simulation, throughUtc = through, applied, appliedSequence = store.Registration(model.Simulation)!.AppliedSequence };
                    break;
                }
                case "status":
                    result = args.Length == 3
                        ? (object)(store.Registration(args[2]) is { } one ? Status(one) : throw new RuntimeFailure("plant.not_admitted", "No simulation with this name is registered."))
                        : new { simulations = store.List().Select(Status) };
                    break;
                case "remove":
                    result = new { simulation = args[2], droppedTables = store.Remove(args[2]), message = "Tables and registration removed. Historian tags already published are not affected." };
                    break;
                default:
                    return RunPlantHost(store, args[2], output, stop);
            }
        }
        catch (RuntimeFailure failure)
        {
            WriteResult(output, [new(failure.Error.Code, "$", failure.Error.Message)]);
            return 1;
        }
        return WriteResponse(output, new { schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), result },
            new("cli.output_limit", "$", "Response too large.")) ? 0 : 1;
    }

    /// <summary>
    /// plant run: delivers an admitted plant until SIGINT (docker stop with
    /// --stop-signal SIGINT). Prints one JSON line when a condition changes
    /// (started, backfill progress each 10 minutes, caught up, stopped).
    /// </summary>
    private static int RunPlantHost(PlantStore store, string simulation, TextWriter output, CancellationToken stop)
    {
        var (model, _) = LoadRegistered(store, simulation);
        string dataset = Environment.GetEnvironmentVariable("TIMEBASE_DATASET") is { Length: > 0 } d ? d
            : throw new RuntimeFailure("connection.missing_setting", "Set TIMEBASE_DATASET to the Historian Dataset the plant's tags are written to.");
        using var hostLock = store.LockSimulation(simulation);
        using var historian = new HistorianClient(HistorianConnection.FromEnvironment());
        var host = new PlantHost(store, model, historian, dataset);
        void Line(object value) { output.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); output.Flush(); }
        Line(new { @event = "plant.started", simulation, dataset, startUtc = model.StartUtc, lookaheadHours = host.LookaheadHours });
        bool caughtUp = false;
        var lastReport = DateTimeOffset.MinValue;
        long rows = 0, points = 0;
        try
        {
            host.RunAsync(cycle =>
            {
                if (cycle.Batches < 0) { Line(new { @event = "plant.uncertain_batch_skipped", message = "A tag batch from an interrupted run was skipped, not resent." }); return; }
                rows += cycle.RowsApplied; points += cycle.PointsPublished;
                if (cycle.CaughtUp != caughtUp || DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(10))
                {
                    Line(new { @event = cycle.CaughtUp ? "plant.live" : "plant.backfilling", throughUtc = cycle.ThroughUtc, rowsApplied = rows, pointsPublished = points });
                    caughtUp = cycle.CaughtUp;
                    lastReport = DateTimeOffset.UtcNow;
                }
            }, stop).GetAwaiter().GetResult();
        }
        catch (RuntimeFailure failure)
        {
            Line(new { @event = "plant.stopped_on_error", code = failure.Error.Code, message = failure.Error.Message, rowsApplied = rows, pointsPublished = points });
            return 1;
        }
        Line(new { @event = "plant.stopped", rowsApplied = rows, pointsPublished = points, message = "Stopped gracefully. Restart with plant run to continue exactly where it left off." });
        return stop.IsCancellationRequested ? 130 : 0;
    }

    private static object Status(PlantRegistration r) => new
    {
        r.Simulation, r.TablePrefix, r.AnchorUtc, r.StartUtc, r.AppliedSequence, r.TagCursorUtc, r.UncertainBatches,
        sendingInProgress = r.SendingUntilUtc is not null
    };

    private static (PlantModel Model, PlantRegistration Registration) LoadRegistered(PlantStore store, string simulation)
    {
        var registration = store.Registration(simulation)
            ?? throw new RuntimeFailure("plant.not_admitted", "No simulation with this name is registered. Run plant build first.");
        // The stored model and anchor reproduce exactly the history already written.
        var loaded = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc);
        if (!loaded.IsValid)
            throw new RuntimeFailure("plant.stored_model_invalid", "The stored model no longer validates with this version of the simulator. Use the version that admitted it, or remove and rebuild the simulation.");
        return (loaded.Model!, registration);
    }

    private static DateTimeOffset? ParseAnchor(string? text)
    {
        if (text is null) return new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return text.EndsWith('Z') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime() : null;
    }

    /// <summary>Reads a plant model with the shared bounded reader, without backfill rewriting (plants resolve their own timeline).</summary>
    private static (string? Json, ValidationError? Error, int ExitCode) ReadPlainFile(string path)
    {
        try
        {
            const int maxBytes = 1024 * 1024;
            using var file = File.OpenRead(path);
            byte[] buffer = new byte[maxBytes + 1];
            int count = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count > maxBytes)
                return (null, new("session.file_too_large", "$", "Configuration files must not exceed 1 MiB (1048576 bytes)."), 1);
            int offset = count >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
            return (new System.Text.UTF8Encoding(false, true).GetString(buffer, offset, count - offset), null, 0);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return (null, new("session.invalid_encoding", "$", "Configuration files must use valid UTF-8. Save the file as UTF-8 and rerun the command."), 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, new("session.file_unreadable", "$", "Could not read the configuration file. Check that the path names an existing file and that you have read permission."), 3);
        }
    }
}
