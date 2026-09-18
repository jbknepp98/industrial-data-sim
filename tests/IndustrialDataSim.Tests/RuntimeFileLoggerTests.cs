using System.Text.Json;
using IndustrialDataSim.Runtime;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Tests;

public class RuntimeFileLoggerTests
{
    [Fact]
    public async Task ConcurrentWritesRotateWithinBoundsAndKeepCompleteRecords()
    {
        using var files = new RuntimeFixture();
        string directory = Path.Combine(files.Folder, "logs");
        using var logger = new RuntimeFileLogger(directory, maximumFileBytes: 4096, retainedFiles: 3);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) Write(logger, $"session-{worker}");
        })));
        var paths = Directory.GetFiles(directory, "*.jsonl");
        Assert.Equal(3, paths.Length);
        foreach (string path in paths)
        {
            Assert.InRange(new FileInfo(path).Length, 1, 4096);
            foreach (string line in File.ReadAllLines(path))
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal("session.test", json.RootElement.GetProperty("eventCode").GetString());
                Assert.Equal("Information", json.RootElement.GetProperty("level").GetString());
            }
        }
        Assert.True(logger.IsAvailable);
        Assert.Equal(0, logger.FailureCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RetentionIncludesActiveFile(int retained)
    {
        using var files = new RuntimeFixture();
        using var logger = new RuntimeFileLogger(files.Folder, maximumFileBytes: 4096, retainedFiles: retained);
        for (int i = 0; i < 40; i++) Write(logger);
        Assert.Equal(retained, Directory.GetFiles(files.Folder, "*.jsonl").Length);
    }

    [Fact]
    public void FiltersDebugAndRejectsUnreviewedMessagesScopesAndExceptions()
    {
        using var files = new RuntimeFixture();
        using var logger = new RuntimeFileLogger(files.Folder);
        Write(logger, level: LogLevel.Debug);
        Assert.Empty(Directory.GetFiles(files.Folder, "*.jsonl"));
        logger.LogError(new Exception("SECRET_EXCEPTION"), "SECRET_MESSAGE");
        using (logger.BeginScope("SECRET_SCOPE")) Write(logger);
        string content = File.ReadAllText(Path.Combine(files.Folder, "runtime.jsonl"));
        Assert.DoesNotContain("SECRET", content);
        Assert.Single(File.ReadAllLines(Path.Combine(files.Folder, "runtime.jsonl")));
    }

    [Fact]
    public void UnavailableDirectoryReportsOnceAndRecoversWithoutLeakingPath()
    {
        using var files = new RuntimeFixture();
        string path = Path.Combine(files.Folder, "SECRET_PATH");
        Directory.CreateDirectory(files.Folder);
        File.WriteAllText(path, "blocks directory creation");
        using var fallback = new StringWriter();
        using var logger = new RuntimeFileLogger(path, fallback: fallback);
        for (int i = 0; i < 10; i++) Write(logger);
        Assert.False(logger.IsAvailable);
        Assert.Equal(11, logger.FailureCount);
        Assert.Single(fallback.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("permissions", fallback.ToString());
        Assert.DoesNotContain("SECRET_PATH", fallback.ToString());
        File.Delete(path);
        Write(logger);
        Assert.True(logger.IsAvailable);
        Assert.Contains("logging.restored", fallback.ToString());
        Assert.Single(File.ReadAllLines(Path.Combine(path, "runtime.jsonl")));
    }

    [Fact]
    public void SecondLoggerCannotRotateFilesOwnedByAnotherLogger()
    {
        using var files = new RuntimeFixture();
        using var first = new RuntimeFileLogger(files.Folder);
        using var warnings = new StringWriter();
        using var second = new RuntimeFileLogger(files.Folder, fallback: warnings);
        Write(second);
        Assert.False(second.IsAvailable);
        Assert.Empty(Directory.GetFiles(files.Folder, "*.jsonl"));
        first.Dispose();
        Write(second);
        Assert.True(second.IsAvailable);
    }

    [Fact]
    public async Task FileFailureAndBrokenFallbackDoNotAffectRuntimeState()
    {
        using var files = new RuntimeFixture();
        string path = Path.Combine(files.Folder, "blocked");
        Directory.CreateDirectory(files.Folder);
        File.WriteAllText(path, "blocks directory creation");
        using var logger = new RuntimeFileLogger(path, fallback: new BrokenWriter());
        using var runtime = new DurableRuntime(files.Database, logger: logger);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        await new SimulatedDelivery(runtime, new()).DeliverOneAsync("session-a");
        Assert.Equal(SessionStatus.Complete, runtime.GetSession("session-a").Status);
        Assert.True(logger.FailureCount > 0);
        Assert.False(logger.IsAvailable);
    }

    [Fact]
    public void InvalidOptionsAndPathsHaveSafeActionableCodes()
    {
        var options = Assert.Throws<RuntimeFailure>(() => new RuntimeFileLogger("logs", maximumFileBytes: 1));
        Assert.Equal("logging.invalid_options", options.Error.Code);
        Assert.Contains("4096 bytes", options.Error.Message);
        var path = Assert.Throws<RuntimeFailure>(() => new RuntimeFileLogger("SECRET_PATH\0"));
        Assert.Equal("logging.invalid_path", path.Error.Code);
        Assert.Contains("writable local directory", path.Error.Message);
        Assert.DoesNotContain("SECRET_PATH", path.Error.Message);
    }

    private static void Write(RuntimeFileLogger logger, string id = "session-a", LogLevel level = LogLevel.Information)
    {
        var entry = new RuntimeLogEvent(DateTimeOffset.UtcNow, "session.test", "Test",
            "Session state changed.", "Inspect the session status before continuing.", id);
        logger.Log(level, new(0, entry.EventCode), entry, null, static (state, _) => state.ToString());
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("SECRET_STDERR_ERROR");
    }
}
