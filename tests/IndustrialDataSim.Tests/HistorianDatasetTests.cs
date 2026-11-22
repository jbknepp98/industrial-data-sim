using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

/// <summary>
/// Creating, checking and deleting Datasets, against a fake Historian that
/// answers the way Historian 2.0.0 did when probed on November 22, 2026:
/// POST /api/datasets creates (400 Error.DatasetAlreadyExists for a name any
/// Dataset has, ignoring case), DELETE removes (404 Error.DatasetNotFound when
/// it is gone), GET …/exists answers true or false.
/// </summary>
public class HistorianDatasetTests
{
    private sealed class Historian : HttpMessageHandler
    {
        public readonly Dictionary<string, int> Datasets = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Requests = [];
        public HttpStatusCode? Force;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == "/auth/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"PRIVATE_TOKEN\",\"expires_in\":3600}");
            Requests.Add($"{request.Method} {path}");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (Force is { } status) return Json(status, "{\"message\":\"PRIVATE_SERVER_DETAILS\"}");
            if (request.Method == HttpMethod.Post && path == "/api/datasets")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(stop))!.AsObject();
                string name = body["n"]!.GetValue<string>();
                if (!Datasets.TryAdd(name, body["pa"]!.GetValue<int>()))
                    return Json(HttpStatusCode.BadRequest, $"{{\"key\":\"Error.DatasetAlreadyExists\",\"message\":\"A dataset named '{name}' already exists.\",\"errorCode\":2}}");
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            string dataset = path["/api/datasets/".Length..];
            if (request.Method == HttpMethod.Get && dataset.EndsWith("/exists"))
                return Json(HttpStatusCode.OK, Datasets.Keys.Contains(dataset[..^"/exists".Length], StringComparer.Ordinal) ? "true" : "false");
            if (request.Method == HttpMethod.Delete)
                return Datasets.Remove(dataset)
                    ? new(HttpStatusCode.OK) { Content = new StringContent("") }
                    : Json(HttpStatusCode.NotFound, $"{{\"key\":\"Error.DatasetNotFound\",\"message\":\"The dataset '{dataset}' could not be found.\",\"errorCode\":1}}");
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task CreateMakesTheDatasetWithItsPurgeAgeAndRefusesANameThatIsTaken()
    {
        var server = new Historian();
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        Assert.False(await client.DatasetExists("PlanTestPlant", default));

        await client.CreateDataset("PlanTestPlant", 365, default);
        Assert.Equal(365, server.Datasets["PlanTestPlant"]);
        Assert.True(await client.DatasetExists("PlanTestPlant", default));

        // Taken, in any letter case: refused, and the existing Dataset is untouched.
        foreach (string name in new[] { "PlanTestPlant", "plantestplant" })
        {
            var refused = await Assert.ThrowsAsync<RuntimeFailure>(() => client.CreateDataset(name, 30, default));
            Assert.Equal("historian.dataset_exists", refused.Error.Code);
            Assert.Contains(name, refused.Message);
        }
        Assert.Equal(365, server.Datasets["PlanTestPlant"]);
        Assert.Single(server.Datasets);
    }

    [Fact]
    public async Task DeleteRemovesTheDatasetAndReportsOneThatWasAlreadyGone()
    {
        var server = new Historian();
        server.Datasets["PlanTestPlant"] = 365;
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        Assert.True(await client.DeleteDataset("PlanTestPlant", default));
        Assert.Empty(server.Datasets);
        Assert.False(await client.DeleteDataset("PlanTestPlant", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherFailuresNameTheOperationAndNeverEchoTheServerBody(HttpStatusCode status)
    {
        var server = new Historian { Force = status };
        using var client = new HistorianClient(ProductionDeliveryTests.Profile(), new HttpClient(server));
        var create = await Assert.ThrowsAsync<RuntimeFailure>(() => client.CreateDataset("PlanTestPlant", 365, default));
        var delete = await Assert.ThrowsAsync<RuntimeFailure>(() => client.DeleteDataset("PlanTestPlant", default));
        foreach (var (failure, operation) in new[] { (create, "Dataset create"), (delete, "Dataset delete") })
        {
            Assert.Equal("historian.http_status", failure.Error.Code);
            Assert.Contains(operation, failure.Message);
            Assert.Contains(((int)status).ToString(), failure.Message);
            Assert.DoesNotContain("PRIVATE", failure.Message);
        }
        // A POST is sent once, never retried.
        Assert.Single(server.Requests, r => r.StartsWith("POST"));
    }
}
