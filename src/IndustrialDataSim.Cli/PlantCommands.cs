using System.Globalization;
using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string PlantUsage =
        "Usage: plant validate <model> [anchorUtc]; plant preview <model> <output.json> [hoursAfterAnchor 1-720, default 24] [anchorUtc]. " +
        "The anchor is \"now\" unless given (UTC ending in Z): backfill and incident times are relative to it. Preview runs offline and writes nothing else.";

    /// <summary>
    /// Plant models: offline validation and preview (milestone 1). The preview
    /// file can be large, so it is written to the given path and only a small
    /// summary is printed.
    /// </summary>
    private static int RunPlantCommand(string[] args, TextWriter output)
    {
        string action = args.Length > 1 ? args[1] : "";
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
        if (model.EndUtc is { } stop && stop < end) end = stop;
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
