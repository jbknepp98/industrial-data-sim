using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Simulation;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class DurableGenerationTests
{
    [Theory]
    [InlineData("constant")]
    [InlineData("ramp")]
    [InlineData("staircase")]
    [InlineData("random-staircase")]
    [InlineData("random-integer-hold")]
    [InlineData("sequence")]
    [InlineData("boolean-trigger")]
    [InlineData("boolean-gate")]
    public void ReopenedWindowsMatchOriginalPreviewForEveryPattern(string example)
    {
        using var files = new RuntimeFixture();
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", example + "-simulation.json"));
        var model = SimulationDefinitionLoader.Load(json).Definition!;
        string id = model.Session.SessionId;
        var limits = new RuntimeLimits { BatchPoints = 37, CandidateSlotsPerTurn = 113 };
        using (var first = new DurableRuntime(files.Database, limits))
        {
            first.AddSession(json);
            first.Generate(id);
        }
        using var reopened = new DurableRuntime(files.Database, limits);
        for (int i = 0; reopened.GetSession(id).Status == SessionStatus.Ready && i < 10000; i++)
            Assert.True(reopened.Generate(id).Progressed);
        Assert.NotEqual(SessionStatus.Ready, reopened.GetSession(id).Status);
        var actual = new JsonObject();
        foreach (var tag in model.Session.OutputTags) actual[tag.Name] = new JsonArray();
        foreach (var batch in reopened.Batches(id))
        {
            Assert.InRange(batch.PointCount, 1, limits.BatchPoints);
            Assert.Equal(batch.ByteCount, Encoding.UTF8.GetByteCount(batch.Payload!));
            Assert.InRange(batch.ByteCount, 2, limits.BatchBytes);
            foreach (var (tag, points) in JsonNode.Parse(batch.Payload!)!.AsObject())
                foreach (var point in points!.AsArray()) actual[tag]!.AsArray().Add(point!.DeepClone());
        }
        var expected = JsonSerializer.SerializeToNode(DryRun.Generate(model).Data);
        Assert.True(JsonNode.DeepEquals(expected, actual), "Resumed batches differ from the original sample-grid preview.");
        Assert.Equal(GenerationWindow.TotalSlots(model), reopened.GetSession(id).NextSlot);
    }

    [Theory]
    [InlineData("before_generation_commit", 0)]
    [InlineData("after_generation_commit", 1)]
    public void CrashBoundaryCommitsBothPayloadAndCursorOrNeither(string boundary, int expectedBatches)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 2 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.FaultPoint = stage => { if (stage == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.Generate("session-a"));
        }
        using var reopened = new DurableRuntime(files.Database, new() { BatchPoints = 2 });
        Assert.Equal(expectedBatches, reopened.Batches("session-a").Count);
        Assert.Equal(expectedBatches * 2L, reopened.GetSession("session-a").NextSlot);
        reopened.Generate("session-a");
        var batches = reopened.Batches("session-a");
        Assert.Equal(expectedBatches + 1, batches.Count);
        Assert.Equal(expectedBatches * 2L, batches[^1].StartSlot);
    }

    [Fact]
    public void QueueAndDiskPressureDoNotAdvanceCheckpoint()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new()
        { BatchPoints = 3, SessionQueuePoints = 3, GlobalQueuePoints = 3 });
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.FreeDiskBytes = () => 0;
        Assert.Contains("Disk headroom", runtime.Generate("session-a").Reason);
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
        runtime.FreeDiskBytes = () => long.MaxValue;
        Assert.True(runtime.Generate("session-a").Progressed);
        Assert.Contains("Queue limit", runtime.Generate("session-a").Reason);
        Assert.Equal(3, runtime.GetSession("session-a").NextSlot);
        Assert.Single(runtime.Batches("session-a"));
    }

    [Fact]
    public void EscapedPointTooLargeFailsWithoutPartialCommit()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 });
        var model = RuntimeFixture.Model();
        model["generators"]![2]!["value"] = new string('<', 100);
        runtime.AddSession(model.ToJsonString());
        runtime.Generate("session-a"); // Small numeric/Boolean points may fill the first batch.
        var before = runtime.GetSession("session-a");
        runtime.Generate("session-a");
        var after = runtime.GetSession("session-a");
        Assert.Equal(SessionStatus.Failed, after.Status);
        Assert.Equal(before.NextSlot, after.NextSlot);
        Assert.Equal("generation.point_too_large", after.ErrorCode);
        Assert.All(runtime.Batches("session-a"), batch => Assert.True(batch.ByteCount <= 128));
    }

    [Fact]
    public void SuppressedSlotsAdvanceWithoutPlaceholderBatches()
    {
        using var files = new RuntimeFixture();
        var model = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "boolean-gate-simulation.json")))!.AsObject();
        model["generators"]![0] = JsonNode.Parse("""{"tag":"Example.Ready.Boolean","kind":"constant","value":false}""");
        using var runtime = new DurableRuntime(files.Database, new() { CandidateSlotsPerTurn = 1 });
        runtime.AddSession(model.ToJsonString());
        string id = model["session"]!["sessionId"]!.GetValue<string>();
        runtime.Generate(id); // Trigger emits.
        runtime.Generate(id); // First gate suppresses.
        runtime.Generate(id); // Second gate suppresses.
        Assert.Single(runtime.Batches(id));
        Assert.Equal(3, runtime.GetSession(id).NextSlot);
        Assert.Equal(1, runtime.GetSession(id).QueuedPoints);
    }
}

internal sealed class SimulatedCrash : Exception;
