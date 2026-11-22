using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDataSim.Web;

/// <summary>
/// The web container: the interview page, its API and the plant hosting
/// service.
///
/// Safety: the page has no login. It listens on loopback by default and is
/// meant to be published only on the host (127.0.0.1:&lt;port&gt;) or a trusted
/// network. Requests are limited to 64 KiB; bodies must be JSON; Host headers
/// must be in FLOWSIM_WEB_ALLOWED_HOSTS; and every state-changing request
/// (POST, DELETE) whose Origin header is present must come from the page's own
/// origin. Browsers always send Origin on these requests, so another web page
/// cannot drive the API; scripts on the host that send no Origin are allowed.
/// </summary>
public static class WebApp
{
    public const long MaxRequestBytes = 64 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplication Build(WebOptions options, WebServices services)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            k.AddServerHeader = false;
        });
        builder.Logging.ClearProviders(); // the container logs JSON lines through JsonLog only
        // PlantHost finishes an in-flight Historian batch before returning; give it time.
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(150));
        builder.Services.AddSingleton(services);
        var hosting = new PlantHostingService(services.Plants, services.Registry, services.Historian, services.Dataset, services.Log, services.HostingOptions);
        builder.Services.AddSingleton(hosting);
        if (options.HostPlants) builder.Services.AddHostedService(_ => hosting);

        var app = builder.Build();
        var allowedHosts = options.AllowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant()).ToHashSet();
        app.Use(async (context, next) =>
        {
            if (Refusal(context, allowedHosts) is { } refusal) { await refusal.ExecuteAsync(context); return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(app.Environment.WebRootPath) });

        if (options.CheckGageAtStartup)
            app.Lifetime.ApplicationStarted.Register(() => _ = services.Gage.Check(force: false, app.Lifetime.ApplicationStopping));
        app.Lifetime.ApplicationStarted.Register(() => services.Log.Write("web.started", null, new
        {
            urls = app.Urls.ToArray(),
            message = "The interview page is up. It has no login: publish it only on this host (127.0.0.1) or a trusted network."
        }));

        MapInterviews(app, services);
        MapPlants(app, services, hosting);
        app.MapGet("/api/health", async (bool? refresh, CancellationToken stop) =>
        {
            var gage = refresh == true ? await services.Gage.Check(force: true, stop) : services.Gage.Current;
            string database;
            try { services.Plants.List(); database = "ok"; }
            catch (RuntimeFailure failure) { database = failure.Error.Code; }
            return Results.Json(new
            {
                status = gage.Ok && database == "ok" ? "ok" : "degraded",
                database,
                dataset = services.Dataset,
                gage
            }, Json);
        });
        return app;
    }

    /// <summary>Host, origin and content-type checks. Null when the request may proceed.</summary>
    private static IResult? Refusal(HttpContext context, HashSet<string> allowedHosts)
    {
        string host = context.Request.Host.Host.ToLowerInvariant();
        if (!allowedHosts.Contains(host) && !allowedHosts.Contains("*"))
            return Error(StatusCodes.Status421MisdirectedRequest, "web.host_not_allowed", "This page only answers on the host names in FLOWSIM_WEB_ALLOWED_HOSTS. Open it as http://localhost:<port>, or add the name you use to that setting.");
        bool changesState = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method) || HttpMethods.IsPut(context.Request.Method);
        if (!changesState) return null;
        if (context.Request.Headers.Origin is { Count: > 0 } origin)
        {
            string own = $"{context.Request.Scheme}://{context.Request.Host}";
            if (!string.Equals(origin.ToString(), own, StringComparison.OrdinalIgnoreCase))
                return Error(StatusCodes.Status403Forbidden, "web.cross_origin", "This request came from another web page. Use the interview page itself to change interviews and plants.");
        }
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            string? type = context.Request.ContentType;
            if (type is null || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return Error(StatusCodes.Status415UnsupportedMediaType, "web.json_required", "Send the request body as JSON with Content-Type: application/json.");
        }
        return null;
    }

    internal static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, Json, statusCode: status);

    private static IResult Failure(RuntimeFailure failure, int status = StatusCodes.Status409Conflict) => Error(status, failure.Error.Code, failure.Message);

    /// <summary>
    /// Reads a small JSON object body; null when the body is missing or not an
    /// object. A body over the 64 KiB limit throws BodyTooLarge, which the
    /// callers turn into 413.
    /// </summary>
    private static async Task<JsonObject?> Body(HttpRequest request)
    {
        try { return await JsonNode.ParseAsync(request.Body) as JsonObject; }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge) { throw new BodyTooLarge(); }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return null; }
    }

    private sealed class BodyTooLarge : Exception;

    private static IResult TooLarge() => Error(StatusCodes.Status413PayloadTooLarge, "web.request_too_large", $"Requests are limited to {MaxRequestBytes / 1024} KiB. Send a shorter message.");

    // ---------------- interviews ----------------

    private static void MapInterviews(WebApplication app, WebServices services)
    {
        app.MapPost("/api/interviews", () =>
        {
            try
            {
                var session = InterviewSession.Start(services.Library, new LazyModel(services), services.Interviews, services.InterviewOptions, services.Clock);
                services.Log.Write("interview.started", null, new { interviewId = session.Id });
                var record = services.Interviews.Load(session.Id)!;
                return Results.Json(InterviewView.Build(record, services.Library, services.Dataset, false, services.Clock()), Json, statusCode: StatusCodes.Status201Created);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapGet("/api/interviews/{id:guid}", (Guid id) =>
        {
            try
            {
                return services.Interviews.Load(id) is { } record
                    ? Results.Json(InterviewView.Build(record, services.Library, services.Dataset, services.Gate.IsBusy(id), services.Clock()), Json)
                    : NotFound();
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapPost("/api/interviews/{id:guid}/messages", async (Guid id, HttpContext context) =>
        {
            JsonObject? body;
            try { body = await Body(context.Request); } catch (BodyTooLarge) { return TooLarge(); }
            if (body?["text"] is not JsonValue textValue || !textValue.TryGetValue(out string? text))
                return Error(StatusCodes.Status400BadRequest, "interview.text_required", "Send {\"text\": \"…\"} with the message for the interviewer.");
            text = text.Trim();
            int limit = (services.InterviewOptions ?? new InterviewOptions()).MaxUserMessageCharacters;
            if (text.Length == 0) return Error(StatusCodes.Status400BadRequest, "interview.empty_message", "Type a message for the interviewer.");
            if (text.Length > limit) return Error(StatusCodes.Status400BadRequest, "interview.message_too_long", $"Keep a message under {limit} characters.");

            var gate = services.Gate.TryEnter(id);
            if (gate is null)
                return Error(StatusCodes.Status409Conflict, "interview.busy", "The interviewer is still answering your previous message. Wait for that answer, then send the next one.");
            InterviewSession session;
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) { gate.Dispose(); return NotFound(); }
                if (record.ApprovedSimulation is not null)
                {
                    gate.Dispose();
                    return Error(StatusCodes.Status409Conflict, "interview.approved", $"This interview was approved and {record.ApprovedSimulation} is running. Start a new interview to design another plant.");
                }
                session = InterviewSession.Resume(id, services.Library, new LazyModel(services), services.Interviews, services.InterviewOptions, services.Clock);
            }
            catch (RuntimeFailure failure) { gate.Dispose(); return Failure(failure, StatusCodes.Status503ServiceUnavailable); }

            // The turn runs to completion and is stored even if the browser goes
            // away; the stream only relays its events. The gate is released when
            // the turn ends, not when the request does.
            var events = Channel.CreateUnbounded<(string Event, object Data)>();
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            _ = Task.Run(async () =>
            {
                try
                {
                    var turn = await session.Send(text, e =>
                    {
                        if (e.Kind == InterviewEventKind.Stopped && e.Code?.StartsWith("gage.", StringComparison.Ordinal) == true) services.Gage.ReportFailure(e.Code, e.Text);
                        events.Writer.TryWrite((EventName(e.Kind), new { text = e.Text, code = e.Code }));
                    }, stopping);
                    if (turn.ModelCalls > 0) services.Gage.ReportWorking();
                    services.Log.Write("interview.turn", null, new
                    {
                        interviewId = id, outcome = turn.Outcome, modelCalls = turn.ModelCalls, toolRounds = turn.ToolRounds,
                        inputTokens = turn.Usage.InputTokens, outputTokens = turn.Usage.OutputTokens, cacheReadTokens = turn.Usage.CacheReadTokens
                    });
                    events.Writer.TryWrite(("done", new { outcome = turn.Outcome, previewReady = session.State.PreviewJson is not null }));
                }
                catch (RuntimeFailure failure)
                {
                    events.Writer.TryWrite(("failure", new { code = failure.Error.Code, text = failure.Message }));
                }
                catch (OperationCanceledException)
                {
                    events.Writer.TryWrite(("failure", new { code = "web.stopping", text = "The container is shutting down. Reopen the page when it is back; the interview continues where it stopped." }));
                }
                catch (Exception error)
                {
                    services.Log.Write("interview.unexpected", null, new { interviewId = id, type = error.GetType().Name });
                    events.Writer.TryWrite(("failure", new { code = "web.unexpected", text = "The interview service failed unexpectedly. Reload the page and send the message again." }));
                }
                finally
                {
                    gate.Dispose();
                    events.Writer.TryComplete();
                }
            });

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var (name, data) in events.Reader.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The browser left; the turn carries on and the page reloads it from the store.
                }
            }
            return Results.Empty;
        });

        app.MapPost("/api/interviews/{id:guid}/approve", async (Guid id, HttpContext context) =>
        {
            JsonObject? body;
            try { body = await Body(context.Request); } catch (BodyTooLarge) { return TooLarge(); }
            if (body?["confirm"] is not JsonValue confirm || !confirm.TryGetValue(out bool confirmed) || !confirmed)
                return Error(StatusCodes.Status400BadRequest, "approve.confirm_required", "Approving builds and runs the plant. Send {\"confirm\": true} from the confirmation step in the page.");
            using var gate = services.Gate.TryEnter(id);
            if (gate is null)
                return Error(StatusCodes.Status409Conflict, "interview.busy", "The interviewer is still answering. Wait for the answer and review the preview, then approve.");
            try { return Approve(id, services, context.RequestServices.GetRequiredService<PlantHostingService>()); }
            catch (RuntimeFailure failure) { return Failure(failure); }
        });
    }

    private static IResult Approve(Guid id, WebServices services, PlantHostingService hosting)
    {
        var record = services.Interviews.Load(id);
        if (record is null) return NotFound();
        if (record.ApprovedSimulation is not null)
            return Error(StatusCodes.Status409Conflict, "approve.already_approved", $"This interview was already approved as {record.ApprovedSimulation}. See the Plants tab.");
        if (record.DraftModelJson is null || record.PreviewJson is null)
            return Error(StatusCodes.Status409Conflict, "approve.no_preview", "There is no current preview to approve. Ask the interviewer to show the preview, review it, then approve.");
        if (string.IsNullOrWhiteSpace(services.Dataset))
            return Error(StatusCodes.Status503ServiceUnavailable, "hosting.dataset_missing", "Set TIMEBASE_DATASET to the Historian Dataset for plant tags and restart the container before approving.");

        // The anchor is fixed now, in whole seconds like the CLI; the history ends here.
        var now = services.Clock();
        var anchor = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var loaded = PlantModelLoader.Load(record.DraftModelJson, anchor);
        if (!loaded.IsValid)
            return Results.Json(new
            {
                error = new { code = "approve.draft_invalid", message = "The draft no longer validates at the current time. Ask the interviewer to compose and preview it again." },
                errors = loaded.Errors.Take(10)
            }, Json, statusCode: StatusCodes.Status409Conflict);
        var model = loaded.Model!;
        var registration = services.Plants.Admit(model, record.DraftModelJson);
        if (!services.Interviews.MarkApproved(id, model.Simulation))
            return Error(StatusCodes.Status409Conflict, "approve.already_approved", "This interview was approved by another request a moment ago. See the Plants tab.");
        services.Registry.Add(model.Simulation, id);
        hosting.StartPlant(model.Simulation);
        services.Log.Write("plant.approved", model.Simulation, new { interviewId = id, anchorUtc = registration.AnchorUtc, startUtc = registration.StartUtc, tables = PlantSchema.TablesFor(model).Count });
        return Results.Json(new
        {
            simulation = model.Simulation,
            tablePrefix = model.TablePrefix,
            tagRoot = model.TagRoot,
            anchorUtc = registration.AnchorUtc,
            startUtc = registration.StartUtc,
            dataset = services.Dataset,
            tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToList(),
            message = "Approved. The plant is admitted and starting: it backfills its history, then runs live."
        }, Json, statusCode: StatusCodes.Status201Created);
    }

    private static string EventName(InterviewEventKind kind) => kind switch
    {
        InterviewEventKind.AssistantText => "assistant_text",
        InterviewEventKind.ToolActivity => "tool_activity",
        InterviewEventKind.PreviewReady => "preview_ready",
        InterviewEventKind.Stopped => "stopped",
        InterviewEventKind.Refusal => "refusal",
        _ => "error"
    };

    private static IResult NotFound() => Error(StatusCodes.Status404NotFound, "interview.not_found", "No interview with this id is stored. Start a new interview.");

    /// <summary>
    /// Creates the Gage model on the first call, so a missing Pulse setting
    /// stops the turn with its explanation (a gage.* Stopped event) instead of
    /// failing the request before the page can show it.
    /// </summary>
    private sealed class LazyModel(WebServices services) : IInterviewModel
    {
        private IInterviewModel? model;
        public Task<Anthropic.Models.Messages.Message> CreateMessage(GageMessageRequest request, CancellationToken stop) => (model ??= services.Model()).CreateMessage(request, stop);
    }

    // ---------------- plants ----------------

    private static void MapPlants(WebApplication app, WebServices services, PlantHostingService hosting)
    {
        app.MapGet("/api/plants", () =>
        {
            try
            {
                var owned = services.Interviews.ApprovedSimulations();
                var registered = services.Plants.List();
                var plants = registered.Select(r =>
                {
                    bool managed = owned.Contains(r.Simulation);
                    var status = hosting.Status(r.Simulation);
                    if (status is null)
                        status = services.Plants.IsHostRunning(r.Simulation)
                            ? new PlantHostingStatus(HostingState.RunningElsewhere, DateTimeOffset.UtcNow, Message: "Another process is delivering this plant. This container does not touch it.")
                            : new PlantHostingStatus(managed ? HostingState.Stopped : HostingState.NotHosted, DateTimeOffset.UtcNow,
                                Message: managed ? "Not running. Start it to continue where it left off." : "Not hosted by this container.");
                    return new
                    {
                        r.Simulation, r.TablePrefix, r.AnchorUtc, r.StartUtc, r.AppliedSequence, r.TagCursorUtc,
                        snapshotUtc = services.Plants.SnapshotUtc(r.Simulation),
                        resentBatches = r.UncertainBatches,
                        sendingInProgress = r.SendingUntilUtc is not null,
                        managed,
                        hosting = status
                    };
                }).ToList();
                return Results.Json(new { dataset = services.Dataset, plants }, Json);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapPost("/api/plants/{name}/stop", async (string name) =>
        {
            if (Ownership(services, name) is { } refused) return refused;
            services.Registry.SetHostingEnabled(name, false);
            await hosting.StopPlant(name);
            services.Log.Write("plant.stop_requested", name, new { message = "Stopped from the page." });
            return Results.Json(new { simulation = name, hosting = hosting.Status(name) }, Json);
        });

        app.MapPost("/api/plants/{name}/start", (string name) =>
        {
            if (Ownership(services, name) is { } refused) return refused;
            services.Registry.SetHostingEnabled(name, true);
            hosting.StartPlant(name);
            return Results.Json(new { simulation = name, hosting = hosting.Status(name) }, Json);
        });

        app.MapDelete("/api/plants/{name}", async (string name, bool? confirm) =>
        {
            if (confirm != true)
                return Error(StatusCodes.Status400BadRequest, "remove.confirm_required", "Removing drops the plant's tables. Confirm in the page (it sends ?confirm=true).");
            if (Ownership(services, name) is { } refused) return refused;
            try
            {
                await hosting.StopPlant(name);
                int dropped = services.Plants.Remove(name);
                hosting.Forget(name);
                services.Log.Write("plant.removed", name, new { droppedTables = dropped });
                return Results.Json(new { simulation = name, droppedTables = dropped, message = "Tables and registration removed. Historian tags already published are not affected." }, Json);
            }
            catch (RuntimeFailure failure) { return Failure(failure); }
        });

        app.MapGet("/api/plants/{name}/connect-spec", (string name, string? format) => Download(services, name, model => format == "json"
            ? Results.Text(JsonSerializer.Serialize(PlantConnectSpec.Json(model), new JsonSerializerOptions(Json) { WriteIndented = true }), "application/json")
            : Results.Text(PlantConnectSpec.Markdown(model), "text/markdown; charset=utf-8")));

        app.MapGet("/api/plants/{name}/atlas-starter", (string name) => Download(services, name, model =>
        {
            var spec = JsonSerializer.SerializeToNode(PlantConnectSpec.Json(model), Json)!;
            return Results.Text(spec["atlas"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
        }));

        app.MapGet("/api/plants/{name}/demo-guide", (string name, string? format) => Download(services, name, model =>
        {
            var interview = services.Interviews.ApprovedSimulations().Contains(name) ? FindInterview(services, name) : null;
            var guide = DemoGuide.Build(services.DemoGuideTemplate, services.Library, interview?.NeedsJson, model, services.Dataset, services.Clock());
            return format == "html" ? Results.Text(guide.Html, "text/html; charset=utf-8") : Results.Text(guide.Markdown, "text/markdown; charset=utf-8");
        }));
    }

    /// <summary>Stop, start and remove are only for plants approved in a web interview (flowsim_interviews.approved_simulation).</summary>
    private static IResult? Ownership(WebServices services, string name)
    {
        try
        {
            if (!services.Interviews.ApprovedSimulations().Contains(name))
                return Error(StatusCodes.Status403Forbidden, "plant.not_owned", "This plant was not approved from this page, so the page does not stop, start or remove it. Use the tool that created it (for example plant remove in its own container).");
            return null;
        }
        catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
    }

    private static IResult Download(WebServices services, string name, Func<PlantModel, IResult> render)
    {
        try
        {
            var registration = services.Plants.Registration(name);
            if (registration is null) return Error(StatusCodes.Status404NotFound, "plant.not_admitted", "No plant with this name is registered in flow_data_sim.");
            var loaded = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc);
            if (!loaded.IsValid) return Error(StatusCodes.Status409Conflict, "plant.stored_model_invalid", "The stored model no longer validates with this version of the simulator.");
            return render(loaded.Model!);
        }
        catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
    }

    /// <summary>The interview that approved a plant, for its needs profile. Looked up through the web registry.</summary>
    private static InterviewRecord? FindInterview(WebServices services, string simulation)
    {
        var entry = services.Registry.List().FirstOrDefault(e => e.Simulation == simulation);
        return entry is null || entry.InterviewId == Guid.Empty ? null : services.Interviews.Load(entry.InterviewId);
    }
}
