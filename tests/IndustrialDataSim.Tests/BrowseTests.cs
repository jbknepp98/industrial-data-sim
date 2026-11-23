using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Runtime;
using IndustrialDataSim.Web;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The page's plant browser (/api/browse): only plants this page built,
/// read-only, bounded. Needs FLOWSIM_TEST_PG; plants are PlanTest names,
/// removed afterwards.
/// </summary>
public partial class WebTests
{
    /// <summary>Approves a scripted interview (no hosting) and fills the plant's tables up to its anchor.</summary>
    private static async Task<PlantModel> ApproveAndFill(Harness web, int extraDays = 0)
    {
        var id = await NewInterview(web.Http);
        await Send(web.Http, id, "A bottler; late orders after labeler jams.");
        var approve = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
        Assert.Equal(HttpStatusCode.Created, approve.StatusCode);
        string name = (await approve.Content.ReadFromJsonAsync<JsonObject>())!["simulation"]!.GetValue<string>();
        // Approval starts the plant's host; stop it so the fill below is the only writer.
        await web.App.Services.GetRequiredService<PlantHostingService>().StopPlant(name);
        var registration = web.Services.Plants.Registration(name)!;
        var model = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc).Model!;
        // Not hosted: the test may run the engine past the anchor to get more rows.
        var through = registration.AnchorUtc.AddDays(extraDays);
        web.Services.Plants.ApplyChanges(model, PlantEngine.Run(model, through).Changes, through);
        return model;
    }

    private static long Scalar(string sql)
    {
        using var db = new NpgsqlConnection(Connection);
        db.Open();
        using var command = new NpgsqlCommand(sql, db);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<(HttpStatusCode Status, string Code, string Message)> Refusal(HttpClient http, string url)
    {
        var response = await http.GetAsync(url);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!;
        return (response.StatusCode, error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheBrowserListsOnlyPlantsThisPageBuilt()
    {
        if (Connection is null) return;
        string other = "PlanTestOther" + Guid.NewGuid().ToString("N")[..8];
        using var store = new PlantStore(Connection);
        try
        {
            NewPlant(store, other); // admitted by another tool, never approved on this page
            await using var web = await Start(() => new ScriptedModel(), Connection);
            Assert.DoesNotContain((await Get(web.Http, "/api/plants"))["plants"]!.AsArray().OfType<JsonObject>(),
                p => p["simulation"]!.GetValue<string>() == other);
            using (store.LockSimulation(other)) // even while another process delivers it
                Assert.DoesNotContain((await Get(web.Http, "/api/plants"))["plants"]!.AsArray().OfType<JsonObject>(),
                    p => p["simulation"]!.GetValue<string>() == other);

            var missing = await Refusal(web.Http, "/api/browse/PlanTestNoSuchPlant");
            foreach (string url in new[] { $"/api/browse/{other}", $"/api/browse/{other}/tables", $"/api/browse/{other}/tables/{other.ToLowerInvariant()}_lines/rows" })
            {
                var refused = await Refusal(web.Http, url);
                Assert.Equal(HttpStatusCode.NotFound, refused.Status);
                Assert.Equal("browse.not_found", refused.Code);
                Assert.Equal(missing.Message, refused.Message); // never says whether the plant exists
            }
        }
        finally { Cleanup(other); }
    }

    [Fact]
    public async Task SummaryAndTablesDescribeThePlantFromItsModelAndPostgres()
    {
        if (Connection is null) return;
        string name = "PlanTestBrowseSum" + Guid.NewGuid().ToString("N")[..6];
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection);
        try
        {
            var model = await ApproveAndFill(web);
            var summary = await Get(web.Http, $"/api/browse/{name}");
            Assert.Equal(name, summary["simulation"]!.GetValue<string>());
            Assert.Equal(model.TablePrefix, summary["tablePrefix"]!.GetValue<string>());
            Assert.Equal("Test", summary["dataset"]!.GetValue<string>()); // no Historian in tests: the shared Dataset
            Assert.Equal(model.Lines.Count, summary["lines"]!.AsArray().Count);
            Assert.Equal(model.Lines[0].Equipment.Count, summary["lines"]![0]!["equipment"]!.AsArray().Count);
            Assert.Equal(PlantSchema.TablesFor(model).Count, summary["counts"]!["tables"]!.GetValue<int>());
            Assert.Equal(new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.Count, summary["counts"]!["tags"]!.GetValue<int>());
            Assert.Equal(model.Incidents.Count, summary["incidents"]!.AsArray().Count);
            if (model.Incidents.Count > 0) Assert.NotNull(summary["incidents"]![0]!["equipment"]);
            Assert.Contains(summary["storylines"]!.AsArray(), s => s!["id"]!.GetValue<string>() == "labeler-jams-late-orders");
            Assert.Equal($"/api/plants/{name}/gage/prompts", summary["downloads"]!["gagePrompts"]!.GetValue<string>());

            var tables = (await web.Http.GetFromJsonAsync<JsonArray>($"/api/browse/{name}/tables"))!;
            Assert.Equal(PlantSchema.TablesFor(model).Count, tables.Count);
            long total = 0;
            foreach (var table in tables.OfType<JsonObject>())
            {
                string physical = table["name"]!.GetValue<string>();
                Assert.StartsWith(model.TablePrefix, physical);
                Assert.Equal(Scalar($"SELECT count(*) FROM \"{physical}\""), table["rows"]!.GetValue<long>());
                Assert.Contains(table["columns"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "row_version");
                total += table["rows"]!.GetValue<long>();
            }
            Assert.True(total > 0, "the engine run filled the tables");
        }
        finally { Cleanup(name); }
    }

    [Fact]
    public async Task RowsPageNewestFirstAndSortSearchDeletedAndRefusalsWork()
    {
        if (Connection is null) return;
        string name = "PlanTestBrowseRows" + Guid.NewGuid().ToString("N")[..6];
        string other = "PlanTestOther" + Guid.NewGuid().ToString("N")[..8];
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection);
        try
        {
            var model = await ApproveAndFill(web, extraDays: 21);
            NewPlant(web.Services.Plants, other);
            // The plant's largest table, so there is more than one page.
            var largest = PlantSchema.TablesFor(model)
                .Select(t => (Table: t, Name: PlantSchema.PhysicalName(model, t.Purpose)))
                .OrderByDescending(t => Scalar($"SELECT count(*) FROM \"{t.Name}\""))
                .First(t => t.Table.Columns.Any(c => c.Type == "timestamptz"));
            string tickets = largest.Name;
            string timeName = largest.Table.Columns.First(c => c.Type == "timestamptz").Name;
            string keyName = largest.Table.Key;
            var textNames = largest.Table.Columns.Where(c => c.Type == "text").Select(c => c.Name).ToList();
            string url = $"/api/browse/{name}/tables/{tickets}/rows";
            long total = Scalar($"SELECT count(*) FROM \"{tickets}\" WHERE NOT is_deleted");
            Assert.True(total > PlantBrowser.PageSize, $"need more than one page of {tickets}, have {total}");

            // Default: newest first by row_version, 200 rows.
            var first = await Get(web.Http, url);
            Assert.Equal(total, first["total"]!.GetValue<long>());
            int versionColumn = first["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList().IndexOf("row_version");
            var versions = first["rows"]!.AsArray().Select(r => r![versionColumn]!.GetValue<long>()).ToList();
            Assert.Equal(PlantBrowser.PageSize, versions.Count);
            Assert.Equal(versions.OrderByDescending(v => v), versions);

            // Page 2 continues page 1 without overlap.
            var second = await Get(web.Http, url + "?page=2");
            var secondVersions = second["rows"]!.AsArray().Select(r => r![versionColumn]!.GetValue<long>()).ToList();
            Assert.Empty(versions.Intersect(secondVersions));
            Assert.True(secondVersions.Max() < versions.Min());

            // Sort ascending and descending on a timestamp.
            var ascending = await Get(web.Http, url + $"?sort={timeName}&dir=asc");
            int timeColumn = ascending["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList().IndexOf(timeName);
            var times = ascending["rows"]!.AsArray().Select(r => r![timeColumn]?.GetValue<DateTimeOffset>()).OfType<DateTimeOffset>().ToList();
            Assert.Equal(times.OrderBy(t => t), times);
            var descending = await Get(web.Http, url + $"?sort={timeName}&dir=desc");
            Assert.True(descending["rows"]![0]![timeColumn]!.GetValue<DateTimeOffset>() >= times[^1]); // NULLS LAST both ways

            // Search matches text literally: a ticket id finds its rows; % and _ are not wildcards.
            string ticket = first["rows"]![0]![first["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList().IndexOf(keyName)]!.GetValue<string>();
            var found = await Get(web.Http, url + "?q=" + Uri.EscapeDataString(ticket));
            string like = string.Join(" OR ", textNames.Select(c => $"\"{c}\" ILIKE '%' || @t || '%'"));
            using (var db = new NpgsqlConnection(Connection))
            {
                db.Open();
                using var expected = new NpgsqlCommand($"SELECT count(*) FROM \"{tickets}\" WHERE NOT is_deleted AND ({like})", db);
                expected.Parameters.AddWithValue("t", ticket);
                Assert.Equal(Convert.ToInt64(expected.ExecuteScalar()), found["total"]!.GetValue<long>());
            }
            Assert.True(found["total"]!.GetValue<long>() >= 1);
            Assert.Equal(0, (await Get(web.Http, url + "?q=%25"))["total"]!.GetValue<long>());
            Assert.Equal(0, (await Get(web.Http, url + "?q=_"))["total"]!.GetValue<long>());

            // A soft-deleted row shows only with deleted=true.
            using (var db = new NpgsqlConnection(Connection))
            {
                db.Open();
                using var mark = new NpgsqlCommand($"UPDATE \"{tickets}\" SET is_deleted = true, deleted_utc = now() WHERE \"{keyName}\" = (SELECT min(\"{keyName}\") FROM \"{tickets}\")", db);
                Assert.Equal(1, mark.ExecuteNonQuery());
            }
            Assert.Equal(total - 1, (await Get(web.Http, url))["total"]!.GetValue<long>());
            Assert.Equal(total, (await Get(web.Http, url + "?deleted=true"))["total"]!.GetValue<long>());

            // Refusals.
            var otherTable = await Refusal(web.Http, $"/api/browse/{name}/tables/{other.ToLowerInvariant()}_lines/rows");
            Assert.Equal((HttpStatusCode.NotFound, "browse.unknown_table"), (otherTable.Status, otherTable.Code));
            var sleep = await Refusal(web.Http, url + "?sort=pg_sleep");
            Assert.Equal((HttpStatusCode.BadRequest, "browse.bad_sort"), (sleep.Status, sleep.Code));
            Assert.Contains("row_version", sleep.Message);
            Assert.Equal("browse.bad_sort", (await Refusal(web.Http, url + "?dir=sideways")).Code);
            Assert.Equal("browse.bad_search", (await Refusal(web.Http, url + "?q=" + new string('x', 101))).Code);
            Assert.Equal("browse.bad_page", (await Refusal(web.Http, url + $"?page={PlantBrowser.MaxPage + 1}")).Code);
            Assert.Equal("browse.bad_page", (await Refusal(web.Http, url + "?page=0")).Code);
            Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync(url + $"?page={PlantBrowser.MaxPage}")).StatusCode);
        }
        finally { Cleanup(name); Cleanup(other); }
    }

    [Fact]
    public void ReadOnlyQueryCannotWrite()
    {
        if (Connection is null) return;
        string name = "PlanTestReadOnly" + Guid.NewGuid().ToString("N")[..8];
        using var store = new PlantStore(Connection);
        try
        {
            NewPlant(store, name);
            string lines = name.ToLowerInvariant() + "_lines";
            store.ApplyChanges(PlantModelLoader.Load(store.Registration(name)!.ModelJson, store.Registration(name)!.AnchorUtc).Model!,
                PlantEngine.Run(PlantModelLoader.Load(store.Registration(name)!.ModelJson, store.Registration(name)!.AnchorUtc).Model!, store.Registration(name)!.AnchorUtc).Changes,
                store.Registration(name)!.AnchorUtc);
            long before = Scalar($"SELECT count(*) FROM \"{lines}\"");
            Assert.True(before > 0);
            var error = Assert.Throws<PostgresException>(() => store.ReadOnlyQuery($"DELETE FROM \"{lines}\"", [], 1));
            Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.SqlState);
            Assert.Equal(before, Scalar($"SELECT count(*) FROM \"{lines}\""));
        }
        finally { Cleanup(name); }
    }

    [Fact]
    public async Task TrendsReadThePlantsOwnTagsWithinItsRunAndRefuseEverythingElse()
    {
        if (Connection is null) return;
        string name = "PlanTestBrowseTrend" + Guid.NewGuid().ToString("N")[..6];
        string other = "PlanTestOther" + Guid.NewGuid().ToString("N")[..8];
        var server = new HistorianReadTests.FakeHistorian(query =>
        {
            // Every requested tag gets 5,000 one-second points.
            var names = query.Split('&').Where(p => p.Contains("tagname=")).Select(p => Uri.UnescapeDataString(p[(p.IndexOf("tagname=") + 8)..]));
            var start = DateTimeOffset.Parse(Uri.UnescapeDataString(query.Split("start=")[1].Split('&')[0]));
            string Points(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"t\":\"{start.AddSeconds(i):yyyy-MM-ddTHH:mm:ssZ}\",\"v\":{i % 97},\"q\":192}}"));
            return (HttpStatusCode.OK, "{\"tl\":[" + string.Join(",", names.Select(n => $"{{\"t\":{{\"n\":\"{n}\"}},\"d\":[{Points(5000)}]}}")) + "]}");
        });
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection, historianReader: () => HistorianReadTests.Client(server));
        try
        {
            var id = await NewInterview(web.Http);
            await Send(web.Http, id, "A bottler; late orders after labeler jams.");
            Assert.Equal(HttpStatusCode.Created, (await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))).StatusCode);
            await web.App.Services.GetRequiredService<PlantHostingService>().StopPlant(name);
            var registration = web.Services.Plants.Registration(name)!;
            var model = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc).Model!;
            var tags = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags;

            // The tag tree groups line tags and machine tags.
            var series = await Get(web.Http, $"/api/browse/{name}/series");
            Assert.Equal("Test", series["dataset"]!.GetValue<string>());
            var line = series["lines"]![0]!;
            Assert.Contains(line["tags"]!.AsArray(), t => t!["name"]!.GetValue<string>().EndsWith(".Speed"));
            var machine = line["equipment"]!.AsArray().First()!;
            Assert.All(machine["tags"]!.AsArray(), t => Assert.Contains("." + machine["id"]!.GetValue<string>() + ".", t!["name"]!.GetValue<string>()));
            Assert.Equal(tags.Count, series["lines"]!.AsArray().Sum(l => l!["tags"]!.AsArray().Count + l["equipment"]!.AsArray().Sum(e => e!["tags"]!.AsArray().Count)) + series["plantTags"]!.AsArray().Count);

            string speed = tags.First(t => t.Name.EndsWith(".Speed")).Name, state = tags.First(t => t.Name.EndsWith(".State")).Name;
            var from = registration.StartUtc.AddHours(1);
            string Range(DateTimeOffset a, DateTimeOffset b) => $"&start={Uri.EscapeDataString(a.ToString("O"))}&end={Uri.EscapeDataString(b.ToString("O"))}";
            string url = $"/api/browse/{name}/trend?tag={Uri.EscapeDataString(speed)}&tag={Uri.EscapeDataString(state)}" + Range(from, from.AddHours(2));
            var trend = await Get(web.Http, url);
            var speedSeries = trend["tags"]![0]!;
            Assert.Equal(speed, speedSeries["name"]!.GetValue<string>());
            Assert.Equal(5000, speedSeries["rawPoints"]!.GetValue<int>());
            Assert.True(speedSeries["thinned"]!.GetValue<bool>());
            Assert.InRange(speedSeries["points"]!.AsArray().Count, 1, TrendThinning.MaxPoints);
            Assert.Equal("string", trend["tags"]![1]!["valueType"]!.GetValue<string>());

            async Task Refused(string query, HttpStatusCode status, string code)
            {
                var refused = await Refusal(web.Http, $"/api/browse/{name}/trend?{query}");
                Assert.Equal((status, code), (refused.Status, refused.Code));
            }
            string t1 = "tag=" + Uri.EscapeDataString(speed);
            await Refused($"tag={Uri.EscapeDataString(other + ".L1.Speed")}" + Range(from, from.AddHours(1)), HttpStatusCode.BadRequest, "browse.bad_tags");
            await Refused(string.Join("&", tags.Take(5).Select(t => "tag=" + Uri.EscapeDataString(t.Name))) + Range(from, from.AddHours(1)), HttpStatusCode.BadRequest, "browse.bad_tags");
            await Refused(Range(from, from.AddHours(1))[1..], HttpStatusCode.BadRequest, "browse.bad_tags");
            await Refused(t1 + Range(from, from.AddDays(32)), HttpStatusCode.BadRequest, "browse.bad_range");
            await Refused(t1 + Range(registration.StartUtc.AddMinutes(-1), from), HttpStatusCode.BadRequest, "browse.bad_range");
            await Refused(t1 + Range(from, from), HttpStatusCode.BadRequest, "browse.bad_range");
            await Refused(t1 + Range(DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddHours(1)), HttpStatusCode.BadRequest, "browse.bad_range");
            Assert.All(server.Queries, q => Assert.StartsWith("/api/datasets/Test/data?", q)); // only the plant's Dataset was read
            Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/api/browse/{other}/trend?{t1}" + Range(from, from.AddHours(1)))).StatusCode);
        }
        finally { Cleanup(name); }

        // Without a Historian connection the page says so.
        string plain = "PlanTestBrowseNoH" + Guid.NewGuid().ToString("N")[..6];
        await using var noHistorian = await Start(() => new ScriptedModel(InterviewToPreview(plain)), Connection);
        try
        {
            var id = await NewInterview(noHistorian.Http);
            await Send(noHistorian.Http, id, "A bottler; late orders after labeler jams.");
            Assert.Equal(HttpStatusCode.Created, (await noHistorian.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))).StatusCode);
            // Stop the host approval started, so cleanup never drops tables it is writing.
            await noHistorian.App.Services.GetRequiredService<PlantHostingService>().StopPlant(plain);
            var registration = noHistorian.Services.Plants.Registration(plain)!;
            var model = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc).Model!;
            string speed = new PlantSignals(new PlantRun(model, model.StartUtc, model.StartUtc, [], [], [])).Tags.First(t => t.Name.EndsWith(".Speed")).Name;
            var refused = await Refusal(noHistorian.Http, $"/api/browse/{plain}/trend?tag={Uri.EscapeDataString(speed)}&start={Uri.EscapeDataString(registration.StartUtc.AddHours(1).ToString("O"))}&end={Uri.EscapeDataString(registration.StartUtc.AddHours(2).ToString("O"))}");
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "browse.historian_unavailable"), (refused.Status, refused.Code));
        }
        finally { Cleanup(plain); }
    }
}
