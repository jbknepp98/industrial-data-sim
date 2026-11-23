using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    /// <summary>One attachment upload: a 10 MB file as base64 (4/3 larger) plus its JSON wrapper.</summary>
    public const long MaxAttachmentRequestBytes = AttachmentReader.MaxFileBytes / 3 * 4 + 64 * 1024;
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
        // no-cache: the browser keeps the files but asks again (ETag) on every
        // load, so a new image's page and script are used at once instead of a
        // cached copy from the previous version.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(app.Environment.WebRootPath),
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache"
        });

        if (options.CheckGageAtStartup)
            app.Lifetime.ApplicationStarted.Register(() => _ = services.Gage.Check(force: false, app.Lifetime.ApplicationStopping));
        app.Lifetime.ApplicationStarted.Register(() => services.Log.Write("web.started", null, new
        {
            urls = app.Urls.ToArray(),
            message = "The interview page is up. It has no login: publish it only on this host (127.0.0.1) or a trusted network."
        }));

        MapInterviews(app, services, hosting, new WebPlantDirectory(services, hosting));
        MapPlants(app, services, hosting);
        MapBrowse(app, services, hosting);
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

    private static void MapInterviews(WebApplication app, WebServices services, PlantHostingService hosting, IPlantDirectory directory)
    {
        app.MapPost("/api/interviews", () =>
        {
            try
            {
                var session = InterviewSession.Start(services.Library, new LazyModel(services), services.Interviews, services.InterviewOptions, services.Clock, directory);
                services.Log.Write("interview.started", null, new { interviewId = session.Id });
                var record = services.Interviews.Load(session.Id)!;
                return Results.Json(InterviewView.Build(record, services.Library, services.Datasets is not null, services.Dataset, false, services.Clock()), Json, statusCode: StatusCodes.Status201Created);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapGet("/api/interviews/{id:guid}", (Guid id) =>
        {
            try
            {
                return services.Interviews.Load(id) is { } record
                    ? Results.Json(InterviewView.Build(record, services.Library, services.Datasets is not null, services.Dataset, services.Gate.IsBusy(id), services.Clock()), Json)
                    : NotFound();
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        // A file for the interview: JSON {"name": "…", "data": "<base64>"}, read,
        // checked and converted by AttachmentReader, stored, and referenced by id
        // in the next message. This endpoint alone accepts a large body.
        app.MapPost("/api/interviews/{id:guid}/attachments", async (Guid id, HttpContext context) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = MaxAttachmentRequestBytes;
            JsonObject? body;
            try { body = await Body(context.Request); }
            catch (BodyTooLarge)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "interview.attachment_too_large", $"Attach files up to {AttachmentReader.MaxFileBytes / 1024 / 1024} MB; split or export a smaller part.");
            }
            string? name = body?["name"] is JsonValue n && n.TryGetValue(out string? s1) ? s1 : null;
            string? data = body?["data"] is JsonValue d && d.TryGetValue(out string? s2) ? s2 : null;
            if (string.IsNullOrWhiteSpace(name) || data is null)
                return Error(StatusCodes.Status400BadRequest, "interview.attachment_required", "Send {\"name\": \"file name\", \"data\": \"base64 content\"} to attach a file.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { return Error(StatusCodes.Status400BadRequest, "interview.attachment_unreadable", "The file content was not valid base64. Attach the file again from the page."); }
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) return NotFound();
                if (services.Interviews.CountAttachments(id) >= AttachmentReader.MaxFilesPerInterview)
                    return Error(StatusCodes.Status409Conflict, "interview.too_many_attachments", $"An interview can hold {AttachmentReader.MaxFilesPerInterview} files. Describe the rest in a message, or start a new plant.");
                var attachment = AttachmentReader.Read(id, name, bytes, services.Clock());
                services.Interviews.SaveAttachment(attachment);
                services.Log.Write("interview.attachment", null, new { interviewId = id, attachmentId = attachment.Id, kind = attachment.Kind, sizeBytes = attachment.SizeBytes, truncated = attachment.Truncated });
                return Results.Json(new
                {
                    id = attachment.Id, name = attachment.Name, kind = attachment.Kind, sizeBytes = attachment.SizeBytes,
                    characters = attachment.Text?.Length, truncated = attachment.Truncated
                }, Json, statusCode: StatusCodes.Status201Created);
            }
            catch (RuntimeFailure failure) when (failure.Error.Code.StartsWith("interview.attachment", StringComparison.Ordinal))
            {
                return Failure(failure, StatusCodes.Status400BadRequest);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapPost("/api/interviews/{id:guid}/messages", async (Guid id, HttpContext context) =>
        {
            JsonObject? body;
            try { body = await Body(context.Request); } catch (BodyTooLarge) { return TooLarge(); }
            string text = body?["text"] is JsonValue textValue && textValue.TryGetValue(out string? typed) ? typed.Trim() : "";
            var attachments = new List<Guid>();
            foreach (var item in body?["attachments"] as JsonArray ?? [])
            {
                if (item is not JsonValue v || !v.TryGetValue(out string? raw) || !Guid.TryParse(raw, out var attachmentId))
                    return Error(StatusCodes.Status400BadRequest, "interview.attachment_not_found", "An attachment id was not valid. Remove the file and attach it again.");
                attachments.Add(attachmentId);
            }
            int limit = (services.InterviewOptions ?? new InterviewOptions()).MaxUserMessageCharacters;
            if (text.Length == 0 && attachments.Count == 0) return Error(StatusCodes.Status400BadRequest, "interview.empty_message", "Type a message for the interviewer, or attach a file.");
            if (text.Length > limit) return Error(StatusCodes.Status400BadRequest, "interview.message_too_long", $"Keep a message under {limit} characters.");
            if (attachments.Distinct().Count() > AttachmentReader.MaxFilesPerMessage)
                return Error(StatusCodes.Status400BadRequest, "interview.too_many_attachments", $"Attach at most {AttachmentReader.MaxFilesPerMessage} files to one message; send the rest with the next one.");
            if (attachments.FirstOrDefault(a => services.Interviews.LoadAttachment(id, a) is null) is var missing && missing != Guid.Empty)
                return Error(StatusCodes.Status400BadRequest, "interview.attachment_not_found", "An attached file is no longer stored for this interview. Remove it and attach it again.");

            var gate = services.Gate.TryEnter(id);
            if (gate is null)
                return Error(StatusCodes.Status409Conflict, "interview.busy", "The interviewer is still answering your previous message. Wait for that answer, then send the next one.");
            InterviewSession session;
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) { gate.Dispose(); return NotFound(); }
                // An approved interview carries on as a change to its plant (InterviewState.LockedSimulation).
                session = InterviewSession.Resume(id, services.Library, new LazyModel(services), services.Interviews, services.InterviewOptions, services.Clock, directory);
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
                    }, stopping, attachments);
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
            bool replace = body?["replaceExisting"] is JsonValue r && r.TryGetValue(out bool replacing) && replacing;
            using var gate = services.Gate.TryEnter(id);
            if (gate is null)
                return Error(StatusCodes.Status409Conflict, "interview.busy", "The interviewer is still answering. Wait for the answer and review the preview, then approve.");
            try { return await Approve(id, replace, services, hosting, context.RequestAborted); }
            catch (RuntimeFailure failure) { return Failure(failure); }
        });

        // The Gage kit for the interview's current draft, before (or after) it is
        // built: the names, tables and tags are the ones the plant gets.
        app.MapGet("/api/interviews/{id:guid}/gage/{kind}", (Guid id, string kind, string? format) =>
        {
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) return NotFound();
                if (record.DraftModelJson is null)
                    return Error(StatusCodes.Status409Conflict, "gage.no_draft", "There is no plant plan yet. Finish the interview until the preview is ready.");
                var loaded = PlantModelLoader.Load(record.DraftModelJson, services.Clock());
                if (!loaded.IsValid) return Error(StatusCodes.Status409Conflict, "gage.draft_invalid", "The plan no longer validates. Ask the interviewer to compose and preview it again.");
                string dataset = record.ApprovedSimulation is { } built
                    ? services.Registry.Find(built)?.Dataset ?? services.Dataset ?? built
                    : services.Datasets is not null ? loaded.Model!.Simulation : services.Dataset ?? loaded.Model!.Simulation;
                return GageKit(kind, format, services, record.NeedsJson, loaded.Model!, dataset);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });

        // A stop or start the interviewer proposed (propose_plant_action): the
        // user confirms or cancels it here, from the bar in the page.
        app.MapPost("/api/interviews/{id:guid}/plant-action", async (Guid id, HttpContext context) =>
        {
            JsonObject? body;
            try { body = await Body(context.Request); } catch (BodyTooLarge) { return TooLarge(); }
            bool confirmed = body?["confirm"] is JsonValue c && c.TryGetValue(out bool yes) && yes;
            using var gate = services.Gate.TryEnter(id);
            if (gate is null) return Error(StatusCodes.Status409Conflict, "interview.busy", "The interviewer is still answering. Wait for the answer, then confirm.");
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) return NotFound();
                var needs = record.NeedsJson is null ? new JsonObject() : JsonNode.Parse(record.NeedsJson)!.AsObject();
                if (needs[InterviewTools.PendingActionKey] is not JsonObject pending)
                    return Error(StatusCodes.Status409Conflict, "plant_action.none", "There is no stop or start waiting for confirmation. Ask the interviewer again.");
                string plant = pending["plant"]?.GetValue<string>() ?? "", action = pending["action"]?.GetValue<string>() ?? "";
                needs.Remove(InterviewTools.PendingActionKey);
                services.Interviews.SaveState(id, needs.ToJsonString(), record.DraftModelJson, record.PreviewJson);
                string text;
                if (!confirmed) text = $"Cancelled: {plant} was not {(action == "stop" ? "stopped" : "started")}.";
                else
                {
                    if (Ownership(services, plant) is { } refused) return refused;
                    if (action == "stop")
                    {
                        services.Registry.SetHostingEnabled(plant, false);
                        await hosting.StopPlant(plant);
                        text = $"{plant} was stopped. Start it again to continue where it left off.";
                    }
                    else
                    {
                        services.Registry.SetHostingEnabled(plant, true);
                        hosting.StartPlant(plant);
                        text = $"{plant} is starting. It catches up on the time it was stopped, then runs live.";
                    }
                    services.Log.Write("plant." + (action == "stop" ? "stop_requested" : "start_requested"), plant, new { interviewId = id, message = "Confirmed from the interview." });
                }
                services.Interviews.Append(id, [("notice", new JsonObject { ["code"] = "plant_action", ["text"] = text }.ToJsonString(), null)], record.Status == InterviewStatus.InTurn ? InterviewStatus.Open : record.Status);
                return Results.Json(new { plant, action, done = confirmed, message = text }, Json);
            }
            catch (RuntimeFailure failure) { return Failure(failure); }
        });

        // A change to an existing plant continues in the interview that built it
        // (change_plant). The page calls this to take the hand-off: it is cleared
        // here, and the page opens the target interview and sends the request there.
        app.MapPost("/api/interviews/{id:guid}/handoff", (Guid id) =>
        {
            try
            {
                var record = services.Interviews.Load(id);
                if (record is null) return NotFound();
                var needs = record.NeedsJson is null ? new JsonObject() : JsonNode.Parse(record.NeedsJson)!.AsObject();
                if (needs[InterviewTools.HandoffKey] is not JsonObject handoff)
                    return Error(StatusCodes.Status409Conflict, "handoff.none", "There is no plant change waiting to be opened.");
                needs.Remove(InterviewTools.HandoffKey);
                services.Interviews.SaveState(id, needs.ToJsonString(), record.DraftModelJson, record.PreviewJson);
                string plant = handoff["plant"]?.GetValue<string>() ?? "";
                if (!Guid.TryParse(handoff["interviewId"]?.GetValue<string>(), out var target) || services.Interviews.Load(target) is null)
                    return Error(StatusCodes.Status409Conflict, "handoff.missing", $"The interview that built {plant} is no longer stored. Design the change as a new plant instead.");
                return Results.Json(new { plant, interviewId = target, request = handoff["request"]?.GetValue<string>() ?? "" }, Json);
            }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        });
    }

    private const string ApprovedDraftKey = InterviewState.ApprovedDraftKey;
    private static string DraftHash(string draftJson) => InterviewState.DraftHash(draftJson);

    private static async Task<IResult> Approve(Guid id, bool replace, WebServices services, PlantHostingService hosting, CancellationToken stop)
    {
        var record = services.Interviews.Load(id);
        if (record is null) return NotFound();
        if (record.DraftModelJson is null || record.PreviewJson is null)
            return Error(StatusCodes.Status409Conflict, "approve.no_preview", "There is no current preview to approve. Ask the interviewer to show the preview, review it, then approve.");
        var needs = record.NeedsJson is null ? new JsonObject() : JsonNode.Parse(record.NeedsJson)!.AsObject();
        string draftHash = DraftHash(record.DraftModelJson);
        if (record.ApprovedSimulation is { } existing)
        {
            if (needs[ApprovedDraftKey]?.GetValue<string>() == draftHash)
                return Error(StatusCodes.Status409Conflict, "approve.unchanged", $"This plan is what {existing} already runs. Ask the interviewer for a change, review the new preview, then approve.");
            if (!replace)
                return Error(StatusCodes.Status400BadRequest, "approve.replace_confirm_required", $"Approving rebuilds {existing}: its current tables and the tag history in its Dataset are deleted. Confirm both steps in the page (it sends replaceExisting: true).");
            return await Rebuild(id, record, existing, needs, draftHash, services, hosting);
        }

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

        // The plant's own Dataset, named like the simulation, is created first.
        // A name some Dataset already has is refused, so the page never writes
        // to a Dataset it did not create. If admission then fails, the new
        // Dataset is deleted again, leaving nothing behind.
        string? dataset = null;
        if (services.Datasets is { } datasets)
        {
            if (await datasets.Exists(model.Simulation, stop)) return DatasetTaken(model.Simulation);
            try { await datasets.Create(model.Simulation, stop); }
            catch (RuntimeFailure failure) when (failure.Error.Code == "historian.dataset_exists") { return DatasetTaken(model.Simulation); }
            dataset = model.Simulation;
        }
        PlantRegistration registration;
        try { registration = services.Plants.Admit(model, record.DraftModelJson); }
        catch (RuntimeFailure)
        {
            if (dataset is not null)
            {
                try { await services.Datasets!.Delete(dataset, CancellationToken.None); }
                catch (RuntimeFailure cleanup) { services.Log.Write("plant.dataset_cleanup_failed", model.Simulation, new { code = cleanup.Error.Code, message = $"The plant was not admitted, and its new Dataset {dataset} could not be deleted again: {cleanup.Message} Delete it in Historian → Datasets." }); }
            }
            throw;
        }
        if (!services.Interviews.MarkApproved(id, model.Simulation))
            return Error(StatusCodes.Status409Conflict, "approve.already_approved", "This interview was approved by another request a moment ago. See the Plants tab.");
        needs[ApprovedDraftKey] = draftHash;
        needs[InterviewSession.ChangeBriefedKey] = false;
        services.Interviews.SaveState(id, needs.ToJsonString(), record.DraftModelJson, record.PreviewJson);
        services.Registry.Add(model.Simulation, id, dataset ?? services.Dataset, datasetCreated: dataset is not null);
        hosting.StartPlant(model.Simulation);
        services.Log.Write("plant.approved", model.Simulation, new { interviewId = id, anchorUtc = registration.AnchorUtc, startUtc = registration.StartUtc, tables = PlantSchema.TablesFor(model).Count, dataset = dataset ?? services.Dataset, datasetCreated = dataset is not null });
        return Results.Json(new
        {
            simulation = model.Simulation,
            tablePrefix = model.TablePrefix,
            tagRoot = model.TagRoot,
            anchorUtc = registration.AnchorUtc,
            startUtc = registration.StartUtc,
            dataset = dataset ?? services.Dataset,
            datasetCreated = dataset is not null,
            tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToList(),
            message = dataset is null
                ? "Approved. The plant is admitted and starting: it backfills its history, then runs live."
                : $"Approved. The Dataset {dataset} was created (purge age {PlantDatasets.PurgeAgeDays} days) and the plant is starting: it backfills its history, then runs live."
        }, Json, statusCode: StatusCodes.Status201Created);
    }

    private static IResult DatasetTaken(string name) => Error(StatusCodes.Status409Conflict, "approve.dataset_exists",
        $"Historian already has a Dataset named {name}. Each plant gets a new Dataset named after it, and the page never writes to one it did not create. Ask the interviewer to give the plant another name, or delete that Dataset in Historian if it is no longer needed, then approve again.");

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
                // Only plants this page built; BluebirdBev and other tools' plants are never listed.
                var entries = services.Registry.List().ToDictionary(e => e.Simulation, StringComparer.Ordinal);
                var plants = PagePlants.List(services).Select(r =>
                {
                    entries.TryGetValue(r.Simulation, out var entry);
                    return new
                    {
                        r.Simulation, r.TablePrefix, r.AnchorUtc, r.StartUtc, r.AppliedSequence, r.TagCursorUtc,
                        snapshotUtc = services.Plants.SnapshotUtc(r.Simulation),
                        resentBatches = r.UncertainBatches,
                        sendingInProgress = r.SendingUntilUtc is not null,
                        managed = true,
                        dataset = entry?.Dataset ?? services.Dataset,
                        datasetCreated = entry?.DatasetCreated == true,
                        hosting = PagePlants.Status(services, hosting, r.Simulation)
                    };
                }).ToList();
                return Results.Json(new { datasetPurgeAgeDays = services.Datasets is null ? (int?)null : PlantDatasets.PurgeAgeDays, plants }, Json);
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

        app.MapDelete("/api/plants/{name}", async (string name, bool? confirm, bool? deleteDataset) =>
        {
            if (confirm != true)
                return Error(StatusCodes.Status400BadRequest, "remove.confirm_required", "Removing drops the plant's tables. Confirm in the page (it sends ?confirm=true).");
            if (Ownership(services, name) is { } refused) return refused;
            try
            {
                // Read the entry first: removing the plant deletes its registry row.
                var entry = services.Registry.Find(name);
                string? dataset = entry?.DatasetCreated == true ? entry.Dataset : null;
                if (deleteDataset == true && (dataset is null || services.Datasets is null))
                {
                    string target = (entry?.Dataset ?? services.Dataset) is { } other ? "the Dataset " + other : "a Dataset";
                    return Error(StatusCodes.Status409Conflict, "remove.dataset_not_owned",
                        $"This plant writes to {target} that the page did not create, so the page never deletes it. Remove the plant without deleting its Dataset. Nothing was removed.");
                }

                await hosting.StopPlant(name);
                int dropped = services.Plants.Remove(name);
                hosting.Forget(name);
                if (deleteDataset != true)
                {
                    services.Log.Write("plant.removed", name, new { droppedTables = dropped, dataset = entry?.Dataset, datasetDeleted = false });
                    return Results.Json(new { simulation = name, droppedTables = dropped, datasetDeleted = false,
                        message = entry?.Dataset is { } kept
                            ? $"Tables and registration removed. The Dataset {kept} and its tags were kept; delete it in Historian when it is no longer needed."
                            : "Tables and registration removed. Historian tags already published are not affected." }, Json);
                }
                try { await services.Datasets!.Delete(dataset!, CancellationToken.None); }
                catch (RuntimeFailure failure)
                {
                    services.Log.Write("plant.removed", name, new { droppedTables = dropped, dataset, datasetDeleted = false, code = failure.Error.Code });
                    return Error(StatusCodes.Status502BadGateway, "remove.dataset_failed",
                        $"The plant's tables and registration were removed, but its Dataset {dataset} could not be deleted: {failure.Message} Delete it in Historian → Datasets.");
                }
                services.Log.Write("plant.removed", name, new { droppedTables = dropped, dataset, datasetDeleted = true });
                return Results.Json(new { simulation = name, droppedTables = dropped, datasetDeleted = true,
                    message = $"Tables, registration and the Dataset {dataset} with all its tags were removed." }, Json);
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

        // The Gage kit of a built plant: prompts, and the Connect and Atlas briefs to hand Gage.
        app.MapGet("/api/plants/{name}/gage/{kind}", (string name, string kind, string? format) => Download(services, name, model =>
            GageKit(kind, format, services, FindInterview(services, name)?.NeedsJson, model, services.Registry.Find(name)?.Dataset ?? services.Dataset)));

        app.MapGet("/api/plants/{name}/demo-guide", (string name, string? format) => Download(services, name, model =>
        {
            var interview = services.Interviews.ApprovedSimulations().Contains(name) ? FindInterview(services, name) : null;
            string? dataset = services.Registry.Find(name)?.Dataset ?? services.Dataset;
            var guide = DemoGuide.Build(services.DemoGuideTemplate, services.Library, interview?.NeedsJson, model, dataset, services.Clock());
            return format == "html" ? Results.Text(guide.Html, "text/html; charset=utf-8") : Results.Text(guide.Markdown, "text/markdown; charset=utf-8");
        }));
    }

    /// <summary>Stop, start and remove are only for plants approved in a web interview (flowsim_interviews.approved_simulation).</summary>
    /// <summary>
    /// Replaces a running plant with this interview's new draft, under the same
    /// name: stop it, empty its own Dataset (delete and create it again, same
    /// name and purge age), drop its tables, admit the new model with the anchor
    /// fixed now, and start it. Connect and Atlas setups that use its tables and
    /// tags keep working; the old data is gone. Each step that can fail before
    /// the tables are dropped leaves the old plant as it was and restarts it.
    /// </summary>
    private static async Task<IResult> Rebuild(Guid id, InterviewRecord record, string existing, JsonObject needs, string draftHash,
        WebServices services, PlantHostingService hosting)
    {
        if (Ownership(services, existing) is { } refused) return refused;
        var now = services.Clock();
        var anchor = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var loaded = PlantModelLoader.Load(record.DraftModelJson!, anchor);
        if (!loaded.IsValid)
            return Results.Json(new
            {
                error = new { code = "approve.draft_invalid", message = "The draft no longer validates at the current time. Ask the interviewer to compose and preview it again." },
                errors = loaded.Errors.Take(10)
            }, Json, statusCode: StatusCodes.Status409Conflict);
        var model = loaded.Model!;
        if (model.Simulation != existing)
            return Error(StatusCodes.Status409Conflict, "approve.name_changed", $"The new draft is named {model.Simulation}, but this interview changes {existing}. Ask the interviewer to compose it again as {existing}.");

        var entry = services.Registry.Find(existing);
        bool wasEnabled = entry?.HostingEnabled != false;
        await hosting.StopPlant(existing);
        string? dataset = entry?.DatasetCreated == true && services.Datasets is not null ? entry.Dataset : null;
        if (dataset is not null)
        {
            try
            {
                await services.Datasets!.Delete(dataset, CancellationToken.None);
                await services.Datasets.Create(dataset, CancellationToken.None);
            }
            catch (RuntimeFailure failure)
            {
                if (wasEnabled) hosting.StartPlant(existing);
                services.Log.Write("plant.rebuild_failed", existing, new { step = "dataset", code = failure.Error.Code });
                return Error(StatusCodes.Status502BadGateway, "approve.dataset_failed",
                    $"{existing} was not rebuilt: its Dataset {dataset} could not be emptied ({failure.Message}). Its tables are unchanged. Check Historian → Datasets, then approve again.");
            }
        }
        int dropped = services.Plants.Remove(existing);
        hosting.Forget(existing);
        PlantRegistration registration;
        try { registration = services.Plants.Admit(model, record.DraftModelJson!); }
        catch (RuntimeFailure failure)
        {
            services.Log.Write("plant.rebuild_failed", existing, new { step = "admit", code = failure.Error.Code, droppedTables = dropped });
            return Error(StatusCodes.Status500InternalServerError, "approve.rebuild_failed",
                $"{existing}'s old tables were removed, but the new plan could not be admitted: {failure.Message} Ask the interviewer to compose and preview the change again, then approve.");
        }
        services.Registry.Add(existing, id, dataset ?? entry?.Dataset ?? services.Dataset, datasetCreated: dataset is not null);
        services.Registry.SetHostingEnabled(existing, true);
        hosting.StartPlant(existing);
        needs[ApprovedDraftKey] = draftHash;
        needs[InterviewSession.ChangeBriefedKey] = false;
        services.Interviews.SaveState(id, needs.ToJsonString(), record.DraftModelJson, record.PreviewJson);
        services.Interviews.SetStatus(id, InterviewStatus.Approved);
        services.Log.Write("plant.rebuilt", existing, new { interviewId = id, anchorUtc = registration.AnchorUtc, startUtc = registration.StartUtc, droppedTables = dropped, datasetEmptied = dataset is not null });
        return Results.Json(new
        {
            simulation = existing, replaced = true,
            tablePrefix = model.TablePrefix, tagRoot = model.TagRoot,
            anchorUtc = registration.AnchorUtc, startUtc = registration.StartUtc,
            dataset = dataset ?? entry?.Dataset ?? services.Dataset, datasetEmptied = dataset is not null,
            tables = PlantSchema.TablesFor(model).Select(t => PlantSchema.PhysicalName(model, t.Purpose)).ToList(),
            message = $"{existing} was rebuilt from the new plan: its old tables{(dataset is null ? "" : " and the tag history in " + dataset)} were deleted, and it is backfilling, then runs live."
        }, Json, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>One part of the Gage kit, as a file to save: prompts (Markdown, or JSON with format=json), or the Connect or Atlas brief.</summary>
    private static IResult GageKit(string kind, string? format, WebServices services, string? needsJson, PlantModel model, string? dataset)
    {
        string File(string suffix) => $"{model.Simulation}-{suffix}.md";
        IResult Markdown(string text, string suffix) => Results.File(System.Text.Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", File(suffix));
        switch (kind)
        {
            case "prompts":
                var prompts = DemoGuide.GagePrompts(services.Library, needsJson, model, dataset);
                return format == "json" ? Results.Json(prompts, Json) : Markdown(DemoGuide.GagePromptsMarkdown(prompts, model), "gage-prompts");
            case "connect":
                return Markdown(DemoGuide.GageConnectBrief(services.Library, needsJson, model, dataset), "gage-connect");
            case "atlas":
                return Markdown(DemoGuide.GageAtlasBrief(services.Library, needsJson, model, dataset), "gage-atlas");
            default:
                return Error(StatusCodes.Status404NotFound, "gage.unknown_kit", "The Gage kit has prompts, connect and atlas.");
        }
    }

    /// <summary>
    /// The plant browser: read-only views of the plants this page built. A plant
    /// the page did not build answers 404 browse.not_found, the same as a name
    /// that does not exist. Reads are not logged.
    /// </summary>
    private static void MapBrowse(WebApplication app, WebServices services, PlantHostingService hosting)
    {
        var browser = new PlantBrowser(services, hosting);
        IResult Run(Func<object> read)
        {
            try { return Results.Json(read(), Json); }
            catch (BrowseRefusal refusal) { return Error(refusal.Status, refusal.Code, refusal.Message); }
            catch (RuntimeFailure failure) { return Failure(failure, StatusCodes.Status503ServiceUnavailable); }
        }
        app.MapGet("/api/browse/{name}", (string name) => Run(() => browser.Summary(name)));
        app.MapGet("/api/browse/{name}/tables", (string name) => Run(() => browser.Tables(name)));
        app.MapGet("/api/browse/{name}/tables/{table}/rows", (string name, string table, string? sort, string? dir, string? q, bool? deleted, int? page) =>
            Run(() => browser.Rows(name, table, sort, dir, q, deleted == true, page ?? 1)));
    }

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
    internal static InterviewRecord? FindInterview(WebServices services, string simulation)
    {
        var entry = services.Registry.List().FirstOrDefault(e => e.Simulation == simulation);
        return entry is null || entry.InterviewId == Guid.Empty ? null : services.Interviews.Load(entry.InterviewId);
    }
}
