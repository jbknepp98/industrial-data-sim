using IndustrialDataSim.Runtime;
using Microsoft.Data.Sqlite;

namespace IndustrialDataSim.Tests;

public class QueueAccountingTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(100000)]
    public void AccountingUsesOnlyLiveIndexRegardlessOfHistorySize(int historyRows)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
            runtime.GenerateRound();
        }
        using var db = Open(files.Database);
        using (var command = db.CreateCommand())
        {
            // Deliberately large acknowledged counts must not contribute to totals.
            command.CommandText = """
                WITH RECURSIVE history(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM history WHERE n<$count)
                INSERT INTO batches(session_id,state,start_slot,end_slot,point_count,byte_count,payload,hash,positions)
                SELECT 'session-a','Acknowledged',0,3,9999,999999,NULL,'synthetic','{}' FROM history;
                """;
            command.Parameters.AddWithValue("$count", historyRows);
            command.ExecuteNonQuery();
        }
        foreach (string sql in new[] { QueueQueries.GlobalUsage, QueueQueries.SessionSnapshot })
        {
            using var command = db.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$id", "session-a");
            using var reader = command.ExecuteReader();
            var batchSteps = new List<string>();
            while (reader.Read())
            {
                string detail = reader.GetString(3);
                if (detail.Contains("batches", StringComparison.OrdinalIgnoreCase)) batchSteps.Add(detail);
            }
            Assert.NotEmpty(batchSteps);
            Assert.All(batchSteps, step => Assert.Contains("batch_outstanding", step));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(3, reopened.GetSession("session-a").QueuedPoints);
        Assert.Equal(3, reopened.GetSession("session-b").QueuedPoints);
        var actual = GlobalTotals(db);
        Assert.Equal(6, actual.Points);
        Assert.Equal(reopened.GetSession("session-a").QueuedBytes + reopened.GetSession("session-b").QueuedBytes, actual.Bytes);
    }

    [Fact]
    public void VersionOneMigrationPreservesPayloadsCheckpointsAndOwnership()
    {
        using var files = new RuntimeFixture();
        SessionSnapshot session;
        BatchSnapshot[] batches;
        TagProgress[] progress;
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            session = runtime.GetSession("session-a");
            batches = runtime.Batches("session-a").ToArray();
            progress = runtime.Progress("session-a").ToArray();
        }
        using (var db = Open(files.Database))
        {
            // Restore v1 by removing the v2 index and v3 session progress table.
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE production_preflight; DROP TABLE observations; ALTER TABLE tags DROP COLUMN published_ticks; ALTER TABLE session_tag_progress DROP COLUMN published_ticks; DROP TABLE archives; ALTER TABLE sessions DROP COLUMN archive_id; ALTER TABLE sessions DROP COLUMN cancellation_mode; DROP TABLE session_tag_progress; DROP INDEX batch_outstanding; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using var migrated = new DurableRuntime(files.Database);
        Assert.Equal(session, migrated.GetSession("session-a"));
        Assert.Equal(batches, migrated.Batches("session-a"));
        Assert.Equal(progress, migrated.Progress("session-a"));
        using var check = Open(files.Database);
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, version.ExecuteScalar());
        Assert.Equal(session.QueuedPoints, GlobalTotals(check).Points);
        Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() =>
            migrated.AddSession(RuntimeFixture.Model("conflict").ToJsonString())).Error.Code);
    }

    [Theory]
    [InlineData("before_generation_commit", 0)]
    [InlineData("after_generation_commit", 3)]
    [InlineData("before_acknowledgement_commit", 3)]
    [InlineData("after_acknowledgement_commit", 0)]
    public async Task IndexTotalsFollowDurableCommitBoundaries(string boundary, int expectedPoints)
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.FaultPoint = stage => { if (stage == boundary) throw new SimulatedCrash(); };
            if (boundary.Contains("generation"))
                Assert.Throws<SimulatedCrash>(() => runtime.Generate("session-a"));
            else
            {
                runtime.Generate("session-a");
                await Assert.ThrowsAsync<SimulatedCrash>(() => new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a"));
            }
        }
        using var reopened = new DurableRuntime(files.Database);
        using var db = Open(files.Database);
        Assert.Equal(expectedPoints, GlobalTotals(db).Points);
        Assert.Equal(expectedPoints, reopened.GetSession("session-a").QueuedPoints);
        Assert.Equal(reopened.GetSession("session-a").QueuedBytes, GlobalTotals(db).Bytes);
    }

    [Fact]
    public void SendingAndRecoveredUncertainBatchesRemainChargedToQueue()
    {
        using var files = new RuntimeFixture();
        long bytes;
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            bytes = runtime.GetSession("session-a").QueuedBytes;
            Assert.NotNull(runtime.Claim("session-a"));
            using var db = Open(files.Database);
            Assert.Equal((3L, bytes), GlobalTotals(db));
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(SessionStatus.Uncertain, reopened.GetSession("session-a").Status);
        using var check = Open(files.Database);
        Assert.Equal((3L, bytes), GlobalTotals(check));
    }

    private static (long Points, long Bytes) GlobalTotals(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = QueueQueries.GlobalUsage;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
}
