using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

// These tests assert short real pipe deadlines. Run apart from parallel bulk
// SQLite/crash tests so CI storage/thread contention is not mistaken for a host
// protocol failure. Separate-process capacity tests still exercise a busy host.
[CollectionDefinition("Host timing", DisableParallelization = true)]
public sealed class HostTimingCollection;

[Collection("Host timing")]
public class ContinuousHostTests
{
    [Fact]
    public async Task DisconnectedMutationIsNotRetriedAndIdleLogsStayBounded()
    {
        using var files = new RuntimeFixture();
        string logFolder = Path.Combine(files.Folder, "logs");
        using var logger = new RuntimeFileLogger(logFolder);
        using var runtime = new DurableRuntime(files.Database, logger: logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Pause("session-a");
        string name = LocalControlProtocol.PipeName(files.Database);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new ContinuousSimulationHost(runtime, name, r => CliApplication.ExecuteLiveRequest(runtime, r)).RunAsync(stop.Token);
        try
        {
            using (var abandoned = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await abandoned.ConnectAsync(stop.Token);
                await LocalControlProtocol.WriteAsync(abandoned, JsonSerializer.SerializeToUtf8Bytes(
                    new ControlRequest(1, "cancel", "session-a", Cancellation: "discard-pending"), LocalControlProtocol.Options),
                    0, LocalControlProtocol.MaximumRequestBytes, stop.Token);
                // Leave without reading the mutation's reply.
            }
            for (int i = 0; i < 4; i++)
                Result(await CliApplication.SendControlAsync(name, new(1, "host-status"), stop.Token));
            Assert.Equal(SessionStatus.Cancelled, runtime.GetSession("session-a").Status);
            Result(await CliApplication.SendControlAsync(name, new(1, "stop"), stop.Token));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            var codes = File.ReadLines(Path.Combine(logFolder, "runtime.jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("eventCode").GetString()).ToArray();
            Assert.Single(codes, code => code == "session.cancellation_requested");
            Assert.Single(codes, code => code == "host.started");
            Assert.Single(codes, code => code == "host.stopped");
            Assert.InRange(codes.Count(code => code == "host.progress_state"), 1, 2);
            Assert.DoesNotContain("worker.started", codes);
        }
        finally { stop.Cancel(); await running; }
    }

    [Fact]
    public async Task GracefulStopInterruptsAStalledControlRead()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        string name = LocalControlProtocol.PipeName(files.Database);
        using var stop = new CancellationTokenSource();
        var running = new ContinuousSimulationHost(runtime, name, r => CliApplication.ExecuteLiveRequest(runtime, r)).RunAsync(stop.Token);
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(3000);
            await client.WriteAsync(new byte[] { 1 }); // Incomplete frame header.
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Cancel(); await running; }
    }

    [Theory]
    [InlineData("host", "run", "0")]
    [InlineData("live", "cancel", "invalid")]
    public void InvalidHostUsageDoesNotCreateState(string group, string action, string option)
    {
        using var files = new RuntimeFixture();
        using var output = new StringWriter();
        string[] args = group == "host" ? [group, action, files.Database, option]
            : [group, action, files.Database, "session-a", option];
        Assert.Equal(2, CliApplication.Run(args, output));
        Assert.Contains("cli.usage", output.ToString());
        Assert.False(Directory.Exists(files.Folder));
    }

    private static JsonElement Result(ControlResponse response)
    {
        Assert.Equal(0, response.ExitCode);
        return JsonSerializer.Deserialize<JsonElement>(response.Json).GetProperty("result");
    }

    [Fact]
    public async Task HostControlsPausedSessionAdmitsPeerAndStopsWithDurableState()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 1 }))
        {
            var model = RuntimeFixture.Model();
            model["session"]!["endUtc"] = "2026-09-02T00:00:00Z";
            runtime.AddSession(model.ToJsonString());
            runtime.Pause("session-a");
            string pipe = LocalControlProtocol.PipeName(files.Database);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var host = new ContinuousSimulationHost(runtime, pipe, r => CliApplication.ExecuteLiveRequest(runtime, r));
            var running = host.RunAsync(stop.Token);
            try
            {
                async Task<ControlResponse> Send(ControlRequest request) => await CliApplication.SendControlAsync(pipe, request, stop.Token);
                Assert.Contains("available", Result(await Send(new(1, "host-status"))).GetProperty("message").GetString());
                Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
                Result(await Send(new(1, "resume", "session-a")));
                Result(await Send(new(1, "pause", "session-a")));
                long paused = runtime.GetSession("session-a").NextSlot;
                Assert.True(paused > 0);
                Result(await Send(new(1, "start", Configuration: RuntimeFixture.Model("session-b", "B").ToJsonString())));
                Assert.Equal(paused, runtime.GetSession("session-a").NextSlot);
                Result(await Send(new(1, "cancel", "session-a", Cancellation: "discard-pending")));
                Result(await Send(new(1, "release", "session-a")));
                Result(await Send(new(1, "stop")));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { stop.Cancel(); await running; }
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(SessionStatus.Cancelled, reopened.GetSession("session-a").Status);
        Assert.DoesNotContain(reopened.Sessions(), s => s.Status == SessionStatus.Uncertain);
    }

    [Fact]
    public async Task MalformedClientAndInvalidStopDoNotKillHost()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        string name = LocalControlProtocol.PipeName(files.Database);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new ContinuousSimulationHost(runtime, name, r => CliApplication.ExecuteLiveRequest(runtime, r)).RunAsync(stop.Token);
        try
        {
            using (var bad = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await bad.ConnectAsync(stop.Token);
                byte[] header = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
                await bad.WriteAsync(header, stop.Token);
            }
            Assert.Equal(1, (await CliApplication.SendControlAsync(name, new(99, "stop"), stop.Token)).ExitCode);
            Result(await CliApplication.SendControlAsync(name, new(1, "host-status"), stop.Token));
            Result(await CliApplication.SendControlAsync(name, new(1, "stop"), stop.Token));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Cancel(); await running; }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(8388609)]
    public async Task FrameBoundsAreCheckedBeforeReadingBody(int length)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => LocalControlProtocol.ReadAsync(stream,
            LocalControlProtocol.MaximumRequestBytes, default));
        Assert.Equal("host.invalid_request", failure.Error.Code);
        Assert.Contains("Inspect", failure.Error.Message);
    }

    [Fact]
    public void InvalidCommandsCannotMutateStateOrExposeInput()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        foreach (var request in new ControlRequest[] {
            new(1, "pause", "session-a", Configuration: "private-input"),
            new(1, "cancel", "session-a", Cancellation: "private-input"),
            new(1, "batches", "session-a", AfterBatch: -1),
            new(1, "start", Configuration: "private-input"),
            new(0, "stop"), new(1, "status"), new(1, "private-input") })
        {
            var response = CliApplication.ExecuteLiveRequest(runtime, request);
            Assert.Equal(1, response.ExitCode);
            Assert.DoesNotContain("private-input", Encoding.UTF8.GetString(response.Json));
        }
        Assert.Equal(SessionStatus.Ready, runtime.GetSession("session-a").Status);
        Assert.Empty(runtime.Batches("session-a"));
    }

    [Fact]
    public void HostAdmissionCapAndOwnershipRefuseBeforeCommit()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var conflict = CliApplication.ExecuteLiveRequest(runtime, new(1, "start", Configuration: RuntimeFixture.Model("other").ToJsonString()));
        Assert.Contains("runtime.tag_owned", Encoding.UTF8.GetString(conflict.Json));
        for (int i = 1; i < 100; i++) runtime.AddSession(RuntimeFixture.Model($"session-{i}", $"tag-{i}").ToJsonString());
        var limit = CliApplication.ExecuteLiveRequest(runtime, new(1, "start", Configuration: RuntimeFixture.Model("overflow", "extra").ToJsonString()));
        Assert.Contains("host.session_limit", Encoding.UTF8.GetString(limit.Json));
        Assert.Equal(100, runtime.Sessions().Count);
    }

    [Fact]
    public void MissingHostProducesActionableErrorWithoutCreatingState()
    {
        using var files = new RuntimeFixture();
        using var output = new StringWriter();
        Assert.Equal(3, CliApplication.Run(["host", "status", files.Database], output));
        Assert.Contains("host.control_unavailable", output.ToString());
        Assert.Contains("may have committed", output.ToString());
        Assert.False(Directory.Exists(files.Folder));
        Assert.DoesNotContain(files.Database, output.ToString());
    }

    [Fact]
    public void InvalidHostPathHasSafeStartupGuidance()
    {
        using var output = new StringWriter();
        Assert.Equal(3, CliApplication.Run(["host", "run", "private-path\0"], output));
        Assert.Contains("host.access_failure", output.ToString());
        Assert.Contains("permissions", output.ToString());
        Assert.Contains("Preserve state", output.ToString());
        Assert.DoesNotContain("private-path", output.ToString());
    }
}
