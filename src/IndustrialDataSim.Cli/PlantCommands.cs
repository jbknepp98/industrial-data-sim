using System.Globalization;
using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Validation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string PlantUsage =
        "Usage: plant compose <storylines-folder> <output-model.json> <SimulationName> <storylineId>...; plant validate <model> [anchorUtc]; plant preview <model> <output.json> [hoursAfterAnchor 1-720, default 24] [anchorUtc]; " +
        "plant connect-spec <model> <output-folder> [anchorUtc]; plant build <model> [anchorUtc]; plant sync <simulation> [throughUtc]; plant status [simulation]; plant remove <simulation> --confirm; plant run <simulation>; plant gage-check. " +
        "build, sync, status, remove and run use FLOWSIM_PG_CONNECTION; run also uses the TIMEBASE_* settings. " +
        "gage-check uses TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET, TIMEBASE_CA_BUNDLE and FLOWSIM_GAGE_MODEL (default anthropic/claude). " +
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
        if (action == "connect-spec") return RunConnectSpec(args, output);
        if (action == "compose") return RunCompose(args, output);
        if (action == "gage-check") return RunGageCheck(args, output, stop);
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

    /// <summary>
    /// plant compose: builds a plant model from the storyline library
    /// (storylines.json and bases/ in the given folder), the chosen storylines
    /// and a simulation name. Offline; writes only the output model file.
    /// </summary>
    private static int RunCompose(string[] args, TextWriter output)
    {
        if (args.Length < 6)
        {
            WriteResult(output, [new("cli.usage", "$", PlantUsage)]);
            return 2;
        }
        string folder = args[2], target = args[3], simulation = args[4];
        var library = ReadPlainFile(Path.Combine(folder, "storylines.json"));
        if (library.Error is not null) { WriteResult(output, [library.Error]); return library.ExitCode; }
        var loaded = PlantStorylines.Load(library.Json!);
        if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
        var chosen = new List<PlantStoryline>();
        foreach (var id in args.Skip(5))
        {
            if (loaded.Library!.Find(id) is { } storyline) chosen.Add(storyline);
            else { WriteResult(output, [new("storyline.unknown", "$", $"No storyline has the id \"{id}\". Use an id from storylines.json.")]); return 1; }
        }
        if (chosen.FirstOrDefault(c => !c.Available) is { } waiting)
        {
            WriteResult(output, [new("storyline.not_available", "$", $"\"{waiting.Title}\" is {waiting.Engine}, not available yet. Choose an available storyline.")]);
            return 1;
        }
        string baseId = chosen[0].Base ?? "";
        var baseFile = ReadPlainFile(Path.Combine(folder, "bases", baseId + ".json"));
        if (baseId.Length == 0 || baseFile.Error is not null)
        {
            WriteResult(output, [new("storyline.base_missing", "$", $"\"{chosen[0].Title}\" has no base plant in {folder}/bases. It may not be available yet.")]);
            return 1;
        }
        var composed = PlantStorylines.Compose(baseId, baseFile.Json!, chosen, new System.Text.Json.Nodes.JsonObject { ["simulation"] = simulation });
        if (!composed.IsValid) { WriteResult(output, composed.Errors); return 1; }
        var check = PlantModelLoader.Load(composed.ModelJson!, DateTimeOffset.UtcNow);
        if (!check.IsValid) { WriteResult(output, check.Errors); return 1; }
        try { File.WriteAllText(target, composed.ModelJson + "\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteResult(output, [new("plant.model_unwritable", "$", "Could not write the model file. Check that its folder exists and that you have write permission.")]);
            return 3;
        }
        return WriteResponse(output, new
        {
            schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), simulation, @base = baseId,
            storylines = chosen.Select(c => c.Id), model = target,
            notice = "Model written. Validate and preview it, then get approval before plant build."
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
                    result = new { registration.Simulation, registration.TablePrefix, registration.AnchorUtc, registration.StartUtc, tables = PlantSchema.TablesFor(loaded.Model!).Count,
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
                        ? (object)(store.Registration(args[2]) is { } one ? Status(store, one) : throw new RuntimeFailure("plant.not_admitted", "No simulation with this name is registered."))
                        : new { simulations = store.List().Select(r => Status(store, r)).ToList() };
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
        host.Notice = (name, message) => Line(new { @event = name, message });
        Line(new { @event = "plant.started", simulation, dataset, startUtc = model.StartUtc, lookaheadHours = host.LookaheadHours });
        // Historian drops points older than the Dataset's purge age. Say so up front
        // instead of letting the oldest backfill disappear without explanation.
        historian.Authenticate(CancellationToken.None).GetAwaiter().GetResult();
        var settings = historian.ReadDatasetSettings(dataset, CancellationToken.None).GetAwaiter().GetResult();
        if (settings.PurgeAgeDays > 0 && model.StartUtc < DateTimeOffset.UtcNow.AddDays(-settings.PurgeAgeDays))
            Line(new { @event = "plant.retention_warning", purgeAgeDays = settings.PurgeAgeDays,
                message = "The backfill starts before this Dataset's purge age, so Historian will drop the oldest tag points. Postgres keeps every row. Use a shorter backfill or a Dataset with a longer purge age to keep them." });
        bool caughtUp = false;
        var lastReport = DateTimeOffset.MinValue;
        long rows = 0, points = 0;
        try
        {
            host.RunAsync(cycle =>
            {
                if (cycle.Batches < 0) { Line(new { @event = "plant.interrupted_batch_resent", message = "The tag batch in flight when the plant last stopped unexpectedly is being sent again. Plant tags are deterministic, so resent points are identical." }); return; }
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

    private static object Status(PlantStore store, PlantRegistration r) => new
    {
        r.Simulation, r.TablePrefix, r.AnchorUtc, r.StartUtc, r.AppliedSequence, r.TagCursorUtc,
        snapshotUtc = store.SnapshotUtc(r.Simulation),
        resentBatches = r.UncertainBatches, // batches interrupted by a crash or power loss and sent again
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

    /// <summary>Writes connect-spec.md and connect-spec.json (with the Atlas starter) for a plant model. Offline.</summary>
    /// <summary>
    /// plant gage-check: proves the interview can reach a model through Pulse's
    /// Gage proxy (token, model catalog, then a one-sentence test message), or
    /// explains which Pulse setting is missing and stops. Sends one short
    /// message, which Pulse meters under the client's policy.
    /// </summary>
    private static int RunGageCheck(string[] args, TextWriter output, CancellationToken stop)
    {
        if (args.Length != 2) { WriteResult(output, [new("cli.usage", "$", "plant gage-check takes no arguments. " + PlantUsage)]); return 2; }
        GageCheckResult check;
        try
        {
            using var gage = new GageClient(GageSettings.FromEnvironment());
            check = gage.Check(stop).GetAwaiter().GetResult();
        }
        catch (RuntimeFailure failure)
        {
            WriteResult(output, [new(failure.Error.Code, "$", failure.Error.Message)]);
            return 1;
        }
        var result = new
        {
            model = check.Model, answeredBy = check.AnsweredBy, reply = check.Reply,
            usage = new { inputTokens = check.InputTokens, outputTokens = check.OutputTokens },
            availableModels = check.AvailableModels,
            message = "The interview can reach a model through Pulse's Gage proxy. Pulse → Gage → Usage shows this call under the client."
        };
        return WriteResponse(output, new { schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), result },
            new("cli.output_limit", "$", "Response too large.")) ? 0 : 1;
    }

    private static int RunConnectSpec(string[] args, TextWriter output)
    {
        if (args.Length is not (4 or 5)) { WriteResult(output, [new("cli.usage", "$", PlantUsage)]); return 2; }
        var anchor = ParseAnchor(args.Length == 5 ? args[4] : null);
        if (anchor is null) { WriteResult(output, [new("cli.usage", "$", "anchorUtc must be an ISO 8601 UTC time ending in Z. " + PlantUsage)]); return 2; }
        var input = ReadPlainFile(args[2]);
        if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
        var loaded = PlantModelLoader.Load(input.Json!, anchor.Value);
        if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
        try
        {
            Directory.CreateDirectory(args[3]);
            File.WriteAllText(Path.Combine(args[3], "connect-spec.md"), PlantConnectSpec.Markdown(loaded.Model!));
            File.WriteAllText(Path.Combine(args[3], "connect-spec.json"), JsonSerializer.Serialize(PlantConnectSpec.Json(loaded.Model!), new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteResult(output, [new("plant.spec_unwritable", "$", "Could not write the spec files. Check that the folder can be created and that you have write permission.")]);
            return 3;
        }
        return WriteResponse(output, new { schemaVersion = 1, valid = true, errors = Array.Empty<ValidationError>(), simulation = loaded.Model!.Simulation,
            files = new[] { "connect-spec.md", "connect-spec.json" }, tables = PlantSchema.TablesFor(loaded.Model!).Count },
            new("cli.output_limit", "$", "Response too large.")) ? 0 : 1;
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
