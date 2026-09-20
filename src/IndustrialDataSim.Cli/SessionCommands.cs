using System.Globalization;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Validation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string SessionUsage =
        "Usage: session start <database> <model-file>; session list <database> [after-session-id]; " +
        "session status|pause|resume|release <database> <session-id>; " +
        "session archive|verify-archive <database> <session-id> <archive-directory>; session archive-info <database> <session-id>; " +
        "session cancel <database> <session-id> drain|discard-pending; " +
        "session retry-generation <database> <session-id> <batch-bytes>; " +
        "session run-simulated <database> <maximum-rounds> [batch-bytes]; " +
        "session batches <database> <session-id> [after-batch-id]. " +
        "Start admits a simulation-only session; it does not launch a worker. Quote arguments containing spaces.";

    private static int RunSessionCommand(string[] args, TextWriter output, CancellationToken stop)
    {
        if (args.Length == 2 && args[1] == "help")
            return WriteSessionResponse(output, "help", new { message = SessionUsage });
        string action = args.Length > 1 ? args[1] : "";
        bool validShape = action switch
        {
            "start" or "status" or "pause" or "resume" or "release" or "archive-info" => args.Length == 4,
            "list" => args.Length is 3 or 4,
            "cancel" or "retry-generation" or "archive" or "verify-archive" => args.Length == 5,
            "batches" or "run-simulated" => args.Length is 4 or 5,
            _ => false
        };
        int batchBytes = 0;
        int maximumRounds = 0;
        long afterBatch = 0;
        if (!validShape ||
            (action == "cancel" && args[4] is not ("drain" or "discard-pending")) ||
            (action == "retry-generation" && (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out batchBytes) || batchBytes is < 128 or > 4194304)) ||
            (action == "run-simulated" && (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out maximumRounds) || maximumRounds is < 1 or > 10000 ||
                (args.Length == 5 && (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out batchBytes) || batchBytes is < 128 or > 4194304)))) ||
            (action == "batches" && args.Length == 5 && (!long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out afterBatch) || afterBatch < 0)))
        {
            WriteResult(output, [new("cli.usage", "$", SessionUsage + " Worker rounds must be 1–10000; batch bytes must be 128–4194304; batch cursors must be nonnegative integers.")]);
            return 2;
        }

        string? configuration = null;
        string? admittedId = null;
        if (action == "start")
        {
            // Validate before opening/creating a database or reserving tags. Use
            // the same bounded file reader and precise model diagnostics as validate.
            var input = ReadInputFile(args[3]);
            if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
            var loaded = SimulationDefinitionLoader.Load(input.Json!);
            if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
            configuration = input.Json;
            admittedId = loaded.Definition!.Session.SessionId;
        }

        object result;
        try
        {
            string database = Path.GetFullPath(args[2]);
            if (action != "start" && !File.Exists(database))
                throw new RuntimeFailure("cli.state_missing",
                    "The state database was not found or is inaccessible. Check its location and permissions; use session start to create a new simulation database. Inspection does not create an empty database.");
            // The logger outlives runtime disposal. Files stay separate from the
            // single stdout JSON response; no credentials or payloads are logged.
            using var logs = new RuntimeFileLogger(Path.Combine(Path.GetDirectoryName(database)!, "logs", Path.GetFileName(database)));
            using var runtime = new DurableRuntime(database,
                batchBytes > 0 ? new() { BatchBytes = batchBytes } : null,
                logs, createIfMissing: action == "start");
            result = action == "run-simulated"
                ? new SimulationWorker(runtime).RunAsync(maximumRounds, stop).GetAwaiter().GetResult()
                : ExecuteSessionCommand(runtime, action, args, configuration, admittedId, afterBatch);
        }
        catch (RuntimeFailure failure)
        {
            WriteResult(output, [failure.Error]);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteResult(output, [new("cli.state_access", "$",
                "Could not access the local state or log location. Check the database path, directory permissions, and free disk space. Inspect session status before repeating a mutation; it may have committed before the failure.")]);
            return 3;
        }
        // Serialize only after the runtime closes. Output failures must not be
        // mislabeled as input/storage errors, and must never trigger a retry.
        if (result is WorkerRunResult worker)
        {
            int writeExit = WriteSessionResponse(output, action, new
            {
                stopReason = worker.StopReason.ToString(), worker.RoundsStarted,
                worker.ProgressingGenerationTurns, worker.AcknowledgedBatches, worker.Message,
                sessions = worker.Sessions.Select(SessionView).ToArray()
            });
            if (writeExit != 0) return writeExit;
            return worker.StopReason == WorkerStopReason.Completed ? 0 : worker.StopReason == WorkerStopReason.Stopped ? 130 : 4;
        }
        return WriteSessionResponse(output, action, result);
    }

    private static object ExecuteSessionCommand(DurableRuntime runtime, string action, string[] args,
        string? configuration, string? admittedId, long afterBatch)
    {
        if (action == "list")
        {
            var sessions = runtime.ListSessions(args.Length == 4 ? args[3] : null);
            return new { sessions = sessions.Select(SessionView).ToArray(), nextCursor = sessions.Count == 100 ? sessions[^1].SessionId : null };
        }
        string id = action == "start" ? admittedId! : args[3];
        if (action == "archive") return runtime.Archive(id, args[4]);
        if (action == "archive-info") return runtime.GetArchive(id);
        if (action == "verify-archive")
        {
            runtime.VerifyArchive(id, args[4]);
            return new { message = "Archive matches its durable receipt. This is audit evidence, not permission to replay data." };
        }
        if (action == "batches")
        {
            var batches = runtime.Batches(id, afterBatch, 100, includePayload: false);
            return new
            {
                sessionId = id,
                batches = batches.Select(b => new { b.Id, status = b.Status.ToString(), b.StartSlot, b.EndSlot, b.PointCount, b.ByteCount }).ToArray(),
                nextCursor = batches.Count == 100 ? (long?)batches[^1].Id : null
            };
        }
        switch (action)
        {
            case "start": runtime.AddSession(configuration!); break;
            case "pause": runtime.Pause(id); break;
            case "resume": runtime.Resume(id); break;
            case "cancel": runtime.Cancel(id, args[4] == "drain" ? CancellationMode.Drain : CancellationMode.DiscardPending); break;
            case "retry-generation": runtime.RetryGeneration(id); break;
            case "release":
                if (runtime.GetSession(id).Status == SessionStatus.Cancelled) runtime.ReleaseCancelled(id);
                else runtime.ReleaseCompleted(id);
                break;
        }
        return new
        {
            session = SessionView(runtime.GetSession(id)),
            progress = action == "status" ? runtime.Progress(id).Select(p => new
            {
                p.Tag, bufferedUtc = ProgressTimestamp(p.BufferedTicks),
                submittedUtc = ProgressTimestamp(p.SubmittedTicks), acknowledgedUtc = ProgressTimestamp(p.AcknowledgedTicks)
            }).ToArray() : null,
            message = action == "start"
                ? "Session admitted to local simulation state. No generation worker or Historian write was started."
                : action == "retry-generation"
                    ? "Generation re-enabled under the requested batch-byte limit. Use matching or larger compatible limits when opening the next runtime; runtime limits are not persisted."
                : "Inspect status and error fields for current state; a successful command does not imply that the session is complete."
        };
    }

    private static DateTime? ProgressTimestamp(long? ticks)
    {
        if (ticks is null) return null;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new RuntimeFailure("runtime.progress_integrity",
                "A saved progress timestamp is outside the supported UTC range. Restore verified progress metadata; do not reset the checkpoint or replay data.");
        return new DateTime(ticks.Value, DateTimeKind.Utc);
    }

    private static object SessionView(SessionSnapshot session) => new
    {
        session.SessionId, status = session.Status.ToString(), session.NextSlot, session.TotalSlots,
        session.QueuedPoints, session.QueuedBytes, session.ErrorCode, session.ErrorMessage,
        cancellation = session.Cancellation?.ToString()
    };

    private static int WriteSessionResponse(TextWriter output, string action, object result) =>
        WriteResponse(output, new { schemaVersion = 1, valid = true, mode = "simulation-only", command = "session." + action, errors = Array.Empty<ValidationError>(), result },
            new("cli.output_limit", "$", "Session response exceeds 4 MiB. Use paginated list/batches or inspect a smaller session. A mutation may already have committed; inspect status before repeating it.")) ? 0 : 1;
}
