using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public sealed class RuntimeFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "sim-runtime-" + Guid.NewGuid());
    public string Database => Path.Combine(Folder, "state.db");
    public static JsonObject Model(string id = "session-a", string prefix = "A")
    {
        var model = ConstantSimulationTests.Model();
        model["session"]!["sessionId"] = id;
        for (int i = 0; i < 3; i++)
        {
            string name = prefix + "." + i;
            model["session"]!["outputTags"]![i]!["name"] = name;
            model["generators"]![i]!["tag"] = name;
        }
        return model;
    }
    public void Dispose() { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
}

public class DurableOwnershipTests
{
    [Fact]
    public void SessionsShareDatasetOnlyForDisjointTagsAndPersist()
    {
        using var files = new RuntimeFixture();
        using (var runtime = new DurableRuntime(files.Database))
        {
            runtime.AddSession(RuntimeFixture.Model().ToJsonString());
            runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
            Assert.Equal(2, runtime.Sessions().Count);
        }
        using var reopened = new DurableRuntime(files.Database);
        Assert.Equal(2, reopened.Sessions().Count);
        Assert.All(reopened.Sessions(), s => Assert.Equal(SessionStatus.Ready, s.Status));
    }

    [Fact]
    public void ConflictRollsBackWholeAdmissionIncludingNonconflictingTags()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        var conflict = RuntimeFixture.Model("session-b", "B");
        conflict["session"]!["outputTags"]![2]!["name"] = "a.0";
        conflict["generators"]![2]!["tag"] = "a.0";
        Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(conflict.ToJsonString())).Error.Code);
        Assert.Single(runtime.Sessions());
        runtime.AddSession(RuntimeFixture.Model("session-b", "B").ToJsonString());
        Assert.Equal(2, runtime.Sessions().Count);
    }

    [Fact]
    public void PausedSessionKeepsOwnershipAndCannotRelease()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        runtime.Pause("session-a");
        Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model("other").ToJsonString())).Error.Code);
        Assert.Throws<RuntimeFailure>(() => runtime.ReleaseCompleted("session-a"));
        runtime.Resume("session-a");
        Assert.Equal(SessionStatus.Ready, runtime.GetSession("session-a").Status);
    }

    [Fact]
    public void SecondDatabaseOwnerIsRejectedUntilFirstCloses()
    {
        using var files = new RuntimeFixture();
        using (var first = new DurableRuntime(files.Database))
            Assert.Equal("runtime.owner_unavailable", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
        using var second = new DurableRuntime(files.Database);
        Assert.Empty(second.Sessions());
    }

    [Fact]
    public void ConfigurationCannotBeReplacedUnderExistingId()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        runtime.AddSession(RuntimeFixture.Model().ToJsonString());
        Assert.Equal("runtime.session_exists", Assert.Throws<RuntimeFailure>(() => runtime.AddSession(RuntimeFixture.Model(prefix: "B").ToJsonString())).Error.Code);
    }

    [Fact]
    public async Task SimultaneousConflictingAdmissionsHaveOneWinner()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            try { runtime.AddSession(RuntimeFixture.Model("session-" + i).ToJsonString()); return true; }
            catch (RuntimeFailure error) when (error.Error.Code == "runtime.tag_owned") { return false; }
        })));
        Assert.Single(outcomes, won => won);
        Assert.Single(runtime.Sessions());
    }
}
