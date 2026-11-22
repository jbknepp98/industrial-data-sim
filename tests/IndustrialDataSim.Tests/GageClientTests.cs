using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Tests;

/// <summary>
/// The Gage client against a fake Pulse: request shape, every "explain and stop"
/// diagnostic, token caching, and that no secret or token reaches an error.
/// The live check runs only when FLOWSIM_TEST_GAGE=1 and the TIMEBASE_* Pulse
/// client settings are in the environment.
/// </summary>
public class GageClientTests
{
    private const string Secret = "PRIVATE_SECRET_VALUE";

    private static GageSettings Settings(string model = GageSettings.DefaultModel) => new()
    {
        Pulse = new("https://pulse.invalid:4542/"), ClientId = "DataSim", ClientSecret = Secret, Model = model
    };

    /// <summary>A fake Pulse: token endpoint, model catalog and a streaming Messages proxy.</summary>
    internal sealed class FakePulse : HttpMessageHandler
    {
        public int Tokens, CatalogReads, MessageCalls;
        public HttpStatusCode TokenStatus = HttpStatusCode.OK;
        public int TokenSeconds = 3600;
        public string Catalog = """{"models":[{"handle":"anthropic/claude","displayName":"Claude","wire":0},{"handle":"openai/gpt","displayName":"GPT","wire":1}],"default":"anthropic/claude","pulseLicense":{"state":0,"gageEntitled":true}}""";
        public Func<HttpResponseMessage>? CatalogFailure, MessageFailure;
        public bool Unreachable;
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> MessageBodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            if (Unreachable) throw new HttpRequestException("PRIVATE_TRANSPORT_DETAILS");
            Requests.Add(request);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/token")
            {
                Tokens++;
                string form = await request.Content!.ReadAsStringAsync(stop);
                Assert.Contains("client_secret=" + Secret, form);
                if (TokenStatus != HttpStatusCode.OK) return new(TokenStatus) { Content = new StringContent("PRIVATE_AUTH_DETAILS " + Secret) };
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = "PRIVATE_TOKEN_" + Tokens, expires_in = TokenSeconds }));
            }
            if (path == "/api/gage/models")
            {
                CatalogReads++;
                return CatalogFailure?.Invoke() ?? Json(HttpStatusCode.OK, Catalog);
            }
            if (path == "/api/gage/proxy/v1/messages")
            {
                MessageCalls++;
                MessageBodies.Add(await request.Content!.ReadAsStringAsync(stop));
                return MessageFailure?.Invoke() ?? new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") };
            }
            return new(HttpStatusCode.NotFound);
        }

        private const string Stream =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":21,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"I can hear \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"you clearly.\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<RuntimeFailure> CheckFails(FakePulse pulse, GageSettings? settings = null)
    {
        using var gage = new GageClient(settings ?? Settings(), pulse);
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => gage.Check());
        AssertNothingSecret(failure.Message);
        return failure;
    }

    private static void AssertNothingSecret(string text)
    {
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("PRIVATE", text);
    }

    [Fact]
    public async Task CheckRunsTheFullChainAndReportsModelReplyAndUsage()
    {
        var pulse = new FakePulse();
        using var gage = new GageClient(Settings(), pulse);
        var result = await gage.Check();
        Assert.Equal("anthropic/claude", result.Model);
        Assert.Equal("claude-sonnet-4-6", result.AnsweredBy);
        Assert.Equal("I can hear you clearly.", result.Reply);
        Assert.Equal(21, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(["anthropic/claude", "openai/gpt"], result.AvailableModels);
        Assert.Equal([1, 1, 1], new[] { pulse.Tokens, pulse.CatalogReads, pulse.MessageCalls });
    }

    [Fact]
    public async Task MessageRequestSendsHandleMaxTokensAndPulseTokenOnly()
    {
        var pulse = new FakePulse();
        using var gage = new GageClient(Settings(), pulse);
        var tool = new Tool
        {
            Name = "validate_model", Description = "Validate the draft plant model.",
            InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>(), Required = [] }
        };
        await gage.CreateMessage(new GageMessageRequest([new UserMessageParam("hello")], 900) { System = "You interview a Timebase user.", Tools = [tool] });

        var body = JsonNode.Parse(Assert.Single(pulse.MessageBodies))!.AsObject();
        Assert.Equal("anthropic/claude", (string?)body["model"]);
        Assert.Equal(900, (int?)body["max_tokens"]);
        Assert.True((bool?)body["stream"]);
        Assert.Equal("You interview a Timebase user.", (string?)body["system"]);
        Assert.Equal("validate_model", (string?)body["tools"]![0]!["name"]);
        Assert.False(body.ContainsKey("tool_choice"));

        var request = pulse.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/gage/proxy/v1/messages");
        Assert.Equal("https://pulse.invalid:4542/api/gage/proxy/v1/messages", request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "PRIVATE_TOKEN_1"), request.Headers.Authorization);
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.False(request.Headers.Contains("anthropic-beta"));
        Assert.True(request.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task MaxTokensIsAlwaysSentAndBounded()
    {
        using var gage = new GageClient(Settings(), new FakePulse());
        var failure = await Assert.ThrowsAsync<RuntimeFailure>(() => gage.CreateMessage(new GageMessageRequest([new UserMessageParam("hi")], 0)));
        Assert.Equal("gage.invalid_request", failure.Error.Code);
        Assert.Contains("MaxTokens", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task RejectedClientCredentialsNameThePulseClientsPage(HttpStatusCode status)
    {
        var failure = await CheckFails(new FakePulse { TokenStatus = status });
        Assert.Equal("gage.client_rejected", failure.Error.Code);
        Assert.Contains("Pulse → Clients", failure.Message);
        Assert.Contains("'DataSim'", failure.Message);
    }

    [Fact]
    public async Task TokenRejectedByGageExplainsTheAudience()
    {
        var pulse = new FakePulse
        {
            CatalogFailure = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.WwwAuthenticate.Add(new("Bearer", "error=\"invalid_token\", error_description=\"The audience '(null)' is invalid\""));
                return response;
            }
        };
        var failure = await CheckFails(pulse);
        Assert.Equal("gage.client_rejected", failure.Error.Code);
        Assert.Contains("The audience '(null)' is invalid", failure.Message);
        Assert.Contains("allowed audiences", failure.Message);
    }

    [Fact]
    public async Task EmptyCatalogAsksForAProvider()
    {
        var failure = await CheckFails(new FakePulse { Catalog = """{"models":[],"default":null}""" });
        Assert.Equal("gage.no_models", failure.Error.Code);
        Assert.Contains("Pulse → Gage → Providers", failure.Message);
    }

    [Fact]
    public async Task MissingHandleListsTheAvailableOnes()
    {
        var pulse = new FakePulse();
        var failure = await CheckFails(pulse, Settings("nope/x"));
        Assert.Equal("gage.model_missing", failure.Error.Code);
        Assert.Contains("'nope/x'", failure.Message);
        Assert.Contains("anthropic/claude, openai/gpt", failure.Message);
        Assert.Contains("FLOWSIM_GAGE_MODEL", failure.Message);
        Assert.Equal(0, pulse.MessageCalls);
    }

    [Fact]
    public async Task UnlicensedGageSaysSo()
    {
        var failure = await CheckFails(new FakePulse { Catalog = """{"models":[{"handle":"anthropic/claude"}],"pulseLicense":{"gageEntitled":false}}""" });
        Assert.Equal("gage.not_licensed", failure.Error.Code);
    }

    [Fact]
    public async Task ProviderRejectionWithoutBearerChallengeMeansNoKey()
    {
        var pulse = new FakePulse { MessageFailure = () => Json(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""") };
        var failure = await CheckFails(pulse);
        Assert.Equal("gage.no_key", failure.Error.Code);
        Assert.Contains("Possible cause", failure.Message);
        Assert.Contains("Pulse → Gage → Providers", failure.Message);
        Assert.Contains("invalid x-api-key", failure.Message);
    }

    [Fact]
    public async Task ServerErrorNamingAMissingKeyMeansNoKey()
    {
        var pulse = new FakePulse { MessageFailure = () => Json(HttpStatusCode.BadGateway, """{"title":"Provider 'anthropic' has no API key configured."}""") };
        var failure = await CheckFails(pulse);
        Assert.Equal("gage.no_key", failure.Error.Code);
    }

    [Fact]
    public async Task ForbiddenAsksTheAdministratorToAllowTheClient()
    {
        var pulse = new FakePulse { MessageFailure = () => Json(HttpStatusCode.Forbidden, """{"detail":"Client policy blocks this request."}""") };
        var failure = await CheckFails(pulse);
        Assert.Equal("gage.blocked", failure.Error.Code);
        Assert.Contains("Client policy blocks this request.", failure.Message);
        Assert.Contains("Pulse → Policy → Client", failure.Message);
    }

    [Fact]
    public async Task ThrottleIncludesThePolicyWindowAndIsNotRetried()
    {
        var pulse = new FakePulse
        {
            MessageFailure = () =>
            {
                var response = Json(HttpStatusCode.TooManyRequests, """{"detail":"Weekly token limit reached; the window resets Monday 00:00 UTC."}""");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            }
        };
        var failure = await CheckFails(pulse);
        Assert.Equal("gage.throttled", failure.Error.Code);
        Assert.Contains("wait 30 seconds", failure.Message);
        Assert.Contains("window resets Monday", failure.Message);
        Assert.Equal(1, pulse.MessageCalls);
    }

    [Fact]
    public async Task UnreachablePulseNamesTheSettingsToCheck()
    {
        var failure = await CheckFails(new FakePulse { Unreachable = true });
        Assert.Equal("gage.pulse_unreachable", failure.Error.Code);
        Assert.Contains("TIMEBASE_PULSE_URL", failure.Message);
        Assert.Contains("TIMEBASE_CA_BUNDLE", failure.Message);
    }

    [Fact]
    public async Task TokenIsCachedAndRenewedBeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-11-21T00:00:00Z");
        var pulse = new FakePulse { TokenSeconds = 600 };
        using var gage = new GageClient(Settings(), pulse) { UtcNow = () => now };
        await gage.ListModels();
        await gage.ListModels();
        Assert.Equal(1, pulse.Tokens);
        now = now.AddSeconds(541); // renewal is due a minute before the 600-second expiry
        await gage.ListModels();
        Assert.Equal(2, pulse.Tokens);
        var latest = pulse.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/gage/models");
        Assert.Equal("PRIVATE_TOKEN_2", latest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task RejectionTextIsSanitizedAndTokensAreRedacted()
    {
        string tokenLike = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9abcdefghijklmnop";
        var pulse = new FakePulse { MessageFailure = () => Json(HttpStatusCode.Forbidden, "{\"detail\":\"Denied\\u0007 for " + tokenLike + "\"}") };
        var failure = await CheckFails(pulse);
        Assert.DoesNotContain(tokenLike, failure.Message);
        Assert.Contains("[redacted]", failure.Message);
        Assert.False(failure.Message.Contains('\u0007')); // xunit's substring check ignores control characters
    }

    [Fact]
    public async Task GuardStripsUnlistedHeadersAndRefusesOtherCredentialsAndOrigins()
    {
        var inner = new FakePulse();
        using var http = new HttpClient(new GageRequestGuard(new Uri("https://pulse.invalid:4542/"), () => "PULSE_TOKEN") { InnerHandler = inner });

        var allowed = new HttpRequestMessage(HttpMethod.Get, "https://pulse.invalid:4542/api/gage/models");
        allowed.Headers.Authorization = new("Bearer", "PULSE_TOKEN");
        allowed.Headers.TryAddWithoutValidation("X-From-Environment", "leak");
        allowed.Headers.TryAddWithoutValidation("x-api-key", "sk-ambient");
        allowed.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using (await http.SendAsync(allowed)) { }
        var sent = Assert.Single(inner.Requests);
        Assert.False(sent.Headers.Contains("X-From-Environment"));
        Assert.False(sent.Headers.Contains("x-api-key"));
        Assert.True(sent.Headers.Contains("anthropic-version"));

        var wrongCredential = new HttpRequestMessage(HttpMethod.Get, "https://pulse.invalid:4542/api/gage/models");
        wrongCredential.Headers.Authorization = new("Bearer", "sk-ambient-anthropic-key");
        var refused = await Assert.ThrowsAsync<RuntimeFailure>(() => http.SendAsync(wrongCredential));
        Assert.Equal("gage.credential_guard", refused.Error.Code);
        Assert.DoesNotContain("sk-ambient", refused.Message);

        var otherOrigin = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/messages");
        otherOrigin.Headers.Authorization = new("Bearer", "PULSE_TOKEN");
        Assert.Equal("gage.origin_guard", (await Assert.ThrowsAsync<RuntimeFailure>(() => http.SendAsync(otherOrigin))).Error.Code);
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData("anthropic/claude", true)]
    [InlineData("nope/x", true)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("a/b/c", false)]
    public void ModelHandleMustBeProviderSlashModel(string handle, bool valid)
    {
        var settings = Settings(handle);
        if (valid) settings.Validate();
        else Assert.Equal("gage.invalid_model", Assert.Throws<RuntimeFailure>(settings.Validate).Error.Code);
    }

    [Fact]
    public async Task LiveCheckThroughPulse()
    {
        if (Environment.GetEnvironmentVariable("FLOWSIM_TEST_GAGE") != "1") return; // live stack only
        using var gage = new GageClient(GageSettings.FromEnvironment());
        var result = await gage.Check();
        Assert.Contains(result.Model, result.AvailableModels);
        Assert.False(string.IsNullOrWhiteSpace(result.Reply));
        Assert.True(result.OutputTokens > 0);
    }
}
