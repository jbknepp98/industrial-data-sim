using System.Text.Json;
using System.Text;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Validation;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Cli;

/// <summary>
/// CLI boundary for offline validation, simulation lifecycle and explicit production commands.
/// Keeping it separate from process startup lets tests exercise the actual JSON
/// and exit codes. Only the production command group can authenticate or contact a Historian.
/// </summary>
public static partial class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Writes one bounded JSON response. Exit codes: 0 success, 1 validation/runtime
    /// refusal, 2 usage, 3 file/control access, 4 unfinished bounded worker, 130 graceful interrupt.
    /// </summary>
    public static int Run(string[] args, TextWriter output, CancellationToken stop = default)
    {
        if (args.Length > 0 && args[0] == "production") return RunProductionCommand(args, output, stop);
        if (args.Length > 0 && args[0] is "host" or "live") return RunHostCommand(args, output, stop);
        if (args.Length > 0 && args[0] == "session") return RunSessionCommand(args, output, stop);
        if (args.Length != 2 || args[0] is not ("validate-dataset" or "validate-session" or "validate-simulation" or "dry-run"))
        {
            WriteResult(output, [new("cli.usage", "$",
                "Usage: validate-dataset <name>, validate-session <file>, validate-simulation <file>, or dry-run <file>. Use session help for durable commands or host help for continuous execution and live controls. Quote arguments containing spaces.")]);
            return 2;
        }

        if (args[0] != "validate-dataset")
        {
            return RunFileCommand(args[0], args[1], output);
        }

        ValidationError? error = DatasetNameValidator.Validate(args[1]);
        WriteResult(output, error is null ? [] : [error]);
        return error is null ? 0 : 1;
    }

    private static int RunFileCommand(string command, string path, TextWriter output)
    {
        var input = ReadInputFile(path);
        if (input.Error is not null)
        {
            WriteResult(output, [input.Error]);
            return input.ExitCode;
        }
        if (command == "validate-session")
        {
            var header = SessionDefinitionLoader.Load(input.Json!);
            WriteResult(output, header.Errors);
            return header.IsValid ? 0 : 1;
        }

        var model = SimulationDefinitionLoader.Load(input.Json!);
        if (!model.IsValid || command == "validate-simulation")
        {
            WriteResult(output, model.Errors);
            return model.IsValid ? 0 : 1;
        }
        var preview = DryRun.Generate(model.Definition!);
        if (preview.Errors.Count > 0)
        {
            WriteResult(output, preview.Errors);
            return 1;
        }
        bool written = WriteResponse(output, new
        {
            schemaVersion = 1,
            valid = true,
            errors = Array.Empty<ValidationError>(),
            mode = "offline",
            sessionId = model.Definition!.Session.SessionId,
            dataset = model.Definition.Session.Dataset,
            generatorVersion = 1,
            pointCount = preview.PointCount,
            data = preview.Data
        }, new("dry_run.output_limit", "$",
            "Dry-run response exceeds 4 MiB. Reduce the range, output count, or constant size."));
        return written ? 0 : 1;
    }

    private static (string? Json, ValidationError? Error, int ExitCode) ReadInputFile(string path)
    {
        try
        {
            // Bound the actual read, not FileInfo.Length: input may grow after
            // a preflight size check. All configuration files are limited to 1 MiB.
            const int maxBytes = 1024 * 1024;
            using var file = File.OpenRead(path);
            byte[] buffer = new byte[maxBytes + 1];
            int count = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count > maxBytes)
            {
                return (null, new("session.file_too_large", "$", "Configuration files must not exceed 1 MiB (1048576 bytes). Reduce the configuration size or split it into smaller sessions."), 1);
            }
            // Accept an editor's BOM, but do not replace malformed UTF-8 bytes.
            int offset = count >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
            return (new UTF8Encoding(false, true).GetString(buffer, offset, count - offset), null, 0);
        }
        catch (DecoderFallbackException)
        {
            return (null, new("session.invalid_encoding", "$", "Configuration files must use valid UTF-8. Save the file as UTF-8 and rerun the command."), 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Keep paths and exception details out of diagnostics. This boundary
            // covers reading only: output or programming failures aren't mislabeled.
            return (null, new("session.file_unreadable", "$", "Could not read the configuration file. Check that the supplied path names an existing file and that you have read permission."), 3);
        }
    }

    private static void WriteResult(TextWriter output, IReadOnlyList<ValidationError> errors)
    {
        // Version this small response envelope independently of the future
        // session schema. No caller-supplied argument is reflected in output.
        WriteResponse(output, new
        {
            schemaVersion = 1,
            valid = errors.Count == 0,
            errors
        }, new("cli.output_limit", "$",
            "The response exceeds 4 MiB. Reduce the configuration size, correct any known validation issues, and rerun the command."));
    }

    private static bool WriteResponse<T>(TextWriter output, T response, ValidationError limitError)
    {
        // Stage every response before emitting it, including validation errors.
        // A fixed LF terminator makes the UTF-8 byte limit independent of the
        // TextWriter's configured newline. Failed serialization emits no prefix.
        using var buffer = new BoundedJsonBuffer(4 * 1024 * 1024 - 1);
        try
        {
            JsonSerializer.Serialize(buffer, response, JsonOptions);
        }
        catch (OutputLimitException)
        {
            // This fixed, small envelope contains no caller-provided content.
            output.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, valid = false, errors = new[] { limitError }
            }, JsonOptions));
            output.Write('\n');
            return false;
        }
        output.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        output.Write('\n');
        return true;
    }
}
