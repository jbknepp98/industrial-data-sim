using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;
using IndustrialDataSim.Web;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The web container (phase 7): the endpoints over a real Kestrel on a
/// loopback port with a scripted model, the plant hosting service, and the
/// demo guide. Tests that admit plants need FLOWSIM_TEST_PG and use
/// simulation names starting with PlanTest, removed afterwards.
/// </summary>
public partial class WebTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string StorylineFolder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "storylines");
    private static string? Connection => Environment.GetEnvironmentVariable("FLOWSIM_TEST_PG");
    /// <summary>No database listens here: endpoint tests that must not need Postgres prove they never touch it.</summary>
    private const string NoDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

    private static InterviewLibrary Library()
    {
        var (library, errors) = InterviewLibrary.Load(StorylineFolder);
        Assert.True(library is not null, JsonSerializer.Serialize(errors));
        return library!;
    }

    // ---------------- scripted model ----------------

    private sealed class ScriptedModel(params Func<GageMessageRequest, Task<Message>>[] steps) : IInterviewModel
    {
        private int next;
        public Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop)
        {
            int index = Interlocked.Increment(ref next) - 1;
            if (index >= steps.Length) throw new InvalidOperationException("The script has no more replies.");
            return steps[index](request);
        }
    }

    private static Task<Message> Reply(string stopReason, params JsonObject[] blocks) => Task.FromResult(JsonSerializer.Deserialize<Message>(new JsonObject
    {
        ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "anthropic/claude",
        ["content"] = new JsonArray(blocks), ["stop_reason"] = stopReason, ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 100 }
    }.ToJsonString())!);

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static int toolCounter;
    private static JsonObject Call(string name, object input) => new()
    {
        ["type"] = "tool_use", ["id"] = "toolu_web_" + Interlocked.Increment(ref toolCounter), ["name"] = name, ["input"] = JsonSerializer.SerializeToNode(input)
    };

    private static readonly object Audience = new
    {
        industry = "food-beverage", product = "soft drinks on two bottling lines", roles = new[] { "maintenance", "plant-manager" },
        kpis = new[] { "on-time delivery", "MTTR" }, pains = new[] { "late orders after labeler jams" }, systems = new[] { "SAP PM", "Ignition" },
        constraints = Array.Empty<string>(), historyDays = 2, requestedStories = Array.Empty<string>()
    };

    /// <summary>A whole interview in one turn: profile, ranking (with an unmet ask), compose, preview.</summary>
    private static Func<GageMessageRequest, Task<Message>>[] InterviewToPreview(string simulation, string backfill = "6d") =>
    [
        _ => Reply("tool_use", Text("Noted."), Call(InterviewTools.RecordAudience, Audience)),
        _ => Reply("tool_use", Call(InterviewTools.FindStorylines, new
        {
            keywords = new[] { "late orders" },
            askedFor = new[] { new { storyline = "mcc-trip-impact", words = "MCC trip impact" } }
        })),
        _ => Reply("tool_use", Call(InterviewTools.ComposeModel, new
        {
            storylines = new[] { "labeler-jams-late-orders", "repeat-bearing-failure" }, simulation, backfill,
            lineNames = Array.Empty<object>(), productNames = Array.Empty<object>(), peopleNames = Array.Empty<object>()
        })),
        _ => Reply("tool_use", Call(InterviewTools.PreviewPlant, new { hours = 4 })),
        _ => Reply("end_turn", Text("Here is the preview. Click Review plan, then Approve plan when it looks right."))
    ];

    // ---------------- harness ----------------

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required WebServices Services { get; init; }
        public required StringWriter LogText { get; init; }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> Start(Func<IInterviewModel> model, string connection = NoDatabase, bool hostPlants = false,
        IInterviewStore? interviews = null, IWebPlantRegistry? registry = null, Func<CancellationToken, Task<GageCheckResult>>? gageCheck = null,
        IPlantDatasets? datasets = null, Func<HistorianClient>? historianReader = null)
    {
        var log = new StringWriter();
        var services = new WebServices
        {
            Library = Library(),
            Interviews = interviews ?? new InMemoryInterviewStore(),
            Plants = new PlantStore(connection),
            Registry = registry ?? new InMemoryWebPlantRegistry(),
            Model = model,
            Gage = new GageHealth(gageCheck ?? (_ => Task.FromResult(new GageCheckResult("anthropic/claude", "test", "ok", 1, 1, ["anthropic/claude"]))), "anthropic/claude"),
            Historian = _ => null,
            Datasets = datasets,
            HistorianReader = historianReader,
            Dataset = "Test",
            Log = new JsonLog(TextWriter.Synchronized(log)),
            DemoGuideTemplate = File.ReadAllText(Path.Combine(StorylineFolder, "demo-guide.md")),
            HostingOptions = new PlantHostingOptions { FirstBackoff = TimeSpan.FromMilliseconds(200), ElsewhereRecheck = TimeSpan.FromMilliseconds(300) }
        };
        var app = WebApp.Build(new WebOptions { Urls = "http://127.0.0.1:0", CheckGageAtStartup = false, HostPlants = hostPlants }, services);
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(3) };
        return new Harness { App = app, Http = http, Services = services, LogText = log };
    }

    private static async Task<Guid> NewInterview(HttpClient http)
    {
        var response = await http.PostAsync("/api/interviews", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    private static HttpContent JsonBody(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    /// <summary>Sends a message and reads the whole event stream: (event, data) pairs.</summary>
    private static async Task<List<(string Event, JsonObject Data)>> Send(HttpClient http, Guid id, string text)
    {
        var response = await http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
        {
            var lines = block.Split('\n');
            return (lines[0]["event: ".Length..], JsonNode.Parse(lines[1]["data: ".Length..])!.AsObject());
        }).ToList();
    }

    private static async Task<JsonObject> Get(HttpClient http, string url) => (await http.GetFromJsonAsync<JsonObject>(url))!;

    private static async Task<string> ErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>();

    private static async Task WaitFor(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail("Timed out waiting for " + what);
    }

    // ---------------- endpoints ----------------

    [Fact]
    public async Task FilesAreUploadedThenSentWithAMessageAndShownInTheChat()
    {
        GageMessageRequest? seen = null;
        await using var web = await Start(() => new ScriptedModel(r => { seen = r; return Reply("end_turn", Text("Thanks, I read the equipment list.")); }));
        var id = await NewInterview(web.Http);

        var upload = await web.Http.PostAsync($"/api/interviews/{id}/attachments", JsonBody(new { name = "lines.xlsx", data = Convert.ToBase64String(AttachmentTests.Workbook()) }));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var file = (await upload.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("text", file["kind"]!.GetValue<string>());

        // Refused: a type it cannot read, a renamed file, a file over the limit.
        var exe = await web.Http.PostAsync($"/api/interviews/{id}/attachments", JsonBody(new { name = "tool.exe", data = Convert.ToBase64String([0x4D, 0x5A]) }));
        Assert.Equal((HttpStatusCode.BadRequest, "interview.attachment_type"), (exe.StatusCode, await ErrorCode(exe)));
        var fake = await web.Http.PostAsync($"/api/interviews/{id}/attachments", JsonBody(new { name = "plan.pdf", data = Convert.ToBase64String("hello"u8.ToArray()) }));
        Assert.Equal((HttpStatusCode.BadRequest, "interview.attachment_unreadable"), (fake.StatusCode, await ErrorCode(fake)));
        var big = await web.Http.PostAsync($"/api/interviews/{id}/attachments", JsonBody(new { name = "big.txt", data = Convert.ToBase64String(new byte[AttachmentReader.MaxFileBytes + 1]) }));
        Assert.Equal("interview.attachment_too_large", await ErrorCode(big));
        // An ordinary request still has the small limit.
        var long_ = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = new string('x', 70_000) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, long_.StatusCode);

        var response = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = "Here is the line list.", attachments = new[] { file["id"]!.GetValue<string>() } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var sent = JsonNode.Parse(JsonSerializer.Serialize(seen!.Messages[0]))!["content"]!.AsArray();
        Assert.Contains(sent, b => b!["type"]!.GetValue<string>() == "document" && b["title"]!.GetValue<string>() == "lines.xlsx");

        var view = await Get(web.Http, $"/api/interviews/{id}");
        var mine = view["messages"]![0]!;
        Assert.Equal("Here is the line list.", mine["text"]!.GetValue<string>());
        Assert.Equal("lines.xlsx", mine["attachments"]![0]!["name"]!.GetValue<string>());

        var unknown = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = "", attachments = new[] { Guid.NewGuid().ToString() } }));
        Assert.Equal((HttpStatusCode.BadRequest, "interview.attachment_not_found"), (unknown.StatusCode, await ErrorCode(unknown)));
    }

    [Fact]
    public async Task AnInterviewRunsThroughToAPreviewOverTheEventStream()
    {
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview("PlanTestWebPreview")));
        var id = await NewInterview(web.Http);
        var events = await Send(web.Http, id, "A bottler; late orders after labeler jams. They also asked about MCC trip impact.");
        foreach (var (name, data) in events) output.WriteLine($"{name}: {data.ToJsonString()}");
        foreach (var m in web.Services.Interviews.Load(id)!.Messages.Where(m => m.ContentJson.Contains("tool_result"))) output.WriteLine(m.ContentJson[..Math.Min(600, m.ContentJson.Length)]);

        Assert.Contains(events, e => e.Event == "tool_activity");
        Assert.Contains(events, e => e.Event == "preview_ready");
        Assert.Equal("Here is the preview. Click Review plan, then Approve plan when it looks right.", events.Last(e => e.Event == "assistant_text").Data["text"]!.GetValue<string>());
        Assert.Equal("completed", events.Last().Data["outcome"]!.GetValue<string>());

        var view = await Get(web.Http, $"/api/interviews/{id}");
        Assert.Equal("PlanTestWebPreview", view["preview"]!["simulation"]!.GetValue<string>());
        Assert.NotNull(view["preview"]!["segments"]![0]!["line"]); // camelCase, as the shared renderer reads it
        var approval = view["approval"]!;
        Assert.True(approval["valid"]!.GetValue<bool>());
        Assert.Equal("Test", approval["dataset"]!.GetValue<string>());
        Assert.Equal("6d", approval["backfill"]!.GetValue<string>());
        Assert.Contains("plantestwebpreview_maintenancetickets", approval["tables"]!.AsArray().Select(t => t!.GetValue<string>()));
        var cards = view["storylines"]!.AsArray();
        Assert.Equal(["chosen", "chosen", "requested"], cards.Select(c => c!["kind"]!.GetValue<string>()));
        Assert.Equal("later", cards[2]!["engine"]!.GetValue<string>());
        var messages = view["messages"]!.AsArray();
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>()); // session facts are not shown
        Assert.DoesNotContain(messages, m => m!["text"]!.GetValue<string>().StartsWith("[Session facts"));
        Assert.Contains(messages, m => m!["role"]!.GetValue<string>() == "tool");
    }

    [Fact]
    public async Task ASecondMessageWhileTheFirstIsRunningGets409()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        await using var web = await Start(() => new ScriptedModel(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return await Reply("end_turn", Text("Done."));
        }));
        var id = await NewInterview(web.Http);
        var first = web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = "first" }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = "second" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("interview.busy", await ErrorCode(second));
        Assert.True((await Get(web.Http, $"/api/interviews/{id}"))["busy"]!.GetValue<bool>());
        var approve = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
        Assert.Equal("interview.busy", await ErrorCode(approve));

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        await (await first).Content.ReadAsStringAsync();
        Assert.False((await Get(web.Http, $"/api/interviews/{id}"))["busy"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ApproveWithoutConfirmIsRefusedWith400()
    {
        await using var web = await Start(() => new ScriptedModel());
        var id = await NewInterview(web.Http);
        foreach (var body in new object[] { new { }, new { confirm = false }, new { confirm = "yes" } })
        {
            var response = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("approve.confirm_required", await ErrorCode(response));
        }
        // Confirmed, but there is no preview yet.
        var early = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
        Assert.Equal("approve.no_preview", await ErrorCode(early));
    }

    [Fact]
    public async Task RemoveStopAndStartRefusePlantsThisContainerDidNotAdmit()
    {
        await using var web = await Start(() => new ScriptedModel());
        var unconfirmed = await web.Http.DeleteAsync("/api/plants/BluebirdBev");
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("remove.confirm_required", await ErrorCode(unconfirmed));
        var remove = await web.Http.DeleteAsync("/api/plants/BluebirdBev?confirm=true");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Equal("plant.not_owned", await ErrorCode(remove));
        Assert.Equal("plant.not_owned", await ErrorCode(await web.Http.PostAsync("/api/plants/BluebirdBev/stop", null)));
        Assert.Equal("plant.not_owned", await ErrorCode(await web.Http.PostAsync("/api/plants/BluebirdBev/start", null)));
    }

    [Fact]
    public async Task RequestsFromOtherOriginsHostsOrWithoutJsonAreRefused()
    {
        await using var web = await Start(() => new ScriptedModel());
        var id = await NewInterview(web.Http);

        var crossSite = new HttpRequestMessage(HttpMethod.Post, $"/api/interviews/{id}/approve") { Content = JsonBody(new { confirm = true }) };
        crossSite.Headers.Add("Origin", "http://evil.example");
        var refused = await web.Http.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("web.cross_origin", await ErrorCode(refused));

        var sameSite = new HttpRequestMessage(HttpMethod.Post, $"/api/interviews/{id}/approve") { Content = JsonBody(new { confirm = false }) };
        sameSite.Headers.Add("Origin", web.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("approve.confirm_required", await ErrorCode(await web.Http.SendAsync(sameSite)));

        var form = await web.Http.PostAsync($"/api/interviews/{id}/approve", new StringContent("confirm=true", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        rebinding.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await web.Http.SendAsync(rebinding)).StatusCode);

        var huge = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = new string('x', 70_000) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Fact]
    public async Task AGageStopExplainsTheFixAndAdmitsNothing()
    {
        const string fix = "Pulse refused the message: the client's Gage usage policy is Block. Change it in Pulse → Gage → Usage policies.";
        await using var web = await Start(() => new ScriptedModel(_ => throw new RuntimeFailure("gage.blocked", fix)),
            gageCheck: _ => throw new RuntimeFailure("gage.blocked", fix));
        var id = await NewInterview(web.Http);
        var events = await Send(web.Http, id, "A bottler.");
        var stopped = events.Single(e => e.Event == "stopped");
        Assert.Equal("gage.blocked", stopped.Data["code"]!.GetValue<string>());
        Assert.Equal(fix, stopped.Data["text"]!.GetValue<string>());

        var health = await Get(web.Http, "/api/health");
        Assert.False(health["gage"]!["ok"]!.GetValue<bool>());
        Assert.Equal("gage.blocked", health["gage"]!["code"]!.GetValue<string>());
        Assert.Equal("gage.blocked", (await Get(web.Http, "/api/health?refresh=true"))["gage"]!["code"]!.GetValue<string>());
        Assert.Empty(web.Services.Interviews.ApprovedSimulations());
        Assert.Null((await Get(web.Http, $"/api/interviews/{id}"))["approval"]);
    }

    [Fact]
    public async Task ThePageAndTheSharedRendererAreServed()
    {
        await using var web = await Start(() => new ScriptedModel());
        string page = await web.Http.GetStringAsync("/");
        Assert.Contains("shared/plant-preview.js", page);
        Assert.DoesNotContain("https://", page); // no CDN: it works air-gapped
        Assert.Contains("PlantPreviewRenderer", await web.Http.GetStringAsync("/shared/plant-preview.js"));
        Assert.Contains(".pp", await web.Http.GetStringAsync("/shared/plant-preview.css"));
        Assert.Contains("/api/interviews", await web.Http.GetStringAsync("/app.js"));
        // Named like the other Timebase apps: "TIMEBASE" with the product under it.
        Assert.Contains("<title>Timebase Plant Simulator</title>", page);
        Assert.Contains("<span class=\"product-sub\">Plant Simulator</span>", page);
        Assert.DoesNotContain("Data Simulator", page);
        // The Timebase look: its fonts and icon are served by the page itself, never fetched from the internet.
        string styles = await web.Http.GetStringAsync("/styles.css");
        Assert.DoesNotContain("https://", styles);
        foreach (string font in new[] { "poppins-latin-400-normal", "poppins-latin-500-normal", "poppins-latin-600-normal", "jetbrains-mono-latin-400-normal", "jetbrains-mono-latin-500-normal" })
        {
            Assert.Contains($"fonts/{font}.woff2", styles);
            var response = await web.Http.GetAsync($"/fonts/{font}.woff2");
            Assert.True(response.IsSuccessStatusCode, $"/fonts/{font}.woff2 answered {(int)response.StatusCode}");
            Assert.Equal("font/woff2", response.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal("image/png", (await web.Http.GetAsync("/favicon.png")).Content.Headers.ContentType?.MediaType);
        // After an upgrade the browser must ask again instead of running a cached script.
        foreach (string path in new[] { "/", "/app.js", "/shared/plant-preview.js" })
            Assert.Equal("no-cache", (await web.Http.GetAsync(path)).Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("http://0.0.0.0:8480", "http://127.0.0.1:8480/api/health")]
    [InlineData("http://+:9000;http://127.0.0.1:9001", "http://127.0.0.1:9000/api/health")]
    [InlineData("http://[::]:8480", "http://127.0.0.1:8480/api/health")]
    [InlineData("http://127.0.0.1:8480", "http://127.0.0.1:8480/api/health")]
    [InlineData(null, "http://127.0.0.1:8480/api/health")]
    public void TheHealthProbeAsksTheLoopbackAddressOfTheFirstListenUrl(string? urls, string expected) =>
        Assert.Equal(expected, WebHealthProbe.HealthUrl(urls).ToString());

    [Fact]
    public async Task TheHealthProbeSucceedsAgainstARunningPageAndFailsWhenNothingListens()
    {
        await using var web = await Start(() => new ScriptedModel());
        var error = new StringWriter();
        Assert.Equal(0, await WebHealthProbe.Run(web.Http.BaseAddress!.ToString().TrimEnd('/'), error));
        Assert.Equal("", error.ToString());

        using var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        int port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        Assert.Equal(1, await WebHealthProbe.Run($"http://127.0.0.1:{port}", error));
        Assert.Contains("did not answer", error.ToString());
    }

    // ---------------- approve and host (live Postgres) ----------------

    [Fact]
    public async Task ApproveAdmitsAndStartsThePlantThenRemoveDropsOnlyItsTables()
    {
        if (Connection is null) return; // needs FLOWSIM_TEST_PG
        string name = "PlanTestWeb" + Guid.NewGuid().ToString("N")[..8];
        using var registry = new PostgresWebPlantRegistry(Connection);
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection, hostPlants: true, registry: registry);
        try
        {
            var id = await NewInterview(web.Http);
            await Send(web.Http, id, "A bottler; late orders after labeler jams.");
            var approve = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
            Assert.Equal(HttpStatusCode.Created, approve.StatusCode);
            var approved = (await approve.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(name.ToLowerInvariant() + "_", approved["tablePrefix"]!.GetValue<string>());
            Assert.Equal(name, approved["tagRoot"]!.GetValue<string>());
            Assert.Equal("approve.unchanged", await ErrorCode(await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))));

            JsonObject? plant = null;
            await WaitFor(async () =>
            {
                plant = (await Get(web.Http, "/api/plants"))["plants"]!.AsArray().OfType<JsonObject>().FirstOrDefault(p => p["simulation"]!.GetValue<string>() == name);
                return plant?["hosting"]!["state"]!.GetValue<string>() == "live";
            }, TimeSpan.FromMinutes(2), "the approved plant to go live");
            Assert.True(plant!["managed"]!.GetValue<bool>());
            Assert.True(plant["appliedSequence"]!.GetValue<long>() > 0);

            // Downloads, with the real tag root and times.
            string guide = await web.Http.GetStringAsync($"/api/plants/{name}/demo-guide");
            output.WriteLine(guide);
            Assert.DoesNotMatch(@"\{[A-Za-z]+\}", guide);
            Assert.Contains($"`{name}.L1.LB-202.Vibration`", guide);
            Assert.Contains("MCC trip impact", guide); // the unmet ask from the interview
            Assert.Contains("<h1>", await web.Http.GetStringAsync($"/api/plants/{name}/demo-guide?format=html"));
            Assert.Contains(name.ToLowerInvariant() + "_maintenancetickets", await web.Http.GetStringAsync($"/api/plants/{name}/connect-spec"));
            Assert.NotNull(JsonNode.Parse(await web.Http.GetStringAsync($"/api/plants/{name}/atlas-starter")));

            // Stop and start from the page.
            Assert.Equal("stopped", (await (await web.Http.PostAsync($"/api/plants/{name}/stop", null)).Content.ReadFromJsonAsync<JsonObject>())!["hosting"]!["state"]!.GetValue<string>());
            Assert.False(registry.List().Single(e => e.Simulation == name).HostingEnabled);
            await web.Http.PostAsync($"/api/plants/{name}/start", null);
            Assert.True(registry.List().Single(e => e.Simulation == name).HostingEnabled);

            var removed = await web.Http.DeleteAsync($"/api/plants/{name}?confirm=true");
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Null(web.Services.Plants.Registration(name));
            Assert.Equal(0L, CountTables(name));
        }
        finally { Cleanup(name); }
    }

    [Fact]
    public async Task ThePlanOffersGagePromptsAndConnectAndAtlasBriefs()
    {
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview("PlanTestGageKit")));
        var id = await NewInterview(web.Http);
        Assert.Equal("gage.no_draft", await ErrorCode(await web.Http.GetAsync($"/api/interviews/{id}/gage/prompts?format=json")));
        await Send(web.Http, id, "A bottler; late orders after labeler jams.");

        var prompts = (await web.Http.GetFromJsonAsync<JsonArray>($"/api/interviews/{id}/gage/prompts?format=json"))!;
        Assert.Contains(prompts, p => p!["prompt"]!.GetValue<string>().Contains("Labeler 202"));   // the storyline's own Gage question
        Assert.Contains(prompts, p => p!["title"]!.GetValue<string>() == "Shift report");
        Assert.DoesNotContain(prompts, p => p!["prompt"]!.GetValue<string>().Contains('{'));      // every placeholder filled

        var connect = await web.Http.GetAsync($"/api/interviews/{id}/gage/connect");
        Assert.Equal("PlanTestGageKit-gage-connect.md", connect.Content.Headers.ContentDisposition?.FileNameStar ?? connect.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        string connectBrief = await connect.Content.ReadAsStringAsync();
        Assert.Contains("Ask the user for the password", connectBrief);
        Assert.Contains("plantestgagekit_maintenancetickets", connectBrief);
        Assert.DoesNotContain("FLOWSIM_READER_PASSWORD=", connectBrief);

        string atlasBrief = await web.Http.GetStringAsync($"/api/interviews/{id}/gage/atlas");
        Assert.Contains("`PlanTestGageKit.L1.LB-202.Vibration`", atlasBrief);
        Assert.Contains("\"queryProperties\"", atlasBrief);
        Assert.Contains("# Gage prompts for PlanTestGageKit", await web.Http.GetStringAsync($"/api/interviews/{id}/gage/prompts"));
        Assert.Equal("gage.unknown_kit", await ErrorCode(await web.Http.GetAsync($"/api/interviews/{id}/gage/other")));
    }

    [Fact]
    public async Task AnApprovedInterviewTakesChangesAndAProposedStopWaitsForTheUser()
    {
        var interviews = new InMemoryInterviewStore();
        var registry = new InMemoryWebPlantRegistry();
        await using var web = await Start(() => new ScriptedModel(
            _ => Reply("tool_use", Call(InterviewTools.ProposePlantAction, new { plant = "PlanTestGallo", action = "stop" })),
            _ => Reply("end_turn", Text("Confirm the stop in the bar at the bottom."))), interviews: interviews, registry: registry);
        var id = await NewInterview(web.Http);
        interviews.MarkApproved(id, "PlanTestGallo");
        registry.Add("PlanTestGallo", id);
        // The directory reads the plant store, which has no database here, so the tool reports the plant as unknown;
        // the proposal is stored directly, as propose_plant_action stores it.
        var record = interviews.Load(id)!;
        interviews.SaveState(id, new JsonObject { [InterviewTools.PendingActionKey] = new JsonObject { ["plant"] = "PlanTestGallo", ["action"] = "stop" } }.ToJsonString(), null, null);

        // Approved interviews stay open: a message is a change request, not refused.
        var response = await web.Http.PostAsync($"/api/interviews/{id}/messages", JsonBody(new { text = "Stop Gallo for now." }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var view = await Get(web.Http, $"/api/interviews/{id}");
        Assert.Equal("stop", view["pendingAction"]!["action"]!.GetValue<string>());
        Assert.Null(view["approval"]); // nothing new to approve
        Assert.Contains(interviews.Load(id)!.Messages, m => m.Role == "user" && m.ContentJson.Contains("[Session facts, not typed by the user] PlanTestGallo"));

        var confirmed = await web.Http.PostAsync($"/api/interviews/{id}/plant-action", JsonBody(new { confirm = true }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.False(registry.List().Single().HostingEnabled);
        view = await Get(web.Http, $"/api/interviews/{id}");
        Assert.Null(view["pendingAction"]);
        Assert.Contains(view["messages"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "notice" && m["text"]!.GetValue<string>().StartsWith("PlanTestGallo was stopped"));
        Assert.Equal("plant_action.none", await ErrorCode(await web.Http.PostAsync($"/api/interviews/{id}/plant-action", JsonBody(new { confirm = true }))));
    }

    [Fact]
    public async Task AChangeApprovedTwiceRebuildsThePlantInPlace()
    {
        if (Connection is null) return; // needs FLOWSIM_TEST_PG
        string name = "PlanTestRebuild" + Guid.NewGuid().ToString("N")[..6];
        using var registry = new PostgresWebPlantRegistry(Connection);
        var datasets = new InMemoryPlantDatasets();
        int sessions = 0;
        Func<GageMessageRequest, Task<Message>>[] change =
        [
            _ => Reply("tool_use", Call(InterviewTools.ComposeModel, new
            {
                storylines = new[] { "labeler-jams-late-orders" }, simulation = name, backfill = "7d",
                lineNames = Array.Empty<object>(), productNames = Array.Empty<object>(), peopleNames = Array.Empty<object>()
            })),
            _ => Reply("tool_use", Call(InterviewTools.PreviewPlant, new { hours = 2 })),
            _ => Reply("end_turn", Text("Here is the changed plant. Approving rebuilds it; its current data is deleted."))
        ];
        await using var web = await Start(() => Interlocked.Increment(ref sessions) == 1 ? new ScriptedModel(InterviewToPreview(name)) : new ScriptedModel(change),
            Connection, hostPlants: true, registry: registry, datasets: datasets);
        try
        {
            var id = await NewInterview(web.Http);
            await Send(web.Http, id, "A bottler; late orders after labeler jams.");
            Assert.Equal(HttpStatusCode.Created, (await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))).StatusCode);

            await Send(web.Http, id, "Drop the bearing story; keep only the labeler jams.");
            var view = await Get(web.Http, $"/api/interviews/{id}");
            Assert.Equal(name, view["approval"]!["replaces"]!.GetValue<string>());

            // One step is not enough: the page must send replaceExisting after the second confirmation.
            Assert.Equal("approve.replace_confirm_required", await ErrorCode(await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))));
            var rebuilt = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true, replaceExisting = true }));
            Assert.Equal(HttpStatusCode.Created, rebuilt.StatusCode);
            var result = (await rebuilt.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.True(result["replaced"]!.GetValue<bool>());
            Assert.True(result["datasetEmptied"]!.GetValue<bool>());
            Assert.True(datasets.Current.ContainsKey(name));                       // emptied and reused, same name
            var model = JsonNode.Parse(web.Services.Plants.Registration(name)!.ModelJson)!.ToJsonString();
            Assert.Contains("\"7d\"", model);
            Assert.Null((await Get(web.Http, $"/api/interviews/{id}"))["approval"]); // the new plan is what runs now
            await WaitFor(async () => (await Get(web.Http, "/api/plants"))["plants"]!.AsArray().OfType<JsonObject>()
                .Any(p => p["simulation"]!.GetValue<string>() == name && p["hosting"]!["state"]!.GetValue<string>() == "live"), TimeSpan.FromMinutes(2), "the rebuilt plant to go live");
            Assert.True(CountTables(name) > 0);
            Assert.True(registry.List().Single(e => e.Simulation == name).DatasetCreated);
        }
        finally { Cleanup(name); }
    }

    // ---------------- each plant's own Dataset ----------------

    [Fact]
    public async Task ApproveIsRefusedWhenADatasetWithThePlantsNameExistsAndNothingIsTouched()
    {
        // The refusal comes before admission, so no database is needed.
        string name = "PlanTestTaken" + Guid.NewGuid().ToString("N")[..6];
        var datasets = new InMemoryPlantDatasets();
        datasets.AddForeign(name, purgeAgeDays: 7);
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), datasets: datasets);
        var id = await NewInterview(web.Http);
        await Send(web.Http, id, "A bottler; late orders after labeler jams.");

        var approval = (await Get(web.Http, $"/api/interviews/{id}"))["approval"]!;
        Assert.Equal(name, approval["dataset"]!.GetValue<string>());
        Assert.True(approval["newDataset"]!.GetValue<bool>());
        Assert.Equal(365, approval["datasetPurgeAgeDays"]!.GetValue<int>());

        var response = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("approve.dataset_exists", await ErrorCode(response));
        Assert.Equal(7, datasets.Current[name]); // the foreign Dataset is untouched
        Assert.Single(datasets.Current);
        Assert.Empty(web.Services.Interviews.ApprovedSimulations());
    }

    [Fact]
    public async Task ApproveCreatesThePlantsOwnDatasetAndRemoveDeletesItOnlyWhenAsked()
    {
        if (Connection is null) return; // needs FLOWSIM_TEST_PG
        string id8 = Guid.NewGuid().ToString("N")[..8];
        string kept = "PlanTestDsKeep" + id8, deleted = "PlanTestDsDrop" + id8;
        var names = new Queue<string>([kept, deleted]);
        var datasets = new InMemoryPlantDatasets();
        using var registry = new PostgresWebPlantRegistry(Connection);
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(names.Dequeue())), Connection, registry: registry, datasets: datasets);
        try
        {
            foreach (string name in new[] { kept, deleted })
            {
                var id = await NewInterview(web.Http);
                await Send(web.Http, id, "A bottler; late orders after labeler jams.");
                var approve = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
                Assert.Equal(HttpStatusCode.Created, approve.StatusCode);
                var approved = (await approve.Content.ReadFromJsonAsync<JsonObject>())!;
                Assert.Equal(name, approved["dataset"]!.GetValue<string>());
                Assert.True(approved["datasetCreated"]!.GetValue<bool>());
                Assert.Equal(365, datasets.Current[name]);
                var entry = registry.Find(name)!;
                Assert.Equal(name, entry.Dataset);
                Assert.True(entry.DatasetCreated);
            }
            var listed = (await Get(web.Http, "/api/plants"))["plants"]!.AsArray().OfType<JsonObject>().Single(p => p["simulation"]!.GetValue<string>() == kept);
            Assert.Equal(kept, listed["dataset"]!.GetValue<string>());
            Assert.True(listed["datasetCreated"]!.GetValue<bool>());
            Assert.Contains($"| Historian Dataset | {kept} |", await web.Http.GetStringAsync($"/api/plants/{kept}/demo-guide"));

            // Remove without the box: tables go, the Dataset stays.
            var keep = await web.Http.DeleteAsync($"/api/plants/{kept}?confirm=true");
            Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
            Assert.False((await keep.Content.ReadFromJsonAsync<JsonObject>())!["datasetDeleted"]!.GetValue<bool>());
            Assert.Null(web.Services.Plants.Registration(kept));
            Assert.True(datasets.Current.ContainsKey(kept));

            // With the box: the Dataset goes too.
            var drop = await web.Http.DeleteAsync($"/api/plants/{deleted}?confirm=true&deleteDataset=true");
            Assert.Equal(HttpStatusCode.OK, drop.StatusCode);
            Assert.True((await drop.Content.ReadFromJsonAsync<JsonObject>())!["datasetDeleted"]!.GetValue<bool>());
            Assert.Null(web.Services.Plants.Registration(deleted));
            Assert.False(datasets.Current.ContainsKey(deleted));
            Assert.Equal(0L, CountTables(deleted));
        }
        finally { Cleanup(kept); Cleanup(deleted); }
    }

    [Fact]
    public async Task RemoveNeverDeletesADatasetThePageDidNotCreate()
    {
        if (Connection is null) return;
        string name = "PlanTestDsShared" + Guid.NewGuid().ToString("N")[..8];
        using var registry = new PostgresWebPlantRegistry(Connection);
        // No Historian here: the plant is recorded against the shared TIMEBASE_DATASET, created by someone else.
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection, registry: registry);
        try
        {
            var id = await NewInterview(web.Http);
            await Send(web.Http, id, "A bottler; late orders after labeler jams.");
            Assert.Equal(HttpStatusCode.Created, (await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }))).StatusCode);
            Assert.False(registry.Find(name)!.DatasetCreated);

            var refused = await web.Http.DeleteAsync($"/api/plants/{name}?confirm=true&deleteDataset=true");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("remove.dataset_not_owned", await ErrorCode(refused));
            Assert.NotNull(web.Services.Plants.Registration(name)); // nothing was removed

            // Removing without the box works; it stops the hosted plant before dropping its tables.
            Assert.Equal(HttpStatusCode.OK, (await web.Http.DeleteAsync($"/api/plants/{name}?confirm=true")).StatusCode);
            Assert.Null(web.Services.Plants.Registration(name));
        }
        finally { Cleanup(name); }
    }

    [Fact]
    public async Task AFailedAdmissionDeletesTheDatasetItJustCreated()
    {
        if (Connection is null) return;
        string name = "PlanTestDsUndo" + Guid.NewGuid().ToString("N")[..8];
        var datasets = new InMemoryPlantDatasets();
        await using var web = await Start(() => new ScriptedModel(InterviewToPreview(name)), Connection, datasets: datasets);
        try
        {
            NewPlant(web.Services.Plants, name, "6h"); // the name is already admitted with another model
            var id = await NewInterview(web.Http);
            await Send(web.Http, id, "A bottler; late orders after labeler jams.");
            var response = await web.Http.PostAsync($"/api/interviews/{id}/approve", JsonBody(new { confirm = true }));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("plant.already_admitted", await ErrorCode(response));
            Assert.Empty(datasets.Current);
            Assert.Empty(web.Services.Interviews.ApprovedSimulations());
        }
        finally { Cleanup(name); }
    }

    // ---------------- hosting service (live Postgres) ----------------

    private static string NewPlant(PlantStore store, string name, string backfill = "12h")
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(StorylineFolder, "bases", "beverage-2-line.json")))!.AsObject();
        node["simulation"] = name;
        node["timeline"] = new JsonObject { ["backfill"] = backfill };
        string json = node.ToJsonString();
        var now = DateTimeOffset.UtcNow;
        var loaded = PlantModelLoader.Load(json, new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero));
        Assert.True(loaded.IsValid, JsonSerializer.Serialize(loaded.Errors));
        store.Admit(loaded.Model!, json);
        return name;
    }

    private static PlantHostingService Hosting(PlantStore store, IWebPlantRegistry registry, StringWriter log, Func<string, HistorianClient?>? historian = null) =>
        new(store, registry, historian ?? (_ => null), "Test", new JsonLog(TextWriter.Synchronized(log)),
            new PlantHostingOptions { FirstBackoff = TimeSpan.FromMilliseconds(200), MaximumBackoff = TimeSpan.FromSeconds(1), ElsewhereRecheck = TimeSpan.FromMilliseconds(300) });

    [Fact]
    public async Task TwoPlantsRunConcurrentlyAndAFailureStaysWithItsPlant()
    {
        if (Connection is null) return;
        string id = Guid.NewGuid().ToString("N")[..8];
        string a = "PlanTestHostA" + id, b = "PlanTestHostB" + id, broken = "PlanTestHostC" + id;
        using var store = new PlantStore(Connection);
        try
        {
            var registry = new InMemoryWebPlantRegistry();
            foreach (var name in new[] { a, b, broken }) { NewPlant(store, name); registry.Add(name, Guid.NewGuid()); }
            var log = new StringWriter();
            var hosting = Hosting(store, registry, log, name => name == broken ? throw new RuntimeFailure("connection.missing_setting", "Set TIMEBASE_HISTORIAN_URL.") : null);
            await hosting.StartAsync(CancellationToken.None);
            await WaitFor(() => Task.FromResult(hosting.Status(a)?.State == HostingState.Live && hosting.Status(b)?.State == HostingState.Live),
                TimeSpan.FromMinutes(2), "both plants to go live");
            var failed = hosting.Status(broken)!;
            Assert.Equal(HostingState.Error, failed.State);
            Assert.Equal("connection.missing_setting", failed.Code);
            Assert.True(failed.Failures >= 1);
            Assert.True(store.Registration(a)!.AppliedSequence > 0);
            Assert.True(store.Registration(b)!.AppliedSequence > 0);
            Assert.Equal(0, store.Registration(broken)!.AppliedSequence);
            await hosting.StopAsync(CancellationToken.None);
            Assert.Equal(HostingState.Stopped, hosting.Status(a)!.State);
            Assert.Contains("\"event\":\"plant.error\"", log.ToString());
            Assert.Contains($"\"simulation\":\"{a}\"", log.ToString());
        }
        finally { foreach (var name in new[] { a, b, broken }) Cleanup(name); }
    }

    [Fact]
    public async Task ARestartResumesFromTheSnapshot()
    {
        if (Connection is null) return;
        string name = "PlanTestResume" + Guid.NewGuid().ToString("N")[..8];
        using var store = new PlantStore(Connection);
        try
        {
            NewPlant(store, name, "1d");
            var registry = new InMemoryWebPlantRegistry();
            registry.Add(name, Guid.NewGuid());
            var firstLog = new StringWriter();
            var first = Hosting(store, registry, firstLog);
            await first.StartAsync(CancellationToken.None);
            await WaitFor(() => Task.FromResult(first.Status(name)?.State == HostingState.Live), TimeSpan.FromMinutes(2), "the plant to go live");
            await first.StopAsync(CancellationToken.None);
            Assert.NotNull(store.SnapshotUtc(name));
            Assert.DoesNotContain("plant.snapshot_resumed", firstLog.ToString());
            long applied = store.Registration(name)!.AppliedSequence;

            var secondLog = new StringWriter();
            var second = Hosting(store, registry, secondLog);
            await second.StartAsync(CancellationToken.None);
            await WaitFor(() => Task.FromResult(second.Status(name)?.State == HostingState.Live), TimeSpan.FromMinutes(1), "the restarted plant to go live");
            await second.StopAsync(CancellationToken.None);
            output.WriteLine(secondLog.ToString());
            Assert.Contains("\"event\":\"plant.snapshot_resumed\"", secondLog.ToString());
            Assert.True(store.Registration(name)!.AppliedSequence >= applied);
        }
        finally { Cleanup(name); }
    }

    [Fact]
    public async Task APlantHeldByAnotherProcessIsShownAsRunningElsewhereAndNotTouched()
    {
        if (Connection is null) return;
        string name = "PlanTestElsewhere" + Guid.NewGuid().ToString("N")[..8];
        using var store = new PlantStore(Connection);
        try
        {
            NewPlant(store, name);
            var registry = new InMemoryWebPlantRegistry();
            registry.Add(name, Guid.NewGuid());
            var log = new StringWriter();
            var hosting = Hosting(store, registry, log);
            using (store.LockSimulation(name)) // "another process" owns the plant
            {
                await hosting.StartAsync(CancellationToken.None);
                await WaitFor(() => Task.FromResult(hosting.Status(name)?.State == HostingState.RunningElsewhere), TimeSpan.FromSeconds(30), "running elsewhere");
                await Task.Delay(800); // a few rechecks
                Assert.Equal(HostingState.RunningElsewhere, hosting.Status(name)!.State);
                Assert.Equal(0, store.Registration(name)!.AppliedSequence);
                Assert.Single(Regex.Matches(log.ToString(), "plant.running_elsewhere")); // logged once, not per recheck
            }
            // Once the other process lets go, this container picks the plant up.
            await WaitFor(() => Task.FromResult(hosting.Status(name)?.State == HostingState.Live), TimeSpan.FromMinutes(2), "the plant to go live here");
            await hosting.StopAsync(CancellationToken.None);
        }
        finally { Cleanup(name); }
    }

    private static long CountTables(string simulation)
    {
        using var db = new NpgsqlConnection(Connection);
        db.Open();
        using var count = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND left(tablename, length(@p)) = @p", db);
        count.Parameters.AddWithValue("p", simulation.ToLowerInvariant() + "_");
        return (long)count.ExecuteScalar()!;
    }

    /// <summary>Removes a test plant if a failed test left it behind. Only PlanTest names are ever passed here.</summary>
    private static void Cleanup(string simulation)
    {
        Assert.StartsWith("PlanTest", simulation);
        using var store = new PlantStore(Connection!);
        if (store.Registration(simulation) is not null) store.Remove(simulation);
    }

    // ---------------- demo guide ----------------

    [Fact]
    public void TheDemoGuideResolvesEveryPlaceholderForStorylinesOneAndTwo()
    {
        var library = Library();
        var chosen = new[] { library.Storylines.Find("labeler-jams-late-orders")!, library.Storylines.Find("repeat-bearing-failure")! };
        var composed = PlantStorylines.Compose("beverage-2-line", library.Bases["beverage-2-line"], chosen, new JsonObject { ["simulation"] = "PlanTestGuide" });
        Assert.True(composed.IsValid, JsonSerializer.Serialize(composed.Errors));
        var anchor = DateTimeOffset.Parse("2026-11-21T18:00:00Z");
        var model = PlantModelLoader.Load(composed.ModelJson!, anchor).Model!;
        var needs = new JsonObject
        {
            ["audience"] = JsonSerializer.SerializeToNode(Audience),
            ["chosenStorylines"] = new JsonArray("labeler-jams-late-orders", "repeat-bearing-failure"),
            ["requests"] = new JsonArray(new JsonObject { ["words"] = "MCC trip impact", ["storyline"] = "mcc-trip-impact", ["title"] = "A motor control center trips", ["engine"] = "later", ["offeredInstead"] = new JsonObject { ["id"] = "labeler-jams-late-orders", ["title"] = "Labeler jams make orders late" } })
        };
        var guide = DemoGuide.Build(File.ReadAllText(Path.Combine(StorylineFolder, "demo-guide.md")), library, needs.ToJsonString(), model, "Test", anchor.AddHours(1));
        output.WriteLine(guide.Markdown);

        Assert.DoesNotMatch(@"\{[A-Za-z]+\}", guide.Markdown);
        Assert.DoesNotContain("<!--", guide.Markdown);
        Assert.Contains("### 1. Labeler jams make orders late", guide.Markdown);
        Assert.Contains("### 2. ", guide.Markdown);
        Assert.Contains("`PlanTestGuide.L1.LB-202.Vibration`", guide.Markdown);
        Assert.Contains("`plantestguide_maintenancetickets`", guide.Markdown);
        // The moment has real times: the walk-through jam 30 hours before the anchor (Nov 20, 12:00 UTC).
        Assert.Matches(@"LB-202 on L1 \(LB202-JAM\): vibration starts rising on \w{3} Nov \d+ at \d\d:\d\d UTC, the line stops on Fri Nov 20 at 12:\d\d UTC, and ticket MT-\d+ opens", guide.Markdown);
        Assert.Contains("MX-101", guide.Markdown);
        Assert.Contains("- MCC trip impact (\"A motor control center trips\"): not scheduled yet; offered \"Labeler jams make orders late\" instead.", guide.Markdown);
        Assert.Contains("maintenance, plant manager, food and beverage", guide.Markdown);
        Assert.Contains("<h3>1. Labeler jams make orders late</h3>", guide.Html);
        Assert.Contains("<ol>", guide.Html);
        Assert.DoesNotContain("<script", guide.Html);
    }
}
