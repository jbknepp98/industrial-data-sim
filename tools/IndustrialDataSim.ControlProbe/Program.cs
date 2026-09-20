using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

// This diagnostic client only lists inventory. It never sends mutations or
// retries a request. Production wire messages and deadlines remain unchanged.
var total = Stopwatch.StartNew();
if (args.Length != 1)
{
    Console.Error.WriteLine("control_probe.usage: Supply the existing synthetic host database path. Start the host before measuring control latency.");
    return 2;
}
string phase = "setup";
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    using var pipe = new NamedPipeClientStream(".", LocalControlProtocol.PipeName(args[0]), PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    byte[] request = JsonSerializer.SerializeToUtf8Bytes(new ControlRequest(1, "list"), LocalControlProtocol.Options);
    double setupMs = total.Elapsed.TotalMilliseconds;
    phase = "connect";
    var clock = Stopwatch.StartNew();
    await pipe.ConnectAsync(3000, deadline.Token);
    double connectMs = clock.Elapsed.TotalMilliseconds;
    phase = "write";
    clock.Restart();
    await LocalControlProtocol.WriteAsync(pipe, request, 0, LocalControlProtocol.MaximumRequestBytes, deadline.Token);
    double writeMs = clock.Elapsed.TotalMilliseconds;
    phase = "reply";
    clock.Restart();
    var response = await LocalControlProtocol.ReadAsync(pipe, LocalControlProtocol.MaximumResponseBytes, deadline.Token);
    double replyMs = clock.Elapsed.TotalMilliseconds;
    using var document = JsonDocument.Parse(response.Json);
    if (response.ExitCode != 0 || !document.RootElement.GetProperty("valid").GetBoolean())
        throw new RuntimeFailure("control_probe.refused", "Host refused the inventory probe. Run verify_host.py and inspect host status before measuring again.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid = true, result = document.RootElement.GetProperty("result"),
        timings = new { setupMs, connectMs, writeMs, replyMs, clientMs = total.Elapsed.TotalMilliseconds }
    }));
    return 0;
}
catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or
    UnauthorizedAccessException or ArgumentException or JsonException or RuntimeFailure or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine($"control_probe.failed: Inventory measurement failed during {phase}. Check host availability, matching builds, pipe permissions and machine load. No request was retried.");
    return 1;
}
