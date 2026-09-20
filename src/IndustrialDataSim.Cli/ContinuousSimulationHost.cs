using System.IO.Pipes;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Channels;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

/// <summary>
/// The foreground host owns one runtime. Control mutations are serviced between
/// worker rounds, so a pause reply means that no earlier round remains in flight.
/// One connected client and one queued command bound control memory and work.
/// </summary>
internal sealed class ContinuousSimulationHost(DurableRuntime runtime, string pipeName,
    Func<ControlRequest, ControlResponse> execute)
{
    private sealed record PreparedReply(ControlResponse Response, long ReadyAt);
    private sealed record Pending(ControlRequest Request, long ReceivedAt, TaskCompletionSource<PreparedReply> Completion);
    private readonly Channel<Pending> commands = Channel.CreateBounded<Pending>(1);

    internal async Task RunAsync(CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // Bind before reporting started. SQLite ownership has already been acquired.
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var server = ServeAsync(pipe, lifetime.Token);
        var worker = new SimulationWorker(runtime);
        WorkerStopReason? previous = null;
        runtime.WorkerEvent("host.started", "Continuous simulation host started.",
            "Use live commands for control, or host stop for graceful shutdown. All delivery acknowledgements are synthetic.");
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (server.IsCompleted) await server; // Do not silently run without controls.
                if (commands.Reader.TryRead(out var command))
                {
                    // Once queued, a command may commit even if its client has left.
                    // Completing the reply never retries or rolls back a mutation.
                    ReportDelay("control queue wait", command.ReceivedAt);
                    long executionStarted = Stopwatch.GetTimestamp();
                    var response = execute(command.Request);
                    ReportDelay("control execution", executionStarted);
                    command.Completion.TrySetResult(new(response, Stopwatch.GetTimestamp()));
                    if (command.Request.Action == "stop" && response.ExitCode == 0)
                    {
                        // Let the stop acknowledgement leave before closing the pipe.
                        await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(1)));
                        break;
                    }
                }
                long roundStarted = Stopwatch.GetTimestamp();
                var result = await worker.RunRoundAsync(lifetime.Token);
                ReportDelay("worker round", roundStarted);
                if (result.StopReason != previous)
                {
                    runtime.WorkerEvent("host.progress_state", $"Host worker state: {result.StopReason}.",
                        result.StopReason is WorkerStopReason.Completed or WorkerStopReason.Blocked
                            ? "The host remains available for live controls. Inspect session status for completion, pauses, failures, or resource pressure. Uncertain work is never replayed."
                            : "Simulation is progressing. Use live status to inspect a session or host stop to preserve checkpoints and exit.");
                    previous = result.StopReason;
                }
                if (result.StopReason is WorkerStopReason.Completed or WorkerStopReason.Blocked)
                    await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            commands.Writer.TryComplete();
            try { await server; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            runtime.WorkerEvent("host.stopped", "Continuous simulation host stopped; database ownership will now close.",
                "Restart with the same database and compatible limits. Do not re-admit existing sessions or replay uncertain work.");
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(stop);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            bool shutdown = false;
            try
            {
                var frame = await LocalControlProtocol.ReadAsync(pipe, LocalControlProtocol.MaximumRequestBytes, deadline.Token);
                if (frame.ExitCode != 0) throw LocalControlProtocol.InvalidFrame();
                var request = LocalControlProtocol.ParseRequest(frame.Json);
                var completion = new TaskCompletionSource<PreparedReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                await commands.Writer.WriteAsync(new(request, Stopwatch.GetTimestamp(), completion), deadline.Token);
                var prepared = await completion.Task.WaitAsync(deadline.Token);
                ReportDelay("reply handoff", prepared.ReadyAt);
                var response = prepared.Response;
                shutdown = request.Action == "stop" && response.ExitCode == 0;
                long writeStarted = Stopwatch.GetTimestamp();
                await LocalControlProtocol.WriteAsync(pipe, response.Json, response.ExitCode,
                    LocalControlProtocol.MaximumResponseBytes, deadline.Token);
                ReportDelay("reply write", writeStarted);
            }
            catch (Exception error) when (error is IOException or JsonException or RuntimeFailure ||
                error is OperationCanceledException && !stop.IsCancellationRequested)
            {
                // Malformed, incomplete, or lost client exchanges affect that
                // connection only. A queued mutation may already have committed.
                // No raw input or exception text belongs in operational logs.
            }
            finally
            {
                // IsConnected becomes false for a broken peer, but the server
                // must still disconnect/reset that state before accepting again.
                pipe.Disconnect();
            }
            if (shutdown) return;
        }
    }

    private void ReportDelay(string phase, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        // Only unusually slow operations produce a record, bounded by the
        // existing rotating logger. Timings are observations, never receipts.
        if (elapsed < TimeSpan.FromSeconds(1)) return;
        runtime.WorkerEvent("host.slow_operation",
            FormattableString.Invariant($"Host {phase} took {elapsed.TotalMilliseconds:F0} ms."),
            "Compare control-client timings with host workload, disk activity and process scheduling. A slow reply does not authorize repeating a mutation.",
            LogLevel.Warning);
    }
}
