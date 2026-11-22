// Gage client: reaches Claude through Timebase Pulse's Gage proxy with the
// official Anthropic C# SDK, so the user enters no model key of their own. The
// provider and key the customer already approved for Gage are reused.
//
// SDK settings proven by the phase 5 spike (Anthropic 13.12.0, November 2026):
//
// - BaseUrl = "<pulse origin>/api/gage/proxy". The SDK appends "/v1/messages",
//   so requests land on Pulse's POST /api/gage/proxy/v1/messages.
// - AuthToken = the Pulse access token. The SDK sends it as
//   "Authorization: Bearer <token>", which is what Pulse expects.
// - AuthToken must be set explicitly on every client. With only ApiKey = null,
//   the SDK still picked up an ambient ANTHROPIC_AUTH_TOKEN, so a client is
//   built per call with the current Pulse token (tokens are renewed between
//   calls). GageRequestGuard also refuses any request whose Authorization is
//   not the Pulse token.
// - The SDK applies ANTHROPIC_CUSTOM_HEADERS from the environment, and no
//   option turns that off. GageRequestGuard strips every header outside a
//   small allowlist before a request leaves the process.
// - Headers the SDK sends: anthropic-version, User-Agent and X-Stainless-*.
//   No anthropic-beta header is sent: no beta features or Credentials option
//   are used, because Credentials adds an OAuth beta header.
// - HttpClient = our own client over the Pulse CA trust handler (the same TLS
//   policy as the Historian writer), with HttpClient.Timeout disabled so only
//   the SDK's own per-request timeout applies to long streamed replies.
// - MaxRetries = 0: failures are explained and stop. They are never silently
//   retried, and a 429 from a Throttle policy goes back to the user with the
//   policy window. OpenTelemetry = false keeps prompt content out of any
//   tracing the environment may enable.
//
// GET /api/gage/models is not part of the Anthropic API, so it is a plain
// bounded HttpClient call with the same token and guard.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace IndustrialDataSim.Runtime;

/// <summary>Deployment settings for the Gage client. Never serialize this object: it holds the client secret.</summary>
public sealed class GageSettings
{
    public const string DefaultModel = "anthropic/claude";
    public required Uri Pulse { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string? CaFile { get; init; }
    /// <summary>The Gage handle (<c>providerId/logicalModelId</c>) sent as the request's <c>model</c>.</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>
    /// Reads TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET,
    /// TIMEBASE_CA_BUNDLE and FLOWSIM_GAGE_MODEL (default <c>anthropic/claude</c>),
    /// the same Pulse client settings the Historian writer uses.
    /// </summary>
    public static GageSettings FromEnvironment()
    {
        string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value :
            throw new RuntimeFailure("gage.missing_setting", $"Set {key} in the process environment. The Gage client uses the same Pulse client settings as the Historian writer (TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET, TIMEBASE_CA_BUNDLE).");
        if (!Uri.TryCreate(Required("TIMEBASE_PULSE_URL"), UriKind.Absolute, out var pulse))
            throw new RuntimeFailure("gage.invalid_origin", "TIMEBASE_PULSE_URL must be an HTTPS origin such as https://pulse:4542 with no path, query or embedded credentials.");
        var settings = new GageSettings
        {
            Pulse = pulse, ClientId = Required("TIMEBASE_CLIENT_ID"), ClientSecret = Required("TIMEBASE_CLIENT_SECRET"),
            CaFile = Environment.GetEnvironmentVariable("TIMEBASE_CA_BUNDLE"),
            Model = Environment.GetEnvironmentVariable("FLOWSIM_GAGE_MODEL") is { Length: > 0 } model ? model : DefaultModel
        };
        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (!Pulse.IsAbsoluteUri || Pulse.Scheme != "https" || Pulse.AbsolutePath != "/" ||
            Pulse.Query.Length != 0 || Pulse.Fragment.Length != 0 || Pulse.UserInfo.Length != 0)
            throw new RuntimeFailure("gage.invalid_origin", "TIMEBASE_PULSE_URL must be an HTTPS origin such as https://pulse:4542 with no path, query or embedded credentials. Do not disable TLS verification.");
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new RuntimeFailure("gage.missing_setting", "Supply the Pulse client ID and secret through TIMEBASE_CLIENT_ID and TIMEBASE_CLIENT_SECRET. Never put them in model files.");
        if (!Regex.IsMatch(Model, "^[A-Za-z0-9._-]{1,64}/[A-Za-z0-9._:-]{1,128}$"))
            throw new RuntimeFailure("gage.invalid_model", "FLOWSIM_GAGE_MODEL must be a Gage handle of the form providerId/logicalModelId, for example anthropic/claude. Unset FLOWSIM_GAGE_MODEL and run plant gage-check to see the handles Pulse offers.");
    }
}

/// <summary>The Gage model catalog as Pulse reports it to any authenticated client.</summary>
public sealed record GageModelCatalog(IReadOnlyList<string> Handles, string? Default, bool? GageEntitled);

/// <summary>One interview turn: everything the loop controls. The model handle comes from <see cref="GageSettings"/>.</summary>
public sealed record GageMessageRequest(IReadOnlyList<MessageParam> Messages, int MaxTokens)
{
    public string? System { get; init; }
    public IReadOnlyList<ToolUnion>? Tools { get; init; }
}

/// <summary>What <c>plant gage-check</c> reports after the full chain worked.</summary>
public sealed record GageCheckResult(string Model, string? AnsweredBy, string Reply, long InputTokens, long OutputTokens, IReadOnlyList<string> AvailableModels);

/// <summary>
/// Claude through the Pulse Gage proxy. Every failure becomes a
/// <see cref="RuntimeFailure"/> with a <c>gage.*</c> code and a message that
/// names the Pulse setting to fix ("explain and stop"). No call is retried.
/// </summary>
public sealed class GageClient : IDisposable
{
    private const int MaxCatalogBytes = 1024 * 1024;
    private const int MaxListedHandles = 20;
    private const int MaxReplyCharacters = 500;
    private static readonly TimeSpan PulseCallDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMinutes(10);

    private readonly GageSettings settings;
    private readonly HttpClient http;
    private readonly PulseTokenSource tokens;

    public GageClient(GageSettings settings) : this(settings, HistorianConnection.CreatePulseTrustHandler(settings.CaFile)) { }

    /// <param name="transport">The innermost handler; tests pass a fake server here.</param>
    internal GageClient(GageSettings settings, HttpMessageHandler transport)
    {
        settings.Validate();
        this.settings = settings;
        var guard = new GageRequestGuard(settings.Pulse, () => tokens!.Current) { InnerHandler = transport };
        http = new HttpClient(guard) { Timeout = Timeout.InfiniteTimeSpan };
        // Pulse 2.0.0 issues the client's registered audiences and ignores a
        // requested one, so the token request names none.
        tokens = new PulseTokenSource(http, settings.Pulse, settings.ClientId, settings.ClientSecret, audience: null);
    }

    public string Model => settings.Model;
    internal Func<DateTimeOffset> UtcNow { get => tokens.UtcNow; set => tokens.UtcNow = value; }

    /// <summary>Runs the full chain: token, model catalog, then a one-sentence test message.</summary>
    public async Task<GageCheckResult> Check(CancellationToken stop = default)
    {
        var catalog = await ListModels(stop);
        if (catalog.GageEntitled == false)
            throw new RuntimeFailure("gage.not_licensed", "Pulse reports that its licence does not include Gage. Check the licence in Pulse → Licence; the interview cannot reach a model until Gage is enabled.");
        if (catalog.Handles.Count == 0)
            throw new RuntimeFailure("gage.no_models", "Pulse's Gage model catalog is empty. Add or enable a provider and at least one model in Pulse → Gage → Providers, then rerun plant gage-check.");
        if (!catalog.Handles.Contains(settings.Model, StringComparer.Ordinal))
            throw new RuntimeFailure("gage.model_missing", $"The Gage handle '{settings.Model}' is not in Pulse's model catalog. Available handles: {ListHandles(catalog.Handles)}. Set FLOWSIM_GAGE_MODEL to one of them, or enable the model in Pulse → Gage → Providers.");
        var message = await CreateMessage(new GageMessageRequest(
            [new UserMessageParam("This is a connection test from the Flow Data Simulator. Reply with one short sentence.")], 60), stop);
        return new GageCheckResult(settings.Model, AnsweredBy(message), ReplyText(message), message.Usage.InputTokens, message.Usage.OutputTokens, catalog.Handles);
    }

    /// <summary>Reads <c>GET /api/gage/models</c>. Any authenticated Pulse client may call it.</summary>
    public async Task<GageModelCatalog> ListModels(CancellationToken stop = default)
    {
        var call = GageRequestGuard.BeginCall();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(PulseCallDeadline);
            string token = await Token(deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.Pulse, "api/gage/models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw Explain(call.Rejection, "the model catalog request");
            return ParseCatalog(await ReadBounded(response, MaxCatalogBytes, deadline.Token));
        }
        catch (Exception error) when (error is not RuntimeFailure) { throw Translate(error, call, stop, "the model catalog request"); }
    }

    /// <summary>
    /// One Messages API call through the proxy, streamed so long replies are
    /// not cut off by intermediate timeouts, and returned as the complete
    /// message. Always sends max_tokens (the proxy only fills a default), never
    /// a forced tool_choice, and no prefill: the upstream model may be older
    /// than the current Claude family.
    /// </summary>
    public async Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop = default)
    {
        if (request.MaxTokens is < 1 or > 128000)
            throw new RuntimeFailure("gage.invalid_request", "MaxTokens must be from 1 through 128000. The interview loop always sets it explicitly.");
        if (request.Messages.Count == 0)
            throw new RuntimeFailure("gage.invalid_request", "A Gage message request needs at least one message.");
        var call = GageRequestGuard.BeginCall();
        try
        {
            string token;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                deadline.CancelAfter(PulseCallDeadline);
                token = await Token(deadline.Token);
            }
            var client = new AnthropicClient(SdkOptions(token));
            return await client.Messages.CreateStreaming(BuildParameters(request), stop).Aggregate();
        }
        catch (Exception error) when (error is not RuntimeFailure) { throw Translate(error, call, stop, "the message request"); }
    }

    internal MessageCreateParams BuildParameters(GageMessageRequest request)
    {
        var parameters = new MessageCreateParams { Model = settings.Model, MaxTokens = request.MaxTokens, Messages = request.Messages.ToList() };
        if (request.System is not null) parameters = parameters with { System = request.System };
        if (request.Tools is { Count: > 0 }) parameters = parameters with { Tools = request.Tools.ToList() };
        return parameters;
    }

    private ClientOptions SdkOptions(string token) => new()
    {
        BaseUrl = new Uri(settings.Pulse, "api/gage/proxy").ToString(),
        AuthToken = token,
        HttpClient = http,
        MaxRetries = 0,
        Timeout = MessageTimeout,
        OpenTelemetry = false,
    };

    private async Task<string> Token(CancellationToken stop)
    {
        try { return await tokens.GetToken(stop); }
        catch (PulseTokenException failure) when (failure.Problem == PulseTokenProblem.HttpStatus &&
            failure.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new RuntimeFailure("gage.client_rejected", $"Pulse refused the client credentials for client '{settings.ClientId}' (HTTP {(int)failure.Status!.Value}). Create the client or fix its secret in Pulse → Clients, then update TIMEBASE_CLIENT_ID and TIMEBASE_CLIENT_SECRET.");
        }
        catch (PulseTokenException failure) when (failure.Problem == PulseTokenProblem.HttpStatus)
        {
            throw new RuntimeFailure("gage.http_status", $"The Pulse token request returned HTTP {(int)failure.Status!.Value}. Check that Pulse is running and healthy (its log names the cause), then rerun plant gage-check.");
        }
        catch (PulseTokenException)
        {
            throw new RuntimeFailure("gage.invalid_response", "Pulse's token response did not match the expected shape. Check that TIMEBASE_PULSE_URL points at Pulse itself and that Pulse is version 2.0.0 or later.");
        }
    }

    /// <summary>Turns a transport, timeout or SDK exception into an explained failure. Never includes raw exception text.</summary>
    private RuntimeFailure Translate(Exception error, GageCall call, CancellationToken stop, string operation)
    {
        if (error is OperationCanceledException && stop.IsCancellationRequested) throw error; // the caller stopped; not a failure to explain
        // The guard's own refusals can arrive wrapped by the SDK; report them as they are.
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is RuntimeFailure guardFailure) return guardFailure;
        if (call.Rejection is not null) return Explain(call.Rejection, operation);
        return error switch
        {
            HttpRequestException or AnthropicIOException => new RuntimeFailure("gage.pulse_unreachable",
                $"Could not reach Pulse at {settings.Pulse.GetLeftPart(UriPartial.Authority)} for {operation}. Check TIMEBASE_PULSE_URL, that this container is on the Timebase network, and that TIMEBASE_CA_BUNDLE holds the Pulse root CA."),
            OperationCanceledException => new RuntimeFailure("gage.timeout",
                $"Pulse did not finish {operation} in time. Check that Pulse and its model provider are responsive (Pulse → Gage → Usage shows recent calls), then retry."),
            AnthropicSseException => new RuntimeFailure("gage.stream_error",
                "The model's streamed reply ended with an error event, for example because the provider was overloaded. Nothing was saved from this turn; retry the turn."),
            _ => new RuntimeFailure("gage.invalid_response",
                $"Pulse's answer to {operation} did not match the Anthropic Messages API. Check that the Gage handle '{settings.Model}' belongs to a provider with the Anthropic wire format in Pulse → Gage → Providers.")
        };
    }

    /// <summary>Maps a proxy rejection to the specific Pulse setting the user has to change.</summary>
    private RuntimeFailure Explain(GageRejection? rejection, string operation)
    {
        if (rejection is null)
            return new RuntimeFailure("gage.http_status", $"Pulse rejected {operation} without a readable response. Check the Pulse log, then rerun plant gage-check.");
        string pulseSaid = rejection.Detail is null ? "" : $" Pulse said: \"{rejection.Detail}\".";
        int status = (int)rejection.Status;
        return rejection.Status switch
        {
            // Pulse's own JWT check answers with a Bearer challenge. A 401
            // without one comes from the upstream provider instead.
            HttpStatusCode.Unauthorized when rejection.BearerChallenge => new RuntimeFailure("gage.client_rejected",
                $"Pulse issued a token for client '{settings.ClientId}', but Gage did not accept it (HTTP 401).{pulseSaid} In Pulse → Clients → {settings.ClientId}, add Pulse to the client's allowed audiences, then rerun plant gage-check."),
            HttpStatusCode.Unauthorized => new RuntimeFailure("gage.no_key",
                $"The model provider behind '{settings.Model}' refused Pulse's credentials (HTTP 401).{pulseSaid} Possible cause: the provider has no API key or the key is invalid. Set the key in Pulse → Gage → Providers."),
            HttpStatusCode.Forbidden => new RuntimeFailure("gage.blocked",
                $"Pulse refused {operation} for client '{settings.ClientId}' (HTTP 403).{pulseSaid} Ask the Pulse administrator to allow this client and the model '{settings.Model}' in Pulse → Policy → Client (a Block policy or a model allow-list refuses calls)."),
            HttpStatusCode.TooManyRequests => new RuntimeFailure("gage.throttled",
                $"Pulse's Throttle policy for client '{settings.ClientId}' refused {operation} (HTTP 429).{RetryText(rejection.RetryAfter)}{pulseSaid} Retry after the policy window, or ask the Pulse administrator to raise the client's limits in Pulse → Policy → Client."),
            _ when status >= 500 && rejection.Detail is { } detail && Regex.IsMatch(detail, @"(?i)\b(api[ -]?key|key)\b") => new RuntimeFailure("gage.no_key",
                $"Pulse could not call the model provider behind '{settings.Model}' (HTTP {status}).{pulseSaid} Possible cause: the provider has no API key. Set the key in Pulse → Gage → Providers."),
            _ => new RuntimeFailure("gage.http_status",
                $"Pulse answered {operation} with HTTP {status}.{pulseSaid} Check the Gage provider and model in Pulse → Gage → Providers and the Pulse log, then rerun plant gage-check.")
        };
    }

    private static string RetryText(TimeSpan? retryAfter) => retryAfter is { } wait
        ? $" Pulse asks to wait {Math.Ceiling(wait.TotalSeconds):0} seconds."
        : " Pulse did not say how long to wait.";

    private static string ListHandles(IReadOnlyList<string> handles) =>
        string.Join(", ", handles.Take(MaxListedHandles)) + (handles.Count > MaxListedHandles ? $" and {handles.Count - MaxListedHandles} more" : "");

    private static GageModelCatalog ParseCatalog(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw CatalogFailure();
            var handles = new List<string>();
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var model in models.EnumerateArray())
                    if (model.ValueKind == JsonValueKind.Object && model.TryGetProperty("handle", out var handle) &&
                        handle.ValueKind == JsonValueKind.String && handle.GetString() is { Length: > 0 and <= 200 } text)
                        handles.Add(Sanitize(text, 200));
            string? defaultHandle = root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String ? Sanitize(d.GetString()!, 200) : null;
            bool? entitled = root.TryGetProperty("pulseLicense", out var licence) && licence.ValueKind == JsonValueKind.Object &&
                licence.TryGetProperty("gageEntitled", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
            return new GageModelCatalog(handles, defaultHandle, entitled);
        }
        catch (JsonException) { throw CatalogFailure(); }
        static RuntimeFailure CatalogFailure() => new("gage.invalid_response",
            "Pulse's model catalog (GET /api/gage/models) did not match the expected shape. Check that TIMEBASE_PULSE_URL points at Pulse 2.0.0 or later.");
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, int limit, CancellationToken stop)
    {
        using var stream = await response.Content.ReadAsStreamAsync(stop);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, stop);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit)
                throw new RuntimeFailure("gage.response_limit", $"Pulse's model catalog exceeded {limit} bytes. Check that TIMEBASE_PULSE_URL points at Pulse itself.");
            output.Write(buffer, 0, count);
        }
    }

    private static string? AnsweredBy(Message message)
    {
        string model = message.Model.Raw() ?? "";
        return model.Length == 0 ? null : Sanitize(model, 100);
    }

    private static string ReplyText(Message message)
    {
        var text = new StringBuilder();
        foreach (var block in message.Content)
            if (block.TryPickText(out var textBlock)) text.Append(textBlock.Text);
        return Sanitize(text.ToString(), MaxReplyCharacters);
    }

    /// <summary>
    /// Makes text from Pulse or the model safe to show: no control characters,
    /// bounded length, and anything shaped like a token or key redacted.
    /// </summary>
    internal static string Sanitize(string text, int limit)
    {
        string clean = Regex.Replace(text, @"[\p{C}\s]+", " ").Trim();
        clean = Regex.Replace(clean, @"[A-Za-z0-9+/=_\-\.]{32,}", "[redacted]");
        return clean.Length <= limit ? clean : clean[..limit] + "…";
    }

    public void Dispose()
    {
        tokens.Invalidate();
        http.Dispose();
    }
}

/// <summary>A non-success answer from Pulse, kept so the failure can be explained after the SDK throws.</summary>
internal sealed record GageRejection(HttpStatusCode Status, TimeSpan? RetryAfter, bool BearerChallenge, string? Detail);

/// <summary>Per-operation record that the guard fills in when Pulse rejects a request.</summary>
internal sealed class GageCall
{
    public GageRejection? Rejection { get; set; }
}

/// <summary>
/// The outermost handler of the Gage HttpClient. It keeps the Pulse token
/// inside Pulse and makes rejections explainable:
/// <list type="bullet">
/// <item>it refuses any request outside the configured Pulse origin;</item>
/// <item>it strips headers outside an allowlist (the SDK adds
/// ANTHROPIC_CUSTOM_HEADERS from the environment and cannot be told not to);</item>
/// <item>it refuses any non-token request whose Authorization is not the
/// current Pulse token, so an ambient Anthropic credential can never be sent;</item>
/// <item>on a non-success answer it records the status, Retry-After, whether
/// Pulse sent a Bearer challenge and a sanitized, bounded reason.</item>
/// </list>
/// </summary>
internal sealed class GageRequestGuard(Uri pulse, Func<string?> currentToken) : DelegatingHandler
{
    private const int MaxRejectionBytes = 16 * 1024;
    private static readonly AsyncLocal<GageCall?> CurrentCall = new();

    /// <summary>Starts recording rejections for one public operation on this async flow.</summary>
    internal static GageCall BeginCall()
    {
        var call = new GageCall();
        CurrentCall.Value = call;
        return call;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
    {
        var target = request.RequestUri;
        if (target is null || Uri.Compare(target, pulse, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new RuntimeFailure("gage.origin_guard", "The Gage client refused to send a request outside TIMEBASE_PULSE_URL. Unset ANTHROPIC_BASE_URL and similar overrides; Pulse tokens must only go to Pulse.");
        foreach (var name in request.Headers.Select(header => header.Key).ToList())
            if (!IsAllowedHeader(name)) request.Headers.Remove(name);
        bool tokenRequest = target.AbsolutePath == "/auth/token";
        if (!tokenRequest && (request.Headers.Authorization is not { Scheme: "Bearer" } authorization ||
            currentToken() is not { } expected || authorization.Parameter != expected))
            throw new RuntimeFailure("gage.credential_guard", "The Gage client refused to send a credential other than the Pulse access token. Unset ANTHROPIC_API_KEY and ANTHROPIC_AUTH_TOKEN in this environment and retry.");

        var response = await base.SendAsync(request, stop);
        if (!response.IsSuccessStatusCode && !tokenRequest && CurrentCall.Value is { } call)
        {
            byte[] body = await ReadBounded(response, stop);
            call.Rejection = new GageRejection(response.StatusCode, RetryAfter(response), HasBearerChallenge(response), Reason(response, body));
            // Give the SDK an equivalent, already-read body so its own error handling still works.
            var replacement = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = replacement;
        }
        return response;
    }

    private static bool IsAllowedHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Stainless-", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        return null;
    }

    private static bool HasBearerChallenge(HttpResponseMessage response) =>
        response.Headers.WwwAuthenticate.Any(challenge => challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The most specific human reason available: the Bearer challenge's
    /// error_description, an RFC 7807 detail or title, or an Anthropic-style
    /// error message. Sanitized and bounded; never the raw body.
    /// </summary>
    private static string? Reason(HttpResponseMessage response, byte[] body)
    {
        foreach (var challenge in response.Headers.WwwAuthenticate)
            if (challenge.Parameter is { } parameter && Regex.Match(parameter, "error_description=\"([^\"]*)\"") is { Success: true } match)
                return GageClient.Sanitize(match.Groups[1].Value, 300);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "detail", "title", "message" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    return GageClient.Sanitize(text, 300);
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text) return GageClient.Sanitize(text, 300);
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 } nested)
                    return GageClient.Sanitize(nested, 300);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken stop)
    {
        using var stream = await response.Content.ReadAsStreamAsync(stop);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (output.Length < MaxRejectionBytes)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxRejectionBytes - output.Length)), stop);
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
