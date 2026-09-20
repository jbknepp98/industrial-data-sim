using System.Globalization;
using IndustrialDataSim.Runtime;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string ProductionUsage = "Usage: production start <database> <model-file>; production run <database> <rounds>; " +
        "production list <database> [after-session-id]; production status|pause|resume|release|retry-preflight|archive-info <database> <session-id>; " +
        "production observations <database> <session-id> [after-batch-id]; production review <database> <session-id> accepted|needs-attention; " +
        "production cancel <database> <session-id> drain|discard-pending; production archive|verify-archive <database> <session-id> <directory>. " +
        "Start performs authenticated read-only preflight and admission. Run publishes real data using environment credentials. Simulation databases cannot be used.";

    private static int RunProductionCommand(string[] args, TextWriter output, CancellationToken stop)
    {
        if (args.Length == 2 && args[1] == "help") return WriteProductionResponse(output, new { message = ProductionUsage });
        string action = args.Length > 1 ? args[1] : "";
        bool shape = action switch {
            "start" or "run" or "status" or "pause" or "resume" or "release" or "retry-preflight" or "archive-info" => args.Length == 4,
            "list" => args.Length is 3 or 4,
            "observations" => args.Length is 4 or 5,
            "archive" or "verify-archive" => args.Length == 5,
            "cancel" => args.Length == 5 && args[4] is "drain" or "discard-pending",
            "review" => args.Length == 5 && args[4] is "accepted" or "needs-attention",
            _ => false };
        int rounds = 0;
        long cursor = 0;
        if (!shape || action == "run" && (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out rounds) || rounds is < 1 or > 10000) ||
            action == "observations" && args.Length == 5 && (!long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out cursor) || cursor < 0))
        { WriteResult(output, [new("cli.usage", "$", ProductionUsage)]); return 2; }
        string? configuration = null;
        if (action == "start")
        {
            var input = ReadInputFile(args[3]);
            if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
            var loaded = Core.Configuration.SimulationDefinitionLoader.Load(input.Json!);
            if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
            configuration = input.Json;
        }
        object result;
        int exit = 0;
        try
        {
            string database = Path.GetFullPath(args[2]);
            using var logs = new RuntimeFileLogger(Path.Combine(Path.GetDirectoryName(database)!, "logs", Path.GetFileName(database)));
            using var runtime = new DurableRuntime(database, logger: logs, createIfMissing: action == "start", mode: ExecutionMode.Production);
            if (action is "start" or "run")
            {
                using var client = new HistorianClient(HistorianConnection.FromEnvironment());
                var delivery = new ProductionDelivery(runtime, client);
                if (action == "start") result = new { settings = delivery.AdmitAsync(configuration!, stop).GetAwaiter().GetResult(),
                    message = "Production session admitted after read-only preflight. No TVQ write has started. Inspect Dataset retention settings before production run." };
                else
                {
                    var run = new ProductionWorker(runtime, delivery).RunAsync(rounds, stop).GetAwaiter().GetResult();
                    result = new { run.StopReason, run.Rounds, run.PublishedBatches, run.Message, sessions = run.Sessions.Select(SessionView) };
                    exit = run.StopReason == "Completed" ? 0 : run.StopReason == "Stopped" ? 130 : 4;
                }
            }
            else if (action == "list")
            {
                var sessions = runtime.ListSessions(args.Length == 4 ? args[3] : null);
                result = new { sessions = sessions.Select(SessionView), nextCursor = sessions.Count == 100 ? sessions[^1].SessionId : null };
            }
            else if (action == "archive") result = runtime.Archive(args[3], args[4]);
            else if (action == "archive-info") result = runtime.GetArchive(args[3]);
            else if (action == "verify-archive") { runtime.VerifyArchive(args[3], args[4]); result = new { message = "Archive matches its durable receipt; no replay is authorized." }; }
            else if (action == "observations")
            {
                var observations = runtime.Observations(args[3], cursor);
                result = new { observations, nextCursor = observations.Count == 100 ? (long?)observations[^1].BatchId : null };
            }
            else
            {
                string id = args[3];
                switch (action)
                {
                    case "retry-preflight": runtime.RetryProductionPreflight(id); break;
                    case "pause": runtime.Pause(id); break;
                    case "resume": runtime.Resume(id); break;
                    case "cancel": runtime.Cancel(id, args[4] == "drain" ? CancellationMode.Drain : CancellationMode.DiscardPending); break;
                    case "review": runtime.RecordUserReview(id, args[4] == "accepted"); break;
                    case "release":
                        if (runtime.GetSession(id).Status == SessionStatus.Cancelled) runtime.ReleaseCancelled(id);
                        else runtime.ReleaseCompleted(id);
                        break;
                }
                result = new { session = SessionView(runtime.GetSession(id)), progress = runtime.Progress(id).Select(p => new {
                    p.Tag, bufferedUtc = ProgressTimestamp(p.BufferedTicks), submittedUtc = ProgressTimestamp(p.SubmittedTicks),
                    publishedUtc = ProgressTimestamp(p.PublishedTicks) }), message = "Published progress is not verified storage. User review and arrival observations are separate." };
            }
        }
        catch (RuntimeFailure failure) { WriteResult(output, [failure.Error]); return 1; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { WriteResult(output, [new("production.interrupted", "$", "Operation interrupted. Inspect durable status before continuing; Sending work may be uncertain and must not be replayed.")]); return 130; }
        catch (Exception error) when (ProductionDelivery.IsTransportFailure(error) || error is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { WriteResult(output, [ProductionDelivery.SafeFailure(error).Error]); return 3; }
        return WriteProductionResponse(output, result) == 0 ? exit : 1;
    }

    private static int WriteProductionResponse(TextWriter output, object result) => WriteResponse(output,
        new { schemaVersion = 1, valid = true, mode = "production", errors = Array.Empty<ValidationError>(), result },
        new("cli.output_limit", "$", "Production response exceeds 4 MiB. Use paginated observations and per-session status. A mutation may have committed; inspect durable state before repeating it.")) ? 0 : 1;
}
