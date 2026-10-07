using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

[Collection("Host timing")]
public class ProductionHostTests
{
    [Fact]
    public async Task SameUserControlsAdmitPauseExtendResumeAndStopWithoutFutureWrites()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        var worker = new ProductionWorker(runtime, delivery);
        string pipe = LocalControlProtocol.PipeName(files.Database) + "p";
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = new ContinuousSimulationHost(runtime, pipe, request => CliApplication.ExecuteProductionLive(runtime, delivery, request),
            async token => (await worker.RunAsync(1, token, DateTimeOffset.UtcNow)).StopReason == "Completed" ? WorkerStopReason.Completed : WorkerStopReason.Blocked);
        var running = host.RunAsync(stop.Token);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var model = RuntimeFixture.Model("session-" + i, "P" + i);
                model["session"]!["connectionProfile"] = "local-profile";
                model["session"]!["startUtc"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O");
                model["session"]!["endUtc"] = DateTimeOffset.UtcNow.AddHours(2).UtcDateTime.ToString("O");
                Assert.Equal(0, (await CliApplication.SendControlAsync(pipe, new(1, "start", Configuration: model.ToJsonString()), stop.Token)).ExitCode);
            }
            Assert.Equal(0, (await CliApplication.SendControlAsync(pipe, new(1, "pause", "session-0"), stop.Token)).ExitCode);
            Assert.Equal(SessionStatus.Paused, runtime.GetSession("session-0").Status);
            Assert.Equal(0, (await CliApplication.SendControlAsync(pipe, new(1, "extend", "session-0", EndUtc: DateTimeOffset.UtcNow.AddHours(3).UtcDateTime.ToString("O"), ExpectedRevision: 0, RequestId: Guid.NewGuid().ToString()), stop.Token)).ExitCode);
            Assert.Equal(SessionStatus.Paused, runtime.GetSession("session-0").Status);
            Assert.Equal(0, (await CliApplication.SendControlAsync(pipe, new(1, "resume", "session-0"), stop.Token)).ExitCode);
            var health = await CliApplication.SendControlAsync(pipe, new(1, "host-status"), stop.Token);
            Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(health.Json).GetProperty("result").GetProperty("sessions").GetInt32());
            Assert.Equal(0, server.Writes);
            Assert.Equal(0, (await CliApplication.SendControlAsync(pipe, new(1, "stop"), stop.Token)).ExitCode);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(runtime.Sessions(), s => Assert.Equal(0, s.NextSlot));
        }
        finally { stop.Cancel(); await running; }
    }

    [Fact]
    public void InvalidProductionControlCannotMutateAndSimulationRejectsExtendedFrame()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(new ProductionDeliveryTests.Server()));
        var delivery = new ProductionDelivery(runtime, client);
        var result = CliApplication.ExecuteProductionLive(runtime, delivery, new(1, "pause", "missing", EndUtc: "PRIVATE"));
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("PRIVATE", System.Text.Encoding.UTF8.GetString(result.Json));
        using var simulation = new DurableRuntime(Path.Combine(files.Folder, "sim.db"));
        Assert.Equal(1, CliApplication.ExecuteLiveRequest(simulation, new(1, "host-status", RequestId: Guid.NewGuid().ToString())).ExitCode);
    }
}
