using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// Optional local JSON-lines sink for runtime events. The host owns its lifetime.
/// Records contain readable messages and actions as well as structured context.
/// Use a dedicated directory for each runtime; files are operational diagnostics,
/// never a substitute for durable state or evidence that a write was accepted.
/// </summary>
public sealed class RuntimeFileLogger : ILogger<DurableRuntime>, IDisposable
{
    private readonly object sync = new();
    private readonly string directory;
    private readonly int maximumFileBytes;
    private readonly int retainedFiles;
    private readonly LogLevel minimumLevel;
    private readonly TextWriter fallback;
    private FileStream? owner;
    private bool disposed;
    private long failureCount;
    private bool unavailable;

    public long FailureCount { get { lock (sync) return failureCount; } }
    public bool IsAvailable { get { lock (sync) return !unavailable && !disposed; } }

    /// <param name="retainedFiles">Total files, including runtime.jsonl; oldest files are deleted on rotation.</param>
    public RuntimeFileLogger(string directory, LogLevel minimumLevel = LogLevel.Information,
        int maximumFileBytes = 1024 * 1024, int retainedFiles = 5, TextWriter? fallback = null)
    {
        if (maximumFileBytes < 4096 || retainedFiles is < 1 or > 100 ||
            minimumLevel < LogLevel.Trace || minimumLevel > LogLevel.None)
            throw new RuntimeFailure("logging.invalid_options", "Use a log file limit of at least 4096 bytes, 1 through 100 retained files, and a valid log level.");
        // Do not expose an invalid path through a framework exception message.
        try { this.directory = Path.GetFullPath(directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { throw new RuntimeFailure("logging.invalid_path", "Log directory is invalid or inaccessible. Supply a valid writable local directory."); }
        this.maximumFileBytes = maximumFileBytes;
        this.retainedFiles = retainedFiles;
        this.minimumLevel = minimumLevel;
        this.fallback = fallback ?? Console.Error;
        lock (sync)
        {
            try { AcquireOwner(); }
            catch (Exception error) when (IsStorageError(error)) { ReportFailure(); }
        }
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel < LogLevel.None;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Ignore arbitrary formatted messages, scopes, and exception text. This
        // sink accepts only the runtime's reviewed, explicitly selected fields.
        if (!IsEnabled(logLevel) || state is not RuntimeLogEvent entry) return;
        lock (sync)
        {
            if (disposed) { ReportFailure(); return; }
            try
            {
                AcquireOwner();
                var fields = entry.ToDictionary(pair => pair.Key, pair => pair.Value);
                fields.Add("level", logLevel.ToString());
                byte[] record = JsonSerializer.SerializeToUtf8Bytes(fields);
                if (record.Length + 1 > maximumFileBytes)
                {
                    ReportFailure();
                    return;
                }
                string path = LogPath(0);
                if (File.Exists(path) && new FileInfo(path).Length > maximumFileBytes - record.Length - 1)
                    Rotate();
                using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    file.Write(record);
                    file.WriteByte((byte)'\n');
                }
                if (unavailable)
                    WriteFallback("logging.restored: Operational file logging has resumed. Some earlier events were lost; inspect SQLite for current session state.");
                unavailable = false;
            }
            catch (Exception error) when (IsStorageError(error)) { ReportFailure(); }
        }
    }

    private void AcquireOwner()
    {
        if (owner is not null) return;
        Directory.CreateDirectory(directory);
        owner = new FileStream(Path.Combine(directory, "runtime.log.owner"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string LogPath(int index) => Path.Combine(directory,
        index == 0 ? "runtime.jsonl" : $"runtime.{index}.jsonl");

    private void Rotate()
    {
        File.Delete(LogPath(retainedFiles - 1));
        for (int index = retainedFiles - 2; index >= 0; index--)
            if (File.Exists(LogPath(index))) File.Move(LogPath(index), LogPath(index + 1), overwrite: true);
    }

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;
    private void ReportFailure()
    {
        failureCount++;
        if (!unavailable)
            WriteFallback("logging.file_unavailable: Operational file logging is unavailable. Check free disk space, directory permissions, and whether another logger owns this directory. Events may be lost; inspect SQLite for authoritative session state.");
        unavailable = true;
    }
    private void WriteFallback(string message)
    {
        try { fallback.WriteLine(message); }
        catch (Exception) { /* No recursive logging, including when stderr has closed. */ }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            owner?.Dispose();
        }
    }
}
