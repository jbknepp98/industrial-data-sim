using System.Net;
using System.Text.Json;

namespace IndustrialDataSim.Runtime;

/// <summary>Why Pulse did not issue a usable token. Callers turn this into their own actionable error.</summary>
internal enum PulseTokenProblem { HttpStatus, ResponseTooLarge, InvalidResponse }

/// <summary>
/// Raised by <see cref="PulseTokenSource"/>. It deliberately carries no response
/// body: token responses can echo client details, and callers must never log them.
/// </summary>
internal sealed class PulseTokenException(PulseTokenProblem problem, HttpStatusCode? status = null)
    : Exception("Pulse did not issue a usable access token.")
{
    public PulseTokenProblem Problem { get; } = problem;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Client-credentials access tokens from Pulse (<c>POST /auth/token</c>), cached
/// until shortly before they expire. Shared by the Historian writer and the Gage
/// client so both authenticate the same way. The token is never logged or put in
/// an error message.
/// </summary>
internal sealed class PulseTokenSource
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient http;
    private readonly Uri pulse;
    private readonly string clientId;
    private readonly string clientSecret;
    private readonly string? audience;
    private string? token;
    private DateTimeOffset renewAt;

    /// <param name="audience">Sent as the token request's <c>audience</c>, or omitted when null. Pulse
    /// 2.0.0 issues the client's registered audiences either way.</param>
    internal PulseTokenSource(HttpClient http, Uri pulse, string clientId, string clientSecret, string? audience)
    {
        this.http = http;
        this.pulse = pulse;
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.audience = audience;
    }

    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The cached token, or null before the first request or after <see cref="Invalidate"/>.</summary>
    internal string? Current => token;

    /// <summary>Forget the cached token, for example after a service rejected it before its advertised expiry.</summary>
    internal void Invalidate() => token = null;

    /// <summary>Returns the cached token, or requests a new one when there is none or it is due for renewal.</summary>
    /// <exception cref="PulseTokenException">Pulse refused the request or answered with an unexpected shape.</exception>
    internal async Task<string> GetToken(CancellationToken stop)
    {
        if (token is not null && UtcNow() < renewAt) return token;
        // Count token lifetime from request start, not response completion. A
        // slow token endpoint must not extend our estimate of server validity.
        var requestedAt = UtcNow();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = clientSecret
        };
        if (audience is not null) form["audience"] = audience;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(pulse, "auth/token")) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode != HttpStatusCode.OK) throw new PulseTokenException(PulseTokenProblem.HttpStatus, response.StatusCode);
        byte[] body = await ReadBounded(response, stop);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()) || !root.TryGetProperty("expires_in", out var expiry) ||
                !expiry.TryGetInt32(out int seconds) || seconds < 1 || seconds > 604800)
                throw new PulseTokenException(PulseTokenProblem.InvalidResponse);
            token = value.GetString();
            // Renew a minute early, or halfway through a very short lifetime.
            renewAt = requestedAt.AddSeconds(seconds - Math.Min(60, seconds / 2.0));
            return token!;
        }
        catch (JsonException) { throw new PulseTokenException(PulseTokenProblem.InvalidResponse); }
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken stop)
    {
        using var stream = await response.Content.ReadAsStreamAsync(stop);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, stop);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxResponseBytes) throw new PulseTokenException(PulseTokenProblem.ResponseTooLarge);
            output.Write(buffer, 0, count);
        }
    }
}
