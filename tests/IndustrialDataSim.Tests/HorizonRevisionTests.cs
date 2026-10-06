using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class HorizonRevisionTests
{
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-09-01T00:00:06Z");
    private const string Request = "6886f6d2-d0e9-43d7-9406-1c31cb34cb54";

    [Fact]
    public async Task CompletedSessionExtendsWithoutChangingAdmittedDefinitionOrPriorBatches()
    {
        using var files = new RuntimeFixture();
        string json = RuntimeFixture.Model().ToJsonString();
        BatchSnapshot[] before;
        long cursor;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(json);
            runtime.Generate("session-a");
            await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a");
            Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
            before = runtime.Batches("session-a").ToArray();
            cursor = runtime.GetSession("session-a").NextSlot;
            var receipt = runtime.ExtendHorizon("session-a", End, 0, Request);
            Assert.Equal(cursor, receipt.Cursor);
            Assert.Equal(SessionStatus.Ready, receipt.ResultingState);
            Assert.Equal(before, runtime.Batches("session-a"));
            Assert.Equal(receipt, runtime.ExtendHorizon("session-a", End, 0, Request));
            Assert.Equal("archive.unresolved", Assert.Throws<RuntimeFailure>(() => runtime.Archive("session-a", files.Folder)).Error.Code);
        }
        using (var reopened = new DurableRuntime(files.Database))
        {
            Assert.Equal((1, End), reopened.Horizon("session-a"));
            Assert.Equal(cursor, reopened.GetSession("session-a").NextSlot);
            reopened.Generate("session-a");
            Assert.Equal(cursor, reopened.Batches("session-a")[^1].StartSlot);
            Assert.Equal(before[0], reopened.Batches("session-a")[0]);
        }
        Assert.Equal(json, Query(files.Database, "SELECT config FROM sessions"));
        Assert.Equal(1L, Query(files.Database, "SELECT horizon_revision FROM batches ORDER BY id DESC LIMIT 1"));
        Assert.Equal(0L, Query(files.Database, "SELECT horizon_revision FROM batches ORDER BY id LIMIT 1"));
    }

    [Theory]
    [InlineData("before_horizon_commit", 0)]
    [InlineData("after_horizon_commit", 1)]
    public void InterruptedMutationCommitsWholeRevisionOrNothing(string boundary, int revision)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.FaultPoint = point => { if (point == boundary) throw new SimulatedCrash(); };
            Assert.Throws<SimulatedCrash>(() => runtime.ExtendHorizon("session-a", End, 0, Request));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(revision, reopened.Horizon("session-a").Revision);
        Assert.Equal(revision == 0 ? 9 : 18, reopened.GetSession("session-a").TotalSlots);
        Assert.Equal(0, reopened.GetSession("session-a").NextSlot);
        var receipt = reopened.ExtendHorizon("session-a", End, 0, Request);
        Assert.Equal(1, receipt.Revision);
        Assert.Equal(receipt, reopened.ExtendHorizon("session-a", End, 0, Request));
    }

    [Fact]
    public void PausedStateAndOldIdempotentReceiptSurviveLaterExtension()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Pause("session-a");
        var first = runtime.ExtendHorizon("session-a", End, 0, Request);
        runtime.ExtendHorizon("session-a", End.AddSeconds(2), 1, Guid.NewGuid().ToString());
        Assert.Equal(SessionStatus.Paused, runtime.GetSession("session-a").Status);
        Assert.Equal(first, runtime.ExtendHorizon("session-a", End, 0, Request));
        Assert.Equal("extension.request_conflict", Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", End.AddSeconds(1), 0, Request)).Error.Code);
        Assert.Equal("extension.stale_revision", Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", End.AddSeconds(3), 0, Guid.NewGuid().ToString())).Error.Code);
    }

    [Theory]
    [InlineData("UPDATE sessions SET state='Failed'", "extension.invalid_state")]
    [InlineData("UPDATE sessions SET state='Cancelled'", "extension.invalid_state")]
    [InlineData("UPDATE sessions SET state='Cancelling'", "extension.invalid_state")]
    [InlineData("UPDATE sessions SET state='Draining'", "extension.invalid_state")]
    [InlineData("UPDATE sessions SET state='Uncertain'", "extension.invalid_state")]
    [InlineData("UPDATE tags SET owner=NULL", "extension.ownership_released")]
    [InlineData("UPDATE sessions SET archive_id='archived'", "extension.archived")]
    public void IneligibleLifecycleCannotBeClearedByExtension(string mutation, string code)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Query(files.Database, mutation);
        using var reopened = new DurableRuntime(files.Database);
        var before = reopened.GetSession("session-a");
        Assert.Equal(code, Assert.Throws<RuntimeFailure>(() => reopened.ExtendHorizon("session-a", End, 0, Request)).Error.Code);
        Assert.Equal(before, reopened.GetSession("session-a"));
        Assert.Equal(0, reopened.Horizon("session-a").Revision);
    }

    [Fact]
    public void PendingWorkIsPreservedAndProductionExtensionDoesNotPublish()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            var before = runtime.Batches("session-a").ToArray();
            Assert.Equal("extension.unresolved_delivery", Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", End, 0, Request)).Error.Code);
            Assert.Equal(before, runtime.Batches("session-a"));
        }
        using var production = new DurableRuntime(Path.Combine(files.Folder, "production.db"), mode: ExecutionMode.Production);
        production.AdmitSession(RuntimeFixture.Model().ToJsonString());
        Assert.Equal(1, production.ExtendHorizon("session-a", End, 0, Request).Revision);
        Assert.Empty(production.Batches("session-a"));
    }

    [Theory]
    [InlineData("UPDATE horizon_revisions SET new_total=100")]
    [InlineData("UPDATE horizon_revisions SET previous_end=1")]
    [InlineData("UPDATE horizon_revisions SET cursor=100")]
    [InlineData("UPDATE horizon_revisions SET config_hash='PRIVATE_BROKEN_METADATA'")]
    [InlineData("UPDATE sessions SET horizon_revision=2")]
    [InlineData("DELETE FROM horizon_revisions")]
    [InlineData("UPDATE sessions SET total_slots=100")]
    public void CorruptRevisionStopsGenerationWithoutLeakingMetadata(string mutation)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.ExtendHorizon("session-a", End, 0, Request);
        }
        Query(files.Database, mutation);
        using var reopened = new DurableRuntime(files.Database);
        var error = Assert.Throws<RuntimeFailure>(() => reopened.Generate("session-a"));
        Assert.Equal("extension.integrity", error.Error.Code);
        Assert.DoesNotContain("PRIVATE_BROKEN_METADATA", error.Message);
        Assert.Contains("restore verified state", error.Message);
        Assert.Equal(SessionStatus.Failed, reopened.GetSession("session-a").Status);
        Assert.Empty(reopened.Batches("session-a"));
    }

    [Fact]
    public void VersionSixMigrationPreservesPendingPayloadAndCursor()
    {
        using var files = new RuntimeFixture();
        BatchSnapshot[] batches;
        SessionSnapshot session;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            batches = runtime.Batches("session-a").ToArray(); session = runtime.GetSession("session-a");
        }
        Query(files.Database, "DROP TABLE horizon_revisions; ALTER TABLE sessions DROP COLUMN horizon_revision; ALTER TABLE batches DROP COLUMN horizon_revision; PRAGMA user_version=6;");
        using (var reopened = new DurableRuntime(files.Database))
        {
            Assert.Equal(session, reopened.GetSession("session-a"));
            Assert.Equal(batches, reopened.Batches("session-a"));
            Assert.Equal(0, reopened.Horizon("session-a").Revision);
        }
        Assert.Equal(7L, Query(files.Database, "PRAGMA user_version"));
    }

    [Fact]
    public void LaterEndWithinSameSamplingIntervalAddsNoWorkAndIsRejected()
    {
        using var files = new RuntimeFixture();
        var model = RuntimeFixture.Model(); model["session"]!["endUtc"] = "2026-09-01T00:00:02.100Z";
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(model.ToJsonString());
        var error = Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a", End.AddSeconds(-3.5), 0, Request));
        Assert.Equal("extension.no_new_slots", error.Error.Code);
        Assert.Equal(0, runtime.Horizon("session-a").Revision);
    }

    [Fact]
    public void FailedVersionSevenMigrationRollsBackAddedColumnsAndVersion()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database)) runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        // Simulate an unexpected conflicting table in a legacy database. The two
        // ALTER statements must roll back when the later CREATE fails.
        Query(files.Database, "DROP TABLE horizon_revisions; ALTER TABLE sessions DROP COLUMN horizon_revision; ALTER TABLE batches DROP COLUMN horizon_revision; PRAGMA user_version=6; CREATE TABLE horizon_revisions(unexpected TEXT);");
        Assert.Equal("runtime.storage_failure", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
        Assert.Equal(6L, Query(files.Database, "PRAGMA user_version"));
        Assert.Equal(0L, Query(files.Database, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name='horizon_revision'"));
        Assert.Equal(0L, Query(files.Database, "SELECT COUNT(*) FROM pragma_table_info('batches') WHERE name='horizon_revision'"));
        Assert.Equal(1L, Query(files.Database, "SELECT COUNT(*) FROM sessions"));
    }

    [Theory]
    [InlineData("request", "extension.request_id")]
    [InlineData("offset", "extension.invalid_request")]
    [InlineData("revision", "extension.invalid_request")]
    [InlineData("earlier", "extension.end_not_later")]
    public void InvalidArgumentsReturnSafeGuidanceWithoutMutation(string field, string code)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var error = Assert.Throws<RuntimeFailure>(() => runtime.ExtendHorizon("session-a",
            field == "offset" ? End.ToOffset(TimeSpan.FromHours(1)) : field == "earlier" ? End.AddSeconds(-4) : End,
            field == "revision" ? -1 : 0, field == "request" ? "PRIVATE_INPUT" : Request));
        Assert.Equal(code, error.Error.Code);
        Assert.DoesNotContain("PRIVATE_INPUT", error.Message);
        Assert.Equal(0, runtime.Horizon("session-a").Revision);
        Assert.Equal(0, runtime.GetSession("session-a").NextSlot);
    }

    private static object? Query(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
