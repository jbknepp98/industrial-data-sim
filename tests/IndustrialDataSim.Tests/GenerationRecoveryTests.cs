using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class GenerationRecoveryTests
{
    private static JsonObject OversizedModel()
    {
        var model = RuntimeFixture.Model();
        model["generators"]![2]!["value"] = new string('<', 100);
        return model;
    }

    private static void FailGeneration(string database, string json)
    {
        using var runtime = new DurableRuntime(database, new() { BatchBytes = 128 });
        runtime.AddSession(json);
        for (int i = 0; i < 10 && runtime.GetSession("session-a").Status == SessionStatus.Ready; i++)
            runtime.Generate("session-a");
        Assert.Equal("generation.point_too_large", runtime.GetSession("session-a").ErrorCode);
    }

    [Fact]
    public void LargerLimitPreservesStateAndResumesExactStreamAcrossAnotherReopen()
    {
        using var files = new RuntimeFixture();
        string json = OversizedModel().ToJsonString();
        FailGeneration(files.Database, json);
        var larger = new RuntimeLimits { BatchBytes = 4096, CandidateSlotsPerTurn = 1 };
        using (var runtime = new DurableRuntime(files.Database, larger))
        {
            var before = runtime.GetSession("session-a");
            var batches = runtime.Batches("session-a").ToArray();
            var positions = runtime.Progress("session-a").ToArray();
            Assert.Equal(SessionStatus.Failed, before.Status); // Opening is not consent to retry.
            Assert.NotEmpty(batches);
            runtime.RetryGeneration("session-a");
            Assert.Equal(before with { Status = SessionStatus.Ready, ErrorCode = null, ErrorMessage = null }, runtime.GetSession("session-a"));
            Assert.Equal(batches, runtime.Batches("session-a"));
            Assert.Equal(positions, runtime.Progress("session-a"));
            Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() =>
                runtime.AddSession(RuntimeFixture.Model("conflict").ToJsonString())).Error.Code);
        }
        using var reopened = new DurableRuntime(files.Database, larger);
        for (int i = 0; i < 100 && reopened.GetSession("session-a").Status == SessionStatus.Ready; i++)
            reopened.Generate("session-a");
        Assert.Equal(SessionStatus.Draining, reopened.GetSession("session-a").Status);
        var actual = new JsonObject();
        var model = SimulationDefinitionLoader.Load(json).Definition!;
        foreach (var tag in model.Session.OutputTags) actual[tag.Name] = new JsonArray();
        foreach (var batch in reopened.Batches("session-a"))
            foreach (var (tag, points) in JsonNode.Parse(batch.Payload!)!.AsObject())
                foreach (var point in points!.AsArray()) actual[tag]!.AsArray().Add(point!.DeepClone());
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(DryRun.Generate(model).Data), actual));
    }

    [Fact]
    public void InsufficientLimitLeavesFailureAndPayloadUntouched()
    {
        using var files = new RuntimeFixture();
        FailGeneration(files.Database, OversizedModel().ToJsonString());
        using var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 });
        var before = runtime.GetSession("session-a");
        var batches = runtime.Batches("session-a").ToArray();
        var error = Assert.Throws<RuntimeFailure>(() => runtime.RetryGeneration("session-a"));
        Assert.Equal("runtime.generation_limit_unresolved", error.Error.Code);
        Assert.Contains("128-byte", error.Message);
        Assert.Contains("RetryGeneration", error.Message);
        Assert.Equal(before, runtime.GetSession("session-a"));
        Assert.Equal(batches, runtime.Batches("session-a"));
    }

    [Fact]
    public void RecoveryLooksPastSuppressedSlotsEvenWhenNewTurnLengthIsSmaller()
    {
        using var files = new RuntimeFixture();
        var model = OversizedModel();
        model["session"]!["outputTags"]![0]!["valueType"] = "boolean";
        model["generators"]![0]!["value"] = false;
        model["session"]!["outputTags"]![1]!["valueType"] = "number";
        model["generators"]![1] = JsonNode.Parse("""
            {"tag":"A.1","kind":"booleanGate","triggerTag":"A.0","whenFalse":"pauseAndSuppress",
             "pattern":{"kind":"ramp","startValue":0,"ratePerSecond":1}}
            """);
        using (var first = new DurableRuntime(files.Database, new() { BatchPoints = 1, BatchBytes = 128 }))
        {
            first.AddSession(model.ToJsonString());
            first.Generate("session-a");
            first.Generate("session-a");
            Assert.Equal(SessionStatus.Failed, first.GetSession("session-a").Status);
            Assert.Equal(1, first.GetSession("session-a").NextSlot);
        }
        using (var small = new DurableRuntime(files.Database, new() { BatchBytes = 128, CandidateSlotsPerTurn = 1 }))
            Assert.Equal("runtime.generation_limit_unresolved",
                Assert.Throws<RuntimeFailure>(() => small.RetryGeneration("session-a")).Error.Code);
        using var larger = new DurableRuntime(files.Database, new() { BatchBytes = 4096, CandidateSlotsPerTurn = 1 });
        larger.RetryGeneration("session-a");
        Assert.Equal(1, larger.GetSession("session-a").NextSlot);
        Assert.Equal(SessionStatus.Ready, larger.GetSession("session-a").Status);
    }

    [Fact]
    public void InFlightSubmissionBlocksGenerationRecovery()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 });
        runtime.AddSession(OversizedModel().ToJsonString());
        runtime.Generate("session-a");
        Assert.NotNull(runtime.Claim("session-a"));
        runtime.Generate("session-a");
        var before = runtime.GetSession("session-a");
        Assert.Equal(SessionStatus.Failed, before.Status);
        Assert.Equal("runtime.delivery_unresolved", Assert.Throws<RuntimeFailure>(() => runtime.RetryGeneration("session-a")).Error.Code);
        Assert.Equal(before, runtime.GetSession("session-a"));
        Assert.Equal(BatchStatus.Sending, Assert.Single(runtime.Batches("session-a")).Status);
    }

    [Theory]
    [InlineData("before_generation_recovery_commit", SessionStatus.Failed)]
    [InlineData("after_generation_recovery_commit", SessionStatus.Ready)]
    public void RecoveryCommitBoundaryIsAtomic(string boundary, SessionStatus expected)
    {
        using var files = new RuntimeFixture();
        FailGeneration(files.Database, OversizedModel().ToJsonString());
        long checkpoint;
        BatchSnapshot[] batches;
        using (var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 4096 }))
        {
            checkpoint = runtime.GetSession("session-a").NextSlot;
            batches = runtime.Batches("session-a").ToArray();
            runtime.FaultPoint = stage => { if (stage == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.RetryGeneration("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(expected, reopened.GetSession("session-a").Status);
        Assert.Equal(checkpoint, reopened.GetSession("session-a").NextSlot);
        Assert.Equal(batches, reopened.Batches("session-a"));
        Assert.Equal(expected == SessionStatus.Ready, reopened.GetSession("session-a").ErrorCode is null);
    }

    [Theory]
    [InlineData("UPDATE sessions SET state='Paused'", "runtime.cannot_retry_generation")]
    [InlineData("UPDATE sessions SET state='Uncertain'", "runtime.cannot_retry_generation")]
    [InlineData("UPDATE sessions SET error_code='generation.non_finite_value'", "runtime.cannot_retry_generation")]
    [InlineData("UPDATE sessions SET config_hash='changed'", "runtime.configuration_integrity")]
    [InlineData("UPDATE sessions SET next_slot=total_slots", "runtime.recovery_checkpoint")]
    [InlineData("UPDATE tags SET owner=NULL", "runtime.recovery_ownership")]
    [InlineData("UPDATE batches SET state='Uncertain'", "runtime.delivery_unresolved")]
    public void OtherFailuresAndUnresolvedDeliveryCannotBeReclassified(string mutation, string expectedCode)
    {
        using var files = new RuntimeFixture();
        FailGeneration(files.Database, OversizedModel().ToJsonString());
        using (var db = new SqliteConnection($"Data Source={files.Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = mutation;
            command.ExecuteNonQuery();
        }
        using var runtime = new DurableRuntime(files.Database);
        var before = runtime.GetSession("session-a");
        var batches = runtime.Batches("session-a").ToArray();
        var error = Assert.Throws<RuntimeFailure>(() => runtime.RetryGeneration("session-a"));
        Assert.Equal(expectedCode, error.Error.Code);
        Assert.Equal(before, runtime.GetSession("session-a"));
        Assert.Equal(batches, runtime.Batches("session-a"));
    }
}
