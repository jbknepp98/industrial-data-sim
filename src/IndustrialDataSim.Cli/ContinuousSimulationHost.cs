using System.IO.Pipes;
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
    private sealed record Pending(ControlRequest Request, TaskCompletionSource<ControlResponse> Completion);
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
                    var response = execute(command.Request);
                    command.Completion.TrySetResult(response);
                    if (command.Request.Action == "stop" && response.ExitCode == 0)
                    {
                        // Let the stop acknowledgement leave before closing the pipe.
                        await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(1)));
                        break;
                    }
                }
                var result = await worker.RunRoundAsync(lifetime.Token);
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
                var completion = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                await commands.Writer.WriteAsync(new(request, completion), deadline.Token);
                var response = await completion.Task.WaitAsync(deadline.Token);
                shutdown = request.Action == "stop" && response.ExitCode == 0;
                await LocalControlProtocol.WriteAsync(pipe, response.Json, response.ExitCode,
                    LocalControlProtocol.MaximumResponseBytes, deadline.Token);
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
}
