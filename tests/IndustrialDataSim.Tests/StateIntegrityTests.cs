using System.Text;
using IndustrialDataSim.Cli;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class StateIntegrityTests
{
    private static void Corrupt(string database, string sql, string value)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("state", "private-state")]
    [InlineData("state", "0")]
    [InlineData("cancellation_mode", "private-mode")]
    [InlineData("next_slot", "-1")]
    public void SavedInvalidLifecycleReturnsSafeActionableDiagnostic(string column, string value)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        // The column comes only from this fixed test list.
        Corrupt(files.Database, $"UPDATE sessions SET {column}=$value", value);
        var response = CliApplication.ExecuteLiveRequest(runtime, new(1, "status", "session-a"));
        Assert.Equal(1, response.ExitCode);
        string json = Encoding.UTF8.GetString(response.Json);
        Assert.Contains("runtime.state_integrity", json);
        Assert.Contains("restore", json);
        Assert.DoesNotContain("private-", json);
    }

    [Theory]
    [InlineData("private-json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"A.0\":-1}")]
    [InlineData("{\"A.0\":0}")]
    [InlineData("{\"A.0\":0,\"A.0\":1}")]
    public async Task InvalidPositionsFailBeforeSubmissionIntent(string positions)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Corrupt(files.Database, "UPDATE batches SET positions=$value", positions);
        var fake = new FakeHistorian();
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a"));
        Assert.Equal("runtime.progress_integrity", failure.Error.Code);
        Assert.DoesNotContain("private-json", failure.Error.Message);
        Assert.Equal(0, fake.Calls("session-a"));
        Assert.Equal(BatchStatus.Pending, Assert.Single(runtime.Batches("session-a")).Status);
        Assert.All(runtime.Progress("session-a"), p => Assert.Null(p.SubmittedTicks));
    }

    [Fact]
    public async Task PositionCorruptionAfterSubmissionCannotCommitAcknowledgement()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            var delivery = new SimulatedDelivery(runtime, new FakeHistorian())
            {
                FaultPoint = stage => Corrupt(files.Database, "UPDATE batches SET positions=$value", "{\"A.0\":0}")
            };
            await Assert.ThrowsAsync<RuntimeFailure>(() => delivery.DeliverOneAsync("session-a"));
            Assert.Equal(BatchStatus.Sending, Assert.Single(runtime.Batches("session-a")).Status);
            Assert.All(runtime.Progress("session-a"), p => Assert.Null(p.AcknowledgedTicks));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(SessionStatus.Uncertain, reopened.GetSession("session-a").Status);
    }

    [Theory]
    [InlineData("{\"version\":1,\"action\":\"pause\",\"action\":\"stop\"}")]
    [InlineData("{\"version\":1,\"action\":\"stop\",\"private-field\":1}")]
    [InlineData("{\"version\":1,\"action\":\"\\uD800\"}")]
    [InlineData("[]")]
    public void MalformedControlJsonIsRejectedWithoutEchoingInput(string json)
    {
        var failure = Assert.Throws<RuntimeFailure>(() => LocalControlProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("host.invalid_request", failure.Error.Code);
        Assert.DoesNotContain("private-field", failure.Error.Message);
    }
}
