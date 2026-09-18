using System.Text.Json;
using IndustrialDataSim.Runtime;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Tests;

public class RuntimeLoggingTests
{
    [Fact]
    public async Task LifecycleAndDeliveryLogsHaveReadableMessagesAndContext()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        using (var runtime = new DurableRuntime(files.Database, logger: logger))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Pause("session-a");
            runtime.Pause("session-a");
            runtime.Resume("session-a");
            runtime.Generate("session-a");
            Assert.True(await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a"));
            runtime.ReleaseCompleted("session-a");
        }
        foreach (string code in new[] { "runtime.opened", "session.admitted", "session.paused", "session.resumed", "session.completed", "session.tags_released", "runtime.closed" })
            Assert.Single(logger.Entries, e => e.Entry.EventCode == code);
        var acknowledgement = Assert.Single(logger.Entries, e => e.Entry.EventCode == "delivery.acknowledged");
        Assert.Equal(LogLevel.Debug, acknowledgement.Level);
        Assert.Equal("session-a", acknowledgement.Entry.SessionId);
        Assert.NotNull(acknowledgement.Entry.BatchId);
        Assert.Equal(9, acknowledgement.Entry.PointCount);
        Assert.All(logger.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Entry.Message));
            Assert.False(string.IsNullOrWhiteSpace(e.Entry.Action));
            Assert.Equal(TimeSpan.Zero, e.Entry.TimestampUtc.Offset);
        });
        Assert.Contains("session=session-a", acknowledgement.Entry.ToString());
    }

    [Fact]
    public async Task PressureWarningsOccurOnlyOnTransitionsAndExplainRecovery()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        using var runtime = new DurableRuntime(files.Database,
            new() { BatchPoints = 3, SessionQueuePoints = 3, GlobalQueuePoints = 3 }, logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.FreeDiskBytes = () => 0;
        for (int i = 0; i < 20; i++) runtime.Generate("session-a");
        Assert.Single(logger.Entries, e => e.Entry.EventCode == "generation.disk_low");
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
        runtime.FreeDiskBytes = () => long.MaxValue;
        runtime.Generate("session-a");
        for (int i = 0; i < 20; i++) runtime.Generate("session-a");
        Assert.Single(logger.Entries, e => e.Entry.EventCode == "generation.queue_full");
        var delivery = new SimulatedDelivery(runtime, new());
        await delivery.DeliverOneAsync("session-a");
        runtime.Generate("session-a");
        Assert.Equal(2, logger.Entries.Count(e => e.Entry.EventCode == "generation.unblocked"));
        runtime.Generate("session-a");
        Assert.Equal(2, logger.Entries.Count(e => e.Entry.EventCode == "generation.queue_full"));
    }

    [Fact]
    public async Task UncertaintyAndRestartWarnAgainstReplay()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        using (var runtime = new DurableRuntime(files.Database, logger: logger))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            Assert.NotNull(runtime.Claim("session-a")); // Leave a committed Sending batch.
        }
        using var reopened = new DurableRuntime(files.Database, logger: logger);
        var recovery = Assert.Single(logger.Entries, e => e.Entry.EventCode == "delivery.interrupted");
        Assert.Equal(LogLevel.Warning, recovery.Level);
        Assert.Contains("Do not replay", recovery.Entry.Action);
        Assert.False(await new SimulatedDelivery(reopened, new()).DeliverOneAsync("session-a"));
        Assert.Equal(SessionStatus.Uncertain, reopened.GetSession("session-a").Status);
    }

    [Fact]
    public async Task AmbiguousDeliveryLogsBatchAndActionWithoutPayload()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        using var runtime = new DurableRuntime(files.Database, logger: logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        var fake = new FakeHistorian();
        fake.NextWrite("session-a", FakeWriteBehavior.LoseResponseAfterAcceptance);
        Assert.False(await new SimulatedDelivery(runtime, fake).DeliverOneAsync("session-a"));
        var warning = Assert.Single(logger.Entries, e => e.Entry.EventCode == "delivery.uncertain");
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.NotNull(warning.Entry.BatchId);
        Assert.Contains("do not resend", warning.Entry.Action);
    }

    [Fact]
    public void RolledBackWorkDoesNotLogSuccess()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        using var runtime = new DurableRuntime(files.Database, logger: logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.FaultPoint = stage => { if (stage == "before_generation_commit") throw new SimulatedCrash(); };
        Assert.Throws<SimulatedCrash>(() => runtime.Generate("session-a"));
        Assert.DoesNotContain(logger.Entries, e => e.Entry.EventCode == "generation.committed");
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThrowingProviderCannotBreakGenerationOrAcknowledgement(bool throwOnEnable)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, logger: new ThrowingLogger(throwOnEnable));
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Assert.True(await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a"));
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        Assert.True(runtime.LoggingFailureCount > 0);
    }

    [Fact]
    public void FailureContextSurvivesReopenWithoutExposingData()
    {
        using var files = new RuntimeFixture();
        var logger = new CaptureLogger();
        const string privateText = "SECRET_PAYLOAD_DO_NOT_LOG";
        var model = RuntimeFixture.Model(prefix: "SECRET_TAG_DO_NOT_LOG");
        model["generators"]![2]!["value"] = string.Concat(Enumerable.Repeat(privateText, 30));
        using (var runtime = new DurableRuntime(files.Database, new() { BatchBytes = 128 }, logger))
        {
            runtime.AddSession(model.ToJsonString());
            for (int i = 0; i < 10 && runtime.GetSession("session-a").Status == SessionStatus.Ready; i++)
                runtime.Generate("session-a");
        }
        var failure = Assert.Single(logger.Entries, e => e.Entry.EventCode == "generation.point_too_large").Entry;
        Assert.Equal(2, failure.OutputTagIndex);
        Assert.Equal(2, failure.CandidateSlot);
        Assert.NotNull(failure.SampleUtc);
        using var reopened = new DurableRuntime(files.Database);
        string persisted = reopened.GetSession("session-a").ErrorMessage!;
        Assert.Contains("candidate slot 2", persisted);
        Assert.Contains("128-byte", persisted);
        string logs = JsonSerializer.Serialize(logger.Entries.Select(e => e.Entry));
        Assert.DoesNotContain(privateText, logs + persisted);
        Assert.DoesNotContain("SECRET_TAG", logs + persisted);
    }

    [Fact]
    public void ArithmeticFailureIdentifiesOffendingSlotWithoutAdvancingCheckpoint()
    {
        var model = IndustrialDataSim.Core.Configuration.SimulationDefinitionLoader.Load(
            RampSimulationTests.Model(1e308, 1e308).ToJsonString()).Definition!;
        var result = IndustrialDataSim.Core.Simulation.GenerationWindow.Generate(model, 0, 100, 100, 4096);
        Assert.Equal("generation.non_finite_value", result.Error!.Code);
        Assert.Equal("$.session.outputTags[0]", result.Error.Path);
        Assert.Equal(0, result.NextSlot);
        Assert.Equal(0, result.PointCount);
        Assert.Equal(0, result.FailureContext!.OutputTagIndex);
        Assert.Equal(3, result.FailureContext.CandidateSlot);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 1, DateTimeKind.Utc), result.FailureContext.SampleUtc);
        Assert.Contains("Reduce", result.Error.Message);
    }

    [Fact]
    public void StorageErrorsAreActionableRedactedAndLoggedOnceAcrossNestedCalls()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={files.Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_checkpoint BEFORE UPDATE OF next_slot ON sessions BEGIN SELECT RAISE(ABORT,'SECRET_STORAGE_DETAIL'); END;";
            command.ExecuteNonQuery();
        }
        var logger = new CaptureLogger();
        using var reopened = new DurableRuntime(files.Database, logger: logger);
        var failure = Assert.Throws<RuntimeFailure>(() => reopened.GenerateRound());
        Assert.Equal("runtime.storage_failure", failure.Error.Code);
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error).Entry;
        Assert.Equal("Generate", entry.Operation);
        Assert.Equal("session-a", entry.SessionId);
        Assert.Contains("disk space", entry.Action);
        Assert.DoesNotContain("SECRET_STORAGE_DETAIL", entry.ToString());
        Assert.Equal(0, reopened.GetSession("session-a").NextSlot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentedDriverContinuesWhenDeliveryFreesCapacity(bool prefillWholeSession)
    {
        using var files = new RuntimeFixture();
        using var logger = new RuntimeFileLogger(Path.Combine(files.Folder, "logs"));
        using var runtime = new DurableRuntime(files.Database,
            new() { BatchPoints = 3, SessionQueuePoints = prefillWholeSession ? 9 : 3, GlobalQueuePoints = prefillWholeSession ? 9 : 3 }, logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        if (prefillWholeSession)
        {
            runtime.Generate("session-a");
            runtime.Generate("session-a");
            Assert.Equal(SessionStatus.Draining, runtime.GetSession("session-a").Status);
        }
        var delivery = new SimulatedDelivery(runtime, new());
        for (int round = 0; round < 100; round++)
        {
            var generation = runtime.GenerateRound();
            int delivered = await delivery.RunRoundAsync();
            if (runtime.Sessions().All(s => s.Status == SessionStatus.Complete)) break;
            if (generation.All(turn => !turn.Progressed) && delivered == 0) break;
        }
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        Assert.Equal(9, runtime.GetSession("session-a").NextSlot);
    }

    private sealed class CaptureLogger : ILogger<DurableRuntime>
    {
        public List<(LogLevel Level, RuntimeLogEvent Entry)> Entries { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(error);
            Entries.Add((level, Assert.IsType<RuntimeLogEvent>(state)));
        }
    }

    private sealed class ThrowingLogger(bool throwOnEnable) : ILogger<DurableRuntime>
    {
        public bool IsEnabled(LogLevel level) => throwOnEnable ? throw new IOException("SECRET_PROVIDER_ERROR") : true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => throw new IOException("SECRET_PROVIDER_ERROR");
    }
}
