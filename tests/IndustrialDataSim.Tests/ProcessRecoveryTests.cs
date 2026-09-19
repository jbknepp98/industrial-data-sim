using System.Diagnostics;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class ProcessRecoveryTests
{
    [Theory]
    [InlineData("before_generation_commit", 0, null)]
    [InlineData("after_generation_commit", 3, BatchStatus.Pending)]
    [InlineData("after_sending_commit", 3, BatchStatus.Uncertain)]
    [InlineData("after_transport_before_acknowledgement", 3, BatchStatus.Uncertain)]
    [InlineData("before_acknowledgement_commit", 3, BatchStatus.Uncertain)]
    [InlineData("after_acknowledgement_commit", 3, BatchStatus.Acknowledged)]
    public async Task KilledProcessRecoversWithoutRunningCleanup(string boundary, long cursor, BatchStatus? batchState)
    {
        using var files = new RuntimeFixture();
        using var child = Start(files, boundary);
        try
        {
            string? signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("boundary-reached", signal);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var recovered = new DurableRuntime(files.Database);
            Assert.Equal(cursor, recovered.GetSession("session-a").NextSlot);
            var batches = recovered.Batches("session-a");
            if (batchState is null) Assert.Empty(batches);
            else Assert.Equal(batchState, Assert.Single(batches).Status);
            if (batchState == BatchStatus.Uncertain)
            {
                Assert.Equal(SessionStatus.Uncertain, recovered.GetSession("session-a").Status);
                var fake = new FakeHistorian();
                Assert.False(await new SimulatedDelivery(recovered, fake).DeliverOneAsync("session-a"));
                Assert.Equal(0, fake.Calls("session-a"));
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData("before_cancellation_commit", SessionStatus.Ready, BatchStatus.Pending)]
    [InlineData("after_cancellation_commit", SessionStatus.Cancelled, BatchStatus.Discarded)]
    public async Task KilledCancellationProcessPreservesAtomicOutcome(string boundary, SessionStatus status, BatchStatus batchStatus)
    {
        using var files = new RuntimeFixture();
        using var child = Start(files, boundary);
        try
        {
            Assert.Equal("boundary-reached", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var recovered = new DurableRuntime(files.Database);
            Assert.Equal(status, recovered.GetSession("session-a").Status);
            Assert.Equal(3, recovered.GetSession("session-a").NextSlot);
            var batch = Assert.Single(recovered.Batches("session-a"));
            Assert.Equal(batchStatus, batch.Status);
            Assert.Equal(batchStatus == BatchStatus.Discarded, batch.Payload is null);
            Assert.Equal(batchStatus == BatchStatus.Discarded ? 0 : 3, recovered.GetSession("session-a").QueuedPoints);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task SeparateProcessCannotOpenOwnedDatabase()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        using var child = Start(files, "after_generation_commit");
        try
        {
            Assert.Equal("runtime.owner_unavailable", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private static Process Start(RuntimeFixture files, string boundary)
    {
        Directory.CreateDirectory(files.Folder);
        string config = Path.Combine(files.Folder, "model.json");
        File.WriteAllText(config, RuntimeFixture.Model().ToJsonString());
        // The test runner's shared runtime directory identifies the host even
        // when a project-local SDK is not on PATH.
        string runtimeFolder = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string host = Path.GetFullPath(Path.Combine(runtimeFolder, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "crash-probe", "IndustrialDataSim.CrashProbe.dll"));
        start.ArgumentList.Add(files.Database);
        start.ArgumentList.Add(config);
        start.ArgumentList.Add(boundary);
        return Process.Start(start)!;
    }
}
