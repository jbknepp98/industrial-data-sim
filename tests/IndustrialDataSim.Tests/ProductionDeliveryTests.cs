using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

public class ProductionDeliveryTests
{
    private static HistorianConnection Profile(string audience = "Historian") => new() {
        Profile = "local-profile", Historian = new("https://historian.invalid/"), Pulse = new("https://pulse.invalid/"),
        ClientId = "test-client", ClientSecret = "PRIVATE_SECRET", Audience = audience };

    private sealed class Server : HttpMessageHandler
    {
        public int Writes, Tokens;
        public HttpStatusCode PublishStatus = HttpStatusCode.OK;
        public bool FailPublish, FailReadAfterPublish, Mismatch, Existing, NullCurrent, FlatInventory, SuppressRepeats;
        public readonly Dictionary<string, JsonArray> Stored = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/token") { Tokens++; return Json("{\"access_token\":\"PRIVATE_TOKEN\",\"expires_in\":3600}"); }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(stop))!.AsObject();
                foreach (var (name, data) in payload)
                {
                    if (!SuppressRepeats) Stored[name] = (JsonArray)data!.DeepClone();
                    else
                    {
                        if (!Stored.TryGetValue(name, out var retained)) Stored[name] = retained = new();
                        foreach (var point in data!.AsArray())
                            if (retained.Count == 0 || !JsonNode.DeepEquals(retained[^1]!["v"], point!["v"])) retained.Add(point!.DeepClone());
                    }
                }
                if (FailPublish) throw new HttpRequestException("PRIVATE_TRANSPORT_DETAILS");
                return new(PublishStatus) { Content = new StringContent("") };
            }
            if (path.EndsWith("/exists")) return Json("true");
            if (path.EndsWith("/tags"))
            {
                var inventory = new JsonArray();
                var inventoryNames = Stored.Keys.Concat(Existing ? new[] { "A.0", "A.1", "A.2" } : Array.Empty<string>()).Distinct();
                foreach (string name in inventoryNames) inventory.Add(new JsonObject { ["n"] = name });
                return Json(FlatInventory ? inventory.ToJsonString() : new JsonObject { ["User"] = inventory, ["System"] = new JsonArray() }.ToJsonString());
            }
            if (!path.EndsWith("/data")) return Json("{\"pa\":0,\"ps\":0,\"ldt\":100,\"lda\":30}");
            if (FailReadAfterPublish && Writes > 0) throw new HttpRequestException("PRIVATE_READ_DETAILS");
            var names = request.RequestUri.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("tagname="))
                .Select(p => Uri.UnescapeDataString(p[8..]));
            bool range = request.RequestUri.Query.Contains("start=");
            var tags = new JsonArray();
            foreach (string name in names)
            {
                JsonArray points = Stored.TryGetValue(name, out var saved) ? (JsonArray)saved.DeepClone() : new();
                if (Existing && points.Count == 0) points.Add(JsonNode.Parse("{\"t\":\"2026-09-02T00:00:00Z\",\"v\":1,\"q\":192}"));
                if (!range && points.Count > 0) points = new JsonArray(points[^1]!.DeepClone());
                if (Mismatch && points.Count > 0) points[0]!["v"] = 99999;
                if (NullCurrent && !range && points.Count > 0) points[0]!["v"] = null;
                tags.Add(new JsonObject { ["t"] = new JsonObject { ["n"] = name }, ["d"] = points });
            }
            return Json(new JsonObject { ["tl"] = tags }.ToJsonString());
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyHttpSuccessPublishesWithSeparateObservationsAndReview(bool flatInventory)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server { FlatInventory = flatInventory };
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        var run = await new ProductionWorker(runtime, delivery).RunAsync(10);
        Assert.Equal("Completed", run.StopReason);
        Assert.Equal(1, server.Writes);
        Assert.Equal(1, server.Tokens);
        var batch = Assert.Single(runtime.Batches("session-a"));
        Assert.Equal(BatchStatus.Published, batch.Status);
        Assert.Null(batch.Payload);
        Assert.All(runtime.Progress("session-a"), p => { Assert.Null(p.AcknowledgedTicks); Assert.NotNull(p.PublishedTicks); });
        var observation = Assert.Single(runtime.Observations("session-a"));
        Assert.Equal("Observed", observation.Status);
        Assert.Equal(3, observation.MatchingTags);
        Assert.Equal(0, observation.ChangedTags); // Constants require no artificial change.
        Assert.Equal("NotReviewed", observation.UserReview);
        runtime.RecordUserReview("session-a", true);
        Assert.Equal("Accepted", Assert.Single(runtime.Observations("session-a")).UserReview);
        runtime.Archive("session-a", files.Folder);
        runtime.VerifyArchive("session-a", files.Folder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousWriteOrCrashKeepsPayloadAndNeverReplays(bool networkFailure)
    {
        using var files = new RuntimeFixture();
        var server = new Server { FailPublish = networkFailure };
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        using (var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            var delivery = new ProductionDelivery(runtime, client);
            await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
            runtime.Generate("session-a");
            if (networkFailure) Assert.False(await delivery.DeliverOneAsync("session-a"));
            else
            {
                delivery.FaultPoint = _ => throw new SimulatedCrash();
                await Assert.ThrowsAsync<SimulatedCrash>(() => delivery.DeliverOneAsync("session-a"));
            }
        }
        using var reopened = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        Assert.Equal(SessionStatus.Uncertain, reopened.GetSession("session-a").Status);
        Assert.NotNull(Assert.Single(reopened.Batches("session-a")).Payload);
        Assert.False(await new ProductionDelivery(reopened, client).DeliverOneAsync("session-a"));
        Assert.Equal(1, server.Writes);
        Assert.Equal("runtime.tag_owned", Assert.Throws<RuntimeFailure>(() => reopened.AdmitSession(RuntimeFixture.Model("peer").ToJsonString())).Error.Code);
        Assert.Equal("archive.unresolved", Assert.Throws<RuntimeFailure>(() => reopened.Archive("session-a", files.Folder)).Error.Code);
    }

    [Theory]
    [InlineData("unavailable", "Unavailable")]
    [InlineData("mismatch", "Mismatch")]
    [InlineData("null", "NotYetObserved")]
    public async Task ObservationFailuresDoNotUndoPublishedProgress(string mode, string expected)
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server { FailReadAfterPublish = mode == "unavailable", Mismatch = mode == "mismatch", NullCurrent = mode == "null" };
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        await new ProductionWorker(runtime, delivery).RunAsync(10);
        Assert.Equal(BatchStatus.Published, Assert.Single(runtime.Batches("session-a")).Status);
        var observation = Assert.Single(runtime.Observations("session-a"));
        Assert.Equal(expected, observation.Status);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(observation));
        Assert.Equal(1, server.Writes);
    }

    [Fact]
    public async Task ExistingTimelineAndChangedProfileAreRefusedWithoutWrites()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server { Existing = true };
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client);
        var error = await Assert.ThrowsAsync<RuntimeFailure>(() => delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString()));
        Assert.Equal("historian.backward_range", error.Error.Code);
        Assert.Empty(runtime.Sessions());
        Assert.Equal(0, server.Writes);
        using var other = new HistorianClient(Profile("different"), new HttpClient(new Server()));
        Assert.Equal("connection.identity_changed", Assert.Throws<RuntimeFailure>(() => new ProductionDelivery(runtime, other)).Error.Code);
    }

    [Fact]
    public async Task ExternalConflictBeforeClaimStopsOnlyItsSession()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server();
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client);
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        server.Existing = true;
        Assert.False(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal(SessionStatus.Failed, runtime.GetSession("session-a").Status);
        Assert.Equal(BatchStatus.Pending, Assert.Single(runtime.Batches("session-a")).Status);
        Assert.Equal(0, server.Writes);
    }

    [Fact]
    public async Task HttpErrorIsUncertainAndUsefulWithoutResponseDetails()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server { PublishStatus = HttpStatusCode.Unauthorized };
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client);
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        Assert.False(await delivery.DeliverOneAsync("session-a"));
        var session = runtime.GetSession("session-a");
        Assert.Equal(SessionStatus.Uncertain, session.Status);
        Assert.Contains("HTTP 401", session.ErrorMessage);
        Assert.DoesNotContain("PRIVATE", session.ErrorMessage);
        Assert.Equal(1, server.Writes);
        Assert.Equal("production.cannot_retry_preflight", Assert.Throws<RuntimeFailure>(() => runtime.RetryProductionPreflight("session-a")).Error.Code);
    }

    [Fact]
    public async Task UnsentCheckpointResumesAndTokenRenewsBeforeNextPublish()
    {
        using var files = new RuntimeFixture();
        var server = new Server();
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var now = DateTimeOffset.UtcNow;
        client.UtcNow = () => now;
        using (var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }, mode: ExecutionMode.Production))
        {
            var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
            await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
            await delivery.AdmitAsync(RuntimeFixture.Model("peer", "B").ToJsonString());
            runtime.Generate("session-a"); // Durable unsent work survives reopening.
            Assert.Equal(0, server.Writes);
        }
        now = now.AddHours(1);
        using var reopened = new DurableRuntime(files.Database, new() { BatchPoints = 3 }, mode: ExecutionMode.Production);
        var restored = new ProductionDelivery(reopened, client) { ObservationDelay = TimeSpan.Zero };
        var result = await new ProductionWorker(reopened, restored).RunAsync(20);
        Assert.Equal("Completed", result.StopReason);
        Assert.Equal(6, result.PublishedBatches);
        Assert.Equal(2, server.Tokens);
        Assert.All(reopened.Sessions(), session => Assert.Equal(SessionStatus.Complete, session.Status));
    }

    [Fact]
    public async Task CorrectedReadOnlyPreflightMayBeRetriedWithoutReplayingWrites()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, mode: ExecutionMode.Production);
        var server = new Server();
        using var client = new HistorianClient(Profile(), new HttpClient(server));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        runtime.Generate("session-a");
        server.Existing = true;
        Assert.False(await delivery.DeliverOneAsync("session-a"));
        server.Existing = false;
        runtime.RetryProductionPreflight("session-a");
        Assert.True(await delivery.DeliverOneAsync("session-a"));
        Assert.Equal(1, server.Writes);
    }

    [Fact]
    public async Task SuppressedConstantBatchesReportCompatibilityWithoutClaimingArrival()
    {
        using var files = new RuntimeFixture();
        using var runtime = new DurableRuntime(files.Database, new() { BatchPoints = 3 }, mode: ExecutionMode.Production);
        using var client = new HistorianClient(Profile(), new HttpClient(new Server { SuppressRepeats = true }));
        var delivery = new ProductionDelivery(runtime, client) { ObservationDelay = TimeSpan.Zero };
        await delivery.AdmitAsync(RuntimeFixture.Model().ToJsonString());
        await new ProductionWorker(runtime, delivery).RunAsync(10);
        var observations = runtime.Observations("session-a");
        Assert.Equal(new[] { "Observed", "ConsistentWithoutNewArrival", "ConsistentWithoutNewArrival" }, observations.Select(item => item.Status));
        Assert.Equal(0, observations[1].MatchingTags);
        Assert.Equal(3, observations[1].NonNullCurrentTags);
        Assert.All(observations, item => Assert.Equal("NotReviewed", item.UserReview));
    }

    [Fact]
    public void ModesCannotOpenEachOthersStateOrInvokeFakeDelivery()
    {
        using var files = new RuntimeFixture();
        using (var production = new DurableRuntime(files.Database, mode: ExecutionMode.Production))
        {
            Assert.Equal("runtime.execution_mode", Assert.Throws<RuntimeFailure>(() => new SimulationWorker(production)).Error.Code);
            Assert.Equal("runtime.execution_mode", Assert.Throws<RuntimeFailure>(() => production.AddSession(RuntimeFixture.Model().ToJsonString())).Error.Code);
        }
        Assert.Equal("runtime.execution_mode", Assert.Throws<RuntimeFailure>(() => new DurableRuntime(files.Database)).Error.Code);
    }
}
