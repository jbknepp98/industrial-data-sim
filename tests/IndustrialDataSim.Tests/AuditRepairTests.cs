using System.Net;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace IndustrialDataSim.Tests;

public class AuditRepairTests(ITestOutputHelper output)
{
    private static void Tamper(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("Ready", false)]
    [InlineData("Draining", false)]
    [InlineData("Cancelling", false)]
    [InlineData("Ready", true)]
    [InlineData("Draining", true)]
    [InlineData("Cancelling", true)]
    public async Task ProductionIntegrityFailureShouldNotAbortHealthySession(string state, bool horizon)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model("session-a", "A").ToJsonString());
        await delivery.AdmitAsync(RuntimeFixture.Model("session-b", "B").ToJsonString());
        if (state != "Ready") runtime.Generate("session-a");
        if (state == "Cancelling") runtime.Cancel("session-a", CancellationMode.Drain);
        long queued = runtime.GetSession("session-a").QueuedPoints;
        Tamper(files.Database, horizon
            ? "UPDATE sessions SET horizon_revision=1 WHERE id='session-a'"
            : "UPDATE sessions SET config_hash='audit-invalid' WHERE id='session-a'");
        var error = await Record.ExceptionAsync(() => new ProductionWorker(runtime, delivery).RunAsync(1));
        output.WriteLine($"Escaped error: {(error as RuntimeFailure)?.Error.Code}; bad={runtime.GetSession("session-a").Status}; healthy cursor={runtime.GetSession("session-b").NextSlot}; writes={server.Writes}");
        Assert.Null(error);
        Assert.Equal(SessionStatus.Failed, runtime.GetSession("session-a").Status);
        Assert.Equal(queued, runtime.GetSession("session-a").QueuedPoints);
        Assert.True(runtime.GetSession("session-b").NextSlot > 0);
    }

    [Theory]
    [InlineData("Ready", false)]
    [InlineData("Draining", false)]
    [InlineData("Cancelling", false)]
    [InlineData("Ready", true)]
    [InlineData("Draining", true)]
    [InlineData("Cancelling", true)]
    public async Task SimulationIntegrityFailureShouldRemainSessionLocal(string state, bool horizon)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model("session-a", "A").ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        if (state != "Ready") runtime.Generate("session-a");
        if (state == "Cancelling") runtime.Cancel("session-a", CancellationMode.Drain);
        Tamper(files.Database, horizon
            ? "UPDATE sessions SET horizon_revision=1 WHERE id='session-a'"
            : "UPDATE sessions SET config_hash='audit-invalid' WHERE id='session-a'");
        var error = await Record.ExceptionAsync(() => new SimulationWorker(runtime).RunAsync(1));
        output.WriteLine($"Escaped error: {(error as RuntimeFailure)?.Error.Code}; bad={runtime.GetSession("session-a").Status}; healthy cursor={runtime.GetSession("session-b").NextSlot}");
        Assert.Null(error);
        Assert.True(runtime.GetSession("session-b").NextSlot > 0);
    }

    [Fact]
    public async Task LiveStatusShouldStillExplainAQuarantinedSession()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(new ProductionDeliveryTests.Server()));
        var delivery = new ProductionDelivery(runtime, client);
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        Tamper(files.Database, "UPDATE sessions SET config_hash='audit-invalid' WHERE id='session-a'");
        Assert.Throws<RuntimeFailure>(() => runtime.Generate("session-a"));
        var response = CliApplication.ExecuteProductionLive(runtime, delivery, new(1, "status", "session-a"));
        output.WriteLine(Encoding.UTF8.GetString(response.Json));
        Assert.Equal(0, response.ExitCode);
        Assert.Contains("Failed", Encoding.UTF8.GetString(response.Json));
        using var json = JsonDocument.Parse(response.Json);
        Assert.Equal("runtime.configuration_integrity", json.RootElement.GetProperty("result").GetProperty("horizonError").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").GetProperty("horizon").ValueKind);
        Assert.DoesNotContain("bufferedTicks", Encoding.UTF8.GetString(response.Json));
    }

    private sealed class RetentionServer(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal int PurgeDays = 30;
        internal int SettingsReads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/datasets/") &&
                !path.EndsWith("/exists") && !path.EndsWith("/tags") && !path.EndsWith("/data"))
            {
                SettingsReads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"pa\":{PurgeDays},\"ps\":0,\"ldt\":100,\"lda\":30}}") });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task ExtendedSessionShouldRecheckRetentionBeforeBlindPublish()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        var retention = new RetentionServer(server);
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(retention));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        var model = RuntimeFixture.Model();
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        model["session"]!["startUtc"] = start.UtcDateTime.ToString("O");
        model["session"]!["endUtc"] = start.AddSeconds(3).UtcDateTime.ToString("O");
        await delivery.AdmitAsync(model.ToJsonString());
        retention.PurgeDays = 1;
        runtime.ExtendHorizon("session-a", start.AddSeconds(6), 0, Guid.NewGuid().ToString());
        runtime.Generate("session-a");
        bool published = await delivery.DeliverOneAsync("session-a");
        output.WriteLine($"Published={published}; dataset settings reads={retention.SettingsReads}; POST calls={server.Writes}; state={runtime.GetSession("session-a").Status}");
        Assert.False(published);
        Assert.Equal(0, server.Writes);
        Assert.Equal(2, retention.SettingsReads);
        Assert.Equal(SessionStatus.Failed, runtime.GetSession("session-a").Status);
        Assert.True(runtime.GetSession("session-a").QueuedPoints > 0);
    }

    [Fact]
    public void DivideByZeroShouldGiveACorrectiveArithmeticDiagnostic()
    {
        var model = ManufacturingTests.Model("""[{"tag":"A","expression":{"op":"divide","left":1,"right":0}}]""");
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        var error = Assert.Single(result.Errors);
        output.WriteLine($"{error.Code} {error.Path}: {error.Message}");
        Assert.Equal("manufacturing.division_by_zero", error.Code);
        Assert.EndsWith(".right", error.Path);
        Assert.Contains("nonzero", error.Message);
        Assert.Contains("eagerly", error.Message);
    }
    [Fact]
    public async Task ElapsedRetentionAfterRestartPreservesPendingBatch()
    {
        using var files = new RuntimeFixture();
        var model = RuntimeFixture.Model();
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var now = start;
        var server = new ProductionDeliveryTests.Server();
        var retention = new RetentionServer(server) { PurgeDays = 1 };
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(retention)) { UtcNow = () => now };
        long queued;
        using (var initial = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            await new ProductionDelivery(initial, client).AdmitAsync(model.ToJsonString());
            initial.Generate("session-a");
            queued = initial.GetSession("session-a").QueuedPoints;
        }
        now = start.AddDays(2);
        using var resumed = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var delivery = new ProductionDelivery(resumed, client) { ObservationDelay = TimeSpan.Zero };
        Assert.False(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal("historian.retention_conflict", resumed.GetSession("session-a").ErrorCode);
        Assert.Equal(queued, resumed.GetSession("session-a").QueuedPoints);
        Assert.Equal(0, server.Writes);
        Assert.All(resumed.Progress("session-a"), p => Assert.Null(p.SubmittedTicks));
        // An explicit preflight retry can send known-unsent work after an
        // operator expands retention. No attempted payload is replayed.
        retention.PurgeDays = 3;
        resumed.RetryProductionPreflight("session-a");
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal(1, server.Writes);
    }

    [Fact]
    public async Task CurrentPendingBatchIsNotRejectedBecauseSessionOriginExpired()
    {
        using var files = new RuntimeFixture();
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var now = start;
        var server = new ProductionDeliveryTests.Server();
        var retention = new RetentionServer(server) { PurgeDays = 1 };
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(retention)) { UtcNow = () => now };
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        runtime.ExtendHorizon("session-a", start.AddSeconds(6), 0, Guid.NewGuid().ToString());
        runtime.Generate("session-a");
        now = start.AddDays(1).AddSeconds(2);
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
    }

    [Theory]
    [InlineData("add", "1e308", "1e308")]
    [InlineData("subtract", "-1e308", "1e308")]
    [InlineData("multiply", "1e308", "2")]
    [InlineData("divide", "1e308", "0.0001")]
    public void ArithmeticOverflowExplainsOperationWithoutEchoingOperands(string operation, string left, string right)
    {
        var nodes = "[{\"tag\":\"A\",\"expression\":{\"op\":\"" + operation + "\",\"left\":" + left + ",\"right\":" + right + "}}]";
        var loaded = SimulationDefinitionLoader.Load(ManufacturingTests.Model(nodes).ToJsonString());
        var error = Assert.Single(loaded.Errors);
        Assert.Equal("manufacturing.arithmetic_overflow", error.Code);
        Assert.EndsWith(".expression", error.Path);
        Assert.Contains(operation, error.Message);
        Assert.Contains("Reduce", error.Message);
        Assert.DoesNotContain(left, error.Message);
    }

    [Fact]
    public async Task PauseResponseRetainsCommittedOutcomeWhenHorizonIsUnavailable()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(new ProductionDeliveryTests.Server()));
        var delivery = new ProductionDelivery(runtime, client);
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Tamper(files.Database, "UPDATE sessions SET horizon_revision=1 WHERE id='session-a'");
        var response = CliApplication.ExecuteProductionLive(runtime, delivery, new(1, "pause", "session-a"));
        Assert.Equal(0, response.ExitCode);
        Assert.Equal(SessionStatus.Paused, runtime.GetSession("session-a").Status);
        using var json = JsonDocument.Parse(response.Json);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("extension.integrity", result.GetProperty("horizonError").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.String, result.GetProperty("progress")[0].GetProperty("bufferedUtc").ValueKind);
    }

}
