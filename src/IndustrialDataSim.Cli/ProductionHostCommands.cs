using System.Globalization;
using System.Text;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

public static partial class CliApplication
{
    private const string ProductionHostUsage = "Usage: production-host run|status|stop <database>; production-live start <database> <model>; " +
        "production-live list <database> [after-id]; production-live status|pause|resume|release|retry-preflight|horizon <database> <session-id>; " +
        "production-live cancel <database> <session-id> drain|discard-pending; production-live extend <database> <session-id> <endUtc> <revision> <request-uuid>. " +
        "Host run uses environment credentials and real writes. Controls require the same user and exact database path. No automatic write retries or model re-admission occur.";

    private static int RunProductionHostCommand(string[] args, TextWriter output, CancellationToken stop)
    {
        if (args.Length == 2 && args[1] == "help") return WriteProductionResponse(output, new { message = ProductionHostUsage });
        bool host = args[0] == "production-host";
        string action = args.Length > 1 ? args[1] : "";
        bool valid = host ? args.Length == 3 && action is "run" or "status" or "stop" : action switch
        {
            "start" or "status" or "pause" or "resume" or "release" or "retry-preflight" or "horizon" => args.Length == 4,
            "list" => args.Length is 3 or 4,
            "cancel" => args.Length == 5 && args[4] is "drain" or "discard-pending",
            "extend" => ValidExtensionArguments(args),
            _ => false
        };
        if (!valid) { WriteResult(output, [new("cli.usage", "$", ProductionHostUsage)]); return 2; }
        try
        {
            string database = Path.GetFullPath(args[2]);
            // Distinct from simulation control; prevents a wrong-mode client from
            // inadvertently admitting a configuration through another host.
            string pipe = LocalControlProtocol.PipeName(database) + "p";
            if (host && action == "run")
            {
                using var logs = new RuntimeFileLogger(Path.Combine(Path.GetDirectoryName(database)!, "logs", Path.GetFileName(database)));
                using var runtime = new DurableRuntime(database, logger: logs, createIfMissing: false, mode: ExecutionMode.Production);
                using var client = new HistorianClient(HistorianConnection.FromEnvironment());
                var delivery = new ProductionDelivery(runtime, client);
                var worker = new ProductionWorker(runtime, delivery);
                DateTimeOffset? lastRoundUtc = null;
                long published = 0;
                var runner = new ContinuousSimulationHost(runtime, pipe,
                    request => ExecuteProductionLive(runtime, delivery, request, lastRoundUtc, published),
                    async token =>
                    {
                        var result = await worker.RunAsync(1, token, DateTimeOffset.UtcNow);
                        lastRoundUtc = DateTimeOffset.UtcNow;
                        published += result.PublishedBatches;
                        return result.StopReason switch
                        {
                            "Completed" => WorkerStopReason.Completed,
                            "Blocked" => WorkerStopReason.Blocked,
                            "Stopped" => WorkerStopReason.Stopped,
                            _ => WorkerStopReason.RoundLimit
                        };
                    });
                runner.RunAsync(stop).GetAwaiter().GetResult();
                WriteProductionResponse(output, new { message = "Production host stopped; resume with the same database. Inspect Failed/Uncertain sessions before any retry.", publishedBatches = published });
                return stop.IsCancellationRequested ? 130 : 0;
            }
            string? configuration = null;
            if (!host && action == "start")
            {
                var input = ReadInputFile(args[3]);
                if (input.Error is not null) { WriteResult(output, [input.Error]); return input.ExitCode; }
                var loaded = SimulationDefinitionLoader.Load(input.Json!);
                if (!loaded.IsValid) { WriteResult(output, loaded.Errors); return 1; }
                configuration = input.Json;
            }
            var request = new ControlRequest(1, host && action == "status" ? "host-status" : action,
                SessionId: !host && action is not ("start" or "list") ? args[3] : null,
                Configuration: configuration, Cursor: !host && action == "list" && args.Length == 4 ? args[3] : null,
                Cancellation: action == "cancel" ? args[4] : null,
                EndUtc: action == "extend" ? args[4] : null,
                ExpectedRevision: action == "extend" ? int.Parse(args[5], CultureInfo.InvariantCulture) : null,
                RequestId: action == "extend" ? args[6] : null);
            var reply = SendControlAsync(pipe, request, stop).GetAwaiter().GetResult();
            output.Write(Encoding.UTF8.GetString(reply.Json));
            return reply.ExitCode;
        }
        catch (RuntimeFailure error) { WriteResult(output, [error.Error]); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            WriteResult(output, [new("production.host_access", "$", "Production host/control could not complete. Check process health, same-user pipe access, state path and credentials/TLS for the host. A queued command may have committed; inspect status before retrying. No command or publish is automatically replayed.")]);
            return 3;
        }
    }

    internal static ControlResponse ExecuteProductionLive(DurableRuntime runtime, ProductionDelivery delivery, ControlRequest request,
        DateTimeOffset? lastRoundUtc = null, long published = 0) => CaptureResponse(writer =>
    {
        try
        {
            runtime.RequireMode(ExecutionMode.Production);
            string action = request.Action;
            bool sessionAction = action is "status" or "pause" or "resume" or "release" or "retry-preflight" or "cancel" or "extend" or "horizon";
            if (request.Version != 1 || request.AfterBatch != 0 ||
                !(sessionAction || action is "start" or "list" or "host-status" or "stop") ||
                sessionAction != (request.SessionId is not null) || request.SessionId?.Length > 64 || request.Cursor?.Length > 64 ||
                ((action == "start") != (request.Configuration is not null)) || (action != "list" && request.Cursor is not null) ||
                (request.Configuration is not null && Encoding.UTF8.GetByteCount(request.Configuration) > 1024 * 1024) ||
                (action == "cancel" ? request.Cancellation is not ("drain" or "discard-pending") : request.Cancellation is not null) ||
                (action != "extend" && (request.EndUtc is not null || request.ExpectedRevision is not null || request.RequestId is not null)))
                throw LocalControlProtocol.InvalidFrame();
            object result;
            if (action is "host-status" or "stop")
            {
                var sessions = runtime.ListSessions();
                result = new
                {
                    message = action == "stop" ? "Graceful stop accepted. Wait for process exit before direct state access." : "Production host is responsive; review failed and uncertain counts separately from process health.",
                    lastRoundUtc,
                    publishedBatches = published,
                    sessions = sessions.Count,
                    failed = sessions.Count(s => s.Status == SessionStatus.Failed),
                    uncertain = sessions.Count(s => s.Status == SessionStatus.Uncertain),
                    paused = sessions.Count(s => s.Status == SessionStatus.Paused),
                    queuedPoints = sessions.Sum(s => s.QueuedPoints),
                    queuedBytes = sessions.Sum(s => s.QueuedBytes)
                };
            }
            else if (action == "list")
            {
                var sessions = runtime.ListSessions(request.Cursor);
                result = new { sessions = sessions.Select(SessionView), nextCursor = sessions.Count == 100 ? sessions[^1].SessionId : null };
            }
            else if (action == "start")
            {
                if (runtime.ListSessions().Count >= 100) throw HostSessionLimit();
                var loaded = SimulationDefinitionLoader.Load(request.Configuration!);
                if (!loaded.IsValid) { WriteResult(writer, loaded.Errors); return 1; }
                var settings = delivery.AdmitAsync(request.Configuration!, CancellationToken.None).GetAwaiter().GetResult();
                result = new { settings, session = SessionView(runtime.GetSession(loaded.Definition!.Session.SessionId)) };
            }
            else
            {
                string id = request.SessionId!;
                switch (action)
                {
                    case "pause": runtime.Pause(id); break;
                    case "resume": runtime.Resume(id); break;
                    case "retry-preflight": runtime.RetryProductionPreflight(id); break;
                    case "cancel": runtime.Cancel(id, request.Cancellation == "drain" ? CancellationMode.Drain : CancellationMode.DiscardPending); break;
                    case "release":
                        if (runtime.GetSession(id).Status == SessionStatus.Cancelled) runtime.ReleaseCancelled(id); else runtime.ReleaseCompleted(id);
                        break;
                }
                if (action == "extend")
                {
                    string[] args = ["production", "extend", "", id, request.EndUtc ?? "", request.ExpectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "", request.RequestId ?? ""];
                    if (!ValidExtensionArguments(args)) throw LocalControlProtocol.InvalidFrame();
                    result = ApplyExtension(runtime, args);
                }
                else result = new
                {
                    session = SessionView(runtime.GetSession(id)),
                    horizon = runtime.HorizonStatus(id),
                    progress = runtime.Progress(id),
                    message = "Control completed between publish rounds. Published progress is not a per-point receipt."
                };
            }
            return WriteProductionResponse(writer, result);
        }
        catch (RuntimeFailure failure) { WriteResult(writer, [failure.Error]); return 1; }
        catch (Exception error) when (ProductionDelivery.IsTransportFailure(error))
        { WriteResult(writer, [ProductionDelivery.SafeFailure(error).Error]); return 3; }
    });
}
