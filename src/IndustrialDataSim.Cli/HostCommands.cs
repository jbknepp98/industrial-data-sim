using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string HostUsage = "Usage: host run <existing-database> [batch-bytes]; host status|stop <database>; " +
        "live start <database> <model-file>; live list <database> [after-session-id]; " +
        "live status|pause|resume|release|retry-generation <database> <session-id>; " +
        "live cancel <database> <session-id> drain|discard-pending; live batches <database> <session-id> [after-batch-id]. " +
        "The foreground host uses a fake Historian. Live commands require the same user and exact database path as the running host. " +
        "Retry-generation uses the host's current limits; stop and restart the host to change limits.";

    private static int RunHostCommand(string[] args, TextWriter output, CancellationToken stop)
    {
        if (args.Length == 2 && args[1] == "help") return WriteSessionResponse(output, "host-help", new { message = HostUsage });
        string action = args.Length > 1 ? args[1] : "";
        bool isHost = args[0] == "host";
        bool run = isHost && action == "run";
        bool shape = isHost ? action switch
        {
            "run" => args.Length is 3 or 4,
            "status" or "stop" => args.Length == 3,
            _ => false
        } : action switch
        {
            "start" or "status" or "pause" or "resume" or "release" or "retry-generation" => args.Length == 4,
            "list" => args.Length is 3 or 4,
            "cancel" => args.Length == 5 && args[4] is "drain" or "discard-pending",
            "batches" => args.Length is 4 or 5,
            _ => false
        };
        int batchBytes = 1024 * 1024;
        long afterBatch = 0;
        if (!shape || (run && args.Length == 4 && (!int.TryParse(args[3], NumberStyles.None,
                CultureInfo.InvariantCulture, out batchBytes) || batchBytes is < 128 or > 4194304)) ||
            (!isHost && action == "batches" && args.Length == 5 && (!long.TryParse(args[4], NumberStyles.None,
                CultureInfo.InvariantCulture, out afterBatch) || afterBatch < 0)))
        {
            WriteResult(output, [new("cli.usage", "$", HostUsage + " Batch bytes must be 128–4194304; batch cursors must be nonnegative integers.")]);
            return 2;
        }
        string? configuration = null;
        if (!isHost && action == "start")
        {
            var input = ReadInputFile(args[3]);
            if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
            var model = SimulationDefinitionLoader.Load(input.Json!);
            if (!model.IsValid) { WriteResult(output, model.Errors); return 1; }
            configuration = input.Json;
        }
        ControlResponse response;
        try
        {
            string database = Path.GetFullPath(args[2]);
            string pipeName = LocalControlProtocol.PipeName(database);
            if (run)
            {
                if (!File.Exists(database)) throw new RuntimeFailure("cli.state_missing",
                    "The state database was not found or is inaccessible. Check the path and permissions; admit the first model with session start before hosting.");
                using var logs = new RuntimeFileLogger(Path.Combine(Path.GetDirectoryName(database)!, "logs", Path.GetFileName(database)));
                using var runtime = new DurableRuntime(database, new() { BatchBytes = batchBytes }, logs, createIfMissing: false);
                // Refuse an oversized inventory before binding controls or generation.
                if (runtime.ListSessions().Count == 100 && runtime.ListSessions(runtime.ListSessions()[^1].SessionId, 1).Count > 0)
                    throw HostSessionLimit();
                new ContinuousSimulationHost(runtime, pipeName, request => ExecuteLiveRequest(runtime, request))
                    .RunAsync(stop).GetAwaiter().GetResult();
                response = CaptureResponse(writer => WriteSessionResponse(writer, "host-run", new
                {
                    message = "Host stopped. Checkpoints and pending work remain durable; restart with the same database and compatible limits."
                }));
            }
            else
            {
                var request = new ControlRequest(1, isHost && action == "status" ? "host-status" : action,
                    SessionId: !isHost && action is not ("start" or "list") ? args[3] : null,
                    Configuration: configuration,
                    Cursor: !isHost && action == "list" && args.Length == 4 ? args[3] : null,
                    AfterBatch: afterBatch,
                    Cancellation: !isHost && action == "cancel" ? args[4] : null);
                response = SendControlAsync(pipeName, request, stop).GetAwaiter().GetResult();
            }
        }
        catch (RuntimeFailure failure) { WriteResult(output, [failure.Error]); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            WriteResult(output, [new(run ? "host.access_failure" : "host.control_unavailable", "$",
                run ? "The continuous host could not complete a local state or pipe operation. Check the database path, permissions, free disk space, and whether another host owns the endpoint. Preserve state and inspect it after the host exits before restarting. No command or delivery is automatically retried." :
                "The host or local control connection could not be used. Check that the host is running under the same user with the same database path, and check local pipe permissions. A submitted command may have committed even without a reply. Inspect live status (or stop the host and inspect session status) before retrying; commands are never automatically retried.")]);
            return 3;
        }
        output.Write(Encoding.UTF8.GetString(response.Json));
        return run && stop.IsCancellationRequested ? 130 : response.ExitCode;
    }

    internal static async Task<ControlResponse> SendControlAsync(string pipeName, ControlRequest request, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, deadline.Token);
        await LocalControlProtocol.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, LocalControlProtocol.Options),
            0, LocalControlProtocol.MaximumRequestBytes, deadline.Token);
        var reply = await LocalControlProtocol.ReadAsync(pipe, LocalControlProtocol.MaximumResponseBytes, deadline.Token);
        // Never relay malformed JSON or arbitrary non-JSON output from an endpoint.
        try { using var document = JsonDocument.Parse(reply.Json); }
        catch (JsonException) { throw LocalControlProtocol.InvalidFrame(); }
        return reply;
    }

    internal static ControlResponse ExecuteLiveRequest(DurableRuntime runtime, ControlRequest request) => CaptureResponse(writer =>
    {
        try
        {
            if (request.Version != 1 || request.Action is null || request.AfterBatch < 0 ||
                request.SessionId?.Length > 64 || request.Cursor?.Length > 64 ||
                request.Configuration is not null && Encoding.UTF8.GetByteCount(request.Configuration) > 1024 * 1024)
                throw LocalControlProtocol.InvalidFrame();
            string action = request.Action;
            bool sessionAction = action is "status" or "pause" or "resume" or "release" or "retry-generation" or "cancel" or "batches";
            if (!(sessionAction || action is "start" or "list" or "host-status" or "stop") ||
                (sessionAction != (request.SessionId is not null)) ||
                ((action == "start") != (request.Configuration is not null)) ||
                (action != "list" && request.Cursor is not null) || (action != "batches" && request.AfterBatch != 0) ||
                (action == "cancel" ? request.Cancellation is not ("drain" or "discard-pending") : request.Cancellation is not null))
                throw LocalControlProtocol.InvalidFrame();
            if (action is "host-status" or "stop")
                return WriteSessionResponse(writer, action, new { message = action == "stop"
                    ? "Graceful host stop accepted. Wait for the host process to exit before opening SQLite directly."
                    : "Continuous simulation host is available. Use live list/status for session progress. All delivery is synthetic." });
            string? id = request.SessionId;
            if (action == "start")
            {
                var loaded = SimulationDefinitionLoader.Load(request.Configuration!);
                if (!loaded.IsValid) { WriteResult(writer, loaded.Errors); return 1; }
                if (runtime.ListSessions().Count >= 100) throw HostSessionLimit();
                id = loaded.Definition!.Session.SessionId;
            }
            string[] arguments = action == "list"
                ? request.Cursor is null ? ["session", action, ""] : ["session", action, "", request.Cursor]
                : ["session", action, "", id!, request.Cancellation ?? ""];
            object result = ExecuteSessionCommand(runtime, action, arguments, request.Configuration, id, request.AfterBatch);
            // Admission is now followed automatically by host rounds. Override
            // the offline CLI's admission text, while retaining its state view.
            if (action is "start" or "retry-generation") result = new
            {
                session = SessionView(runtime.GetSession(id!)),
                message = "Session is eligible for the host's next round under its current runtime limits. Inspect live status for progress and errors."
            };
            return WriteSessionResponse(writer, "live-" + action, result);
        }
        catch (RuntimeFailure failure) { WriteResult(writer, [failure.Error]); return 1; }
    });

    private static ControlResponse CaptureResponse(Func<TextWriter, int> write)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int code = write(output);
        return new(code, Encoding.UTF8.GetBytes(output.ToString()));
    }

    private static RuntimeFailure HostSessionLimit() => new("host.session_limit",
        "The continuous host supports at most 100 unarchived sessions per database, including finished sessions. Archive eligible finished sessions or use separate databases only for disjoint tags; preserve unresolved recovery state.");
}
