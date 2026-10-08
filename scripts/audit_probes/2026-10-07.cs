// Audit reproductions, intentionally outside the normal test compile glob.
// These assert desired behavior and fail on c285ed7. All HTTP is scripted;
// only temporary SQLite state is changed. See the dated audit for execution.
using System.Net;
using System.Text;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace IndustrialDataSim.Tests;

public class Audit20261007Probes(ITestOutputHelper output)
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

    [Fact]
    public async Task ProductionDrainingIntegrityFailureShouldNotAbortHealthySession()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new ProductionDeliveryTests.Server();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model("session-a", "A").ToJsonString());
        await delivery.AdmitAsync(RuntimeFixture.Model("session-b", "B").ToJsonString());
        runtime.Generate("session-a");
        Assert.Equal(SessionStatus.Draining, runtime.GetSession("session-a").Status);
        Tamper(files.Database, "UPDATE sessions SET config_hash='audit-invalid' WHERE id='session-a'");
        var error = await Record.ExceptionAsync(() => new ProductionWorker(runtime, delivery).RunAsync(1));
        output.WriteLine($"Escaped error: {(error as RuntimeFailure)?.Error.Code}; bad={runtime.GetSession("session-a").Status}; healthy cursor={runtime.GetSession("session-b").NextSlot}; writes={server.Writes}");
        Assert.Null(error);
        Assert.True(runtime.GetSession("session-b").NextSlot > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimulationHorizonIntegrityFailureShouldRemainSessionLocal(bool draining)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model("session-a", "A").ToJsonString());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        if (draining) runtime.Generate("session-a");
        Tamper(files.Database, "UPDATE sessions SET horizon_revision=1 WHERE id='session-a'");
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
    }

    [Fact]
    public void DivideByZeroShouldGiveACorrectiveArithmeticDiagnostic()
    {
        var model = ManufacturingTests.Model("""[{"tag":"A","expression":{"op":"divide","left":1,"right":0}}]""");
        var result = SimulationDefinitionLoader.Load(model.ToJsonString());
        var error = Assert.Single(result.Errors);
        output.WriteLine($"{error.Code} {error.Path}: {error.Message}");
        Assert.Contains("zero", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
