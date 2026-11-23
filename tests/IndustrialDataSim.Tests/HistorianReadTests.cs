using System.Net;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Runtime;
using IndustrialDataSim.Web;
using Xunit;

namespace IndustrialDataSim.Tests;

/// <summary>
/// HistorianClient.ReadTagData against a fake Historian that answers in the
/// shape recorded live on Historian 2.0.0 (November 23, 2026): tag name in
/// tl[].t.n, points {t, v, q}, a leading point before the range, and a year-1
/// placeholder with no value for an empty range.
/// </summary>
public class HistorianReadTests
{
    internal sealed class FakeHistorian(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public readonly List<string> Queries = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            if (request.RequestUri!.AbsolutePath == "/auth/token")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"PRIVATE_TOKEN\",\"expires_in\":3600}"));
            Queries.Add(request.RequestUri.PathAndQuery);
            var (status, body) = answer(request.RequestUri.PathAndQuery);
            return Task.FromResult(Json(status, body));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    internal static HistorianClient Client(FakeHistorian server) => new(ProductionDeliveryTests.Profile(), new HttpClient(server));

    private static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    [Fact]
    public async Task TheBoundaryPointBecomesTheValueAtStartAndThePlaceholderIsDropped()
    {
        var server = new FakeHistorian(_ => (HttpStatusCode.OK, """
            {"s":"2026-11-22T10:00:00Z","e":"2026-11-22T11:00:00Z","tl":[
              {"t":{"n":"P.L1.Speed","t":"Double"},"d":[{"t":"2026-11-22T09:58:00Z","v":4.5,"q":192},{"t":"2026-11-22T10:10:00Z","v":5,"q":192}]},
              {"t":{"n":"P.L1.State","t":"String"},"d":[{"t":"0001-01-01T00:00:00Z","q":0}]}
            ]}
            """));
        using var client = Client(server);
        var series = await client.ReadTagData("PlanTestDs", ["P.L1.Speed", "P.L1.State", "P.L1.Missing"], Start, End, default);
        Assert.Equal(["P.L1.Speed", "P.L1.State", "P.L1.Missing"], series.Select(s => s.Tag));
        Assert.Equal([Start, Start.AddMinutes(10)], series[0].Points.Select(p => p.Time));
        Assert.Equal(4.5, series[0].Points[0].Value.GetDouble());
        Assert.Empty(series[1].Points); // placeholder only
        Assert.Empty(series[2].Points); // not in the answer
    }

    [Fact]
    public async Task TheQueryEscapesTagNamesAndDatasetAndSendsTheRange()
    {
        var server = new FakeHistorian(_ => (HttpStatusCode.OK, "{\"tl\":[]}"));
        using var client = Client(server);
        await client.ReadTagData("Plan Test&Ds", ["A tag&x=1", "B"], Start, End, default);
        string query = Assert.Single(server.Queries);
        Assert.StartsWith("/api/datasets/Plan%20Test%26Ds/data?", query);
        Assert.Contains("tagname=A%20tag%26x%3D1", query);
        Assert.Contains("tagname=B", query);
        Assert.Contains("start=2026-11-22T10%3A00%3A00.000Z", query);
        Assert.Contains("end=2026-11-22T11%3A00%3A00.000Z", query);
    }

    [Fact]
    public async Task NotFoundMeansNoPointsYet()
    {
        using var client = Client(new FakeHistorian(_ => (HttpStatusCode.NotFound, "{}")));
        var series = await client.ReadTagData("PlanTestDs", ["A"], Start, End, default);
        Assert.Empty(Assert.Single(series).Points);
    }

    [Fact]
    public async Task ServerErrorsNeverEchoTheBody()
    {
        using var client = Client(new FakeHistorian(_ => (HttpStatusCode.InternalServerError, "{\"message\":\"PRIVATE_SERVER_DETAILS\"}")));
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => client.ReadTagData("PlanTestDs", ["A"], Start, End, default));
        Assert.Equal("historian.http_status", failure.Error.Code);
        Assert.DoesNotContain("PRIVATE", failure.Message);
    }

    [Fact]
    public async Task AnAnswerOver32MegabytesIsRefused()
    {
        string huge = "{\"tl\":[{\"t\":{\"n\":\"A\"},\"d\":[" + string.Join(",", Enumerable.Repeat("{\"t\":\"2026-11-22T10:00:00Z\",\"v\":1.0,\"q\":192}", 33 * 1024 * 1024 / 44 + 1)) + "]}]}";
        using var client = Client(new FakeHistorian(_ => (HttpStatusCode.OK, huge)));
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => client.ReadTagData("PlanTestDs", ["A"], Start, End, default));
        Assert.Equal("historian.read_too_large", failure.Error.Code);
    }

    [Fact]
    public async Task AMalformedAnswerIsReadUnexpected()
    {
        foreach (string body in new[] { "not json", "{\"tl\":5}", "{\"tl\":[{\"d\":[]}]}" })
        {
            using var client = Client(new FakeHistorian(_ => (HttpStatusCode.OK, body)));
            var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => client.ReadTagData("PlanTestDs", ["A"], Start, End, default));
            Assert.Equal("historian.read_unexpected", failure.Error.Code);
        }
    }
}

public class TrendThinningTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    private static TagPoint Point(int seconds, object value) => new(Start.AddSeconds(seconds), JsonSerializer.SerializeToElement(value), 192);

    [Fact]
    public void ManyNumbersBecomeAtMost1000AndKeepTheExtremesInTimeOrder()
    {
        var raw = Enumerable.Range(0, 10_000).Select(i => Point(i * 10, Math.Sin(i / 50.0) * 10)).ToList();
        raw[4321] = Point(43210, 99.0);   // a spike
        raw[7000] = Point(70000, -99.0);  // a dip
        var end = Start.AddSeconds(100_000);
        var (points, thinned) = TrendThinning.Thin(raw, Start, end, "number");
        Assert.True(thinned);
        Assert.InRange(points.Count, 2, TrendThinning.MaxPoints);
        Assert.Contains(points, p => p.Value.GetDouble() == 99.0);
        Assert.Contains(points, p => p.Value.GetDouble() == -99.0);
        Assert.Equal(points.OrderBy(p => p.Time).Select(p => p.Time), points.Select(p => p.Time));
    }

    [Fact]
    public void FewNumbersAreReturnedUnchanged()
    {
        var raw = Enumerable.Range(0, 999).Select(i => Point(i, i)).ToList();
        var (points, thinned) = TrendThinning.Thin(raw, Start, Start.AddSeconds(1000), "number");
        Assert.False(thinned);
        Assert.Same(raw, points);
    }

    [Fact]
    public void TextKeepsOnlyChangesAndALongSeriesIsCapped()
    {
        var states = new[] { "Running", "Running", "Down", "Down", "Running" }.Select((s, i) => Point(i, s)).ToList();
        var (changes, thinned) = TrendThinning.Thin(states, Start, Start.AddSeconds(10), "string");
        Assert.Equal(["\"Running\"", "\"Down\"", "\"Running\""], changes.Select(p => p.Value.GetRawText()));
        Assert.True(thinned);

        var flapping = Enumerable.Range(0, 5000).Select(i => Point(i, i % 2 == 0 ? "A" : "B")).ToList();
        var (capped, cut) = TrendThinning.Thin(flapping, Start, Start.AddSeconds(5000), "string");
        Assert.True(cut);
        Assert.InRange(capped.Count, 1, TrendThinning.MaxPoints);
    }
}
