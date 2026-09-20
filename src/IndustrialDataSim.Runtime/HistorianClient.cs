using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Runtime;

public sealed record ProductionPreflight(DatasetSettings Settings, IReadOnlyDictionary<string, long?> BaselineTicks);
public sealed record DatasetSettings(int PurgeAgeDays, int PurgeSizeGb, int? LateToleranceMs, int? LateAgeDays);
internal sealed record ObservedPoint(DateTimeOffset Timestamp, JsonElement Value, int Quality);
internal sealed record TagRead(string Name, string? Type, IReadOnlyList<ObservedPoint> Points);

/// <summary>Bounded HTTP operations with no automatic write retry or response-body logging.</summary>
public sealed class HistorianClient : IDisposable
{
    internal HistorianConnection Connection { get; }
    private readonly HttpClient http;
    private string? token;
    private DateTimeOffset renewAt;
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal HistorianClient(HistorianConnection connection, HttpClient http)
    { connection.Validate(); Connection = connection; this.http = http; }
    public HistorianClient(HistorianConnection connection) : this(connection, connection.CreateClient()) { }

    internal async Task Authenticate(CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        stop = deadline.Token;
        if (token is not null && UtcNow() < renewAt) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Connection.Pulse, "auth/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"] = "client_credentials", ["client_id"] = Connection.ClientId,
                ["client_secret"] = Connection.ClientSecret, ["audience"] = Connection.Audience })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        RequireOk(response, "authentication");
        using var json = await ReadJson(response, 64 * 1024, stop);
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || !root.TryGetProperty("expires_in", out var expiry) ||
            !expiry.TryGetInt32(out int seconds) || seconds < 1 || seconds > 604800)
            throw ProtocolFailure();
        token = value.GetString();
        renewAt = UtcNow().AddSeconds(seconds - Math.Min(60, seconds / 2.0));
    }

    internal async Task<ProductionPreflight> Preflight(SimulationDefinition model, CancellationToken stop = default)
    {
        ValidateProfile(model);
        await Authenticate(stop);
        string dataset = DatasetPath(model.Session.Dataset);
        using var exists = await GetJson(dataset + "/exists", stop);
        if (exists.RootElement.ValueKind != JsonValueKind.True)
            throw new RuntimeFailure("historian.dataset_missing", "Dataset existence was not confirmed. Check the configured Dataset and permissions; no session was admitted or written.");
        using var settings = await GetJson(dataset, stop);
        var root = settings.RootElement;
        int Number(string name) => root.TryGetProperty(name, out var element) && element.TryGetInt32(out int number) && number >= 0 ? number : throw ProtocolFailure();
        var result = new DatasetSettings(Number("pa"), Number("ps"), root.TryGetProperty("ldt", out _) ? Number("ldt") : null,
            root.TryGetProperty("lda", out _) ? Number("lda") : null);
        if (result.PurgeAgeDays > 0 && model.Session.StartUtc < DateTimeOffset.UtcNow.AddDays(-result.PurgeAgeDays))
            throw new RuntimeFailure("historian.retention_conflict", "The proposed range begins outside the Dataset purge-age window. Choose a retained range or review Dataset settings; timestamps will not be shifted automatically.");
        var current = await Read(model.Session.Dataset, model.Session.OutputTags.Select(t => t.Name), null, null, stop);
        foreach (var tag in current)
        {
            string expectedType = model.Session.OutputTags.Single(output => output.Name == tag.Name).ValueType switch
            { "number" => "System.Double", "boolean" => "System.Boolean", "string" => "System.String", _ => "unsupported" };
            if (tag.Type is not null && tag.Type != expectedType)
                throw new RuntimeFailure("historian.tag_type", "An existing tag type differs from the model's output type. Use a compatible model or fresh tags; the adapter will not silently cast or change server tag types.");
            if (tag.Points.Any(point => point.Timestamp >= model.Session.StartUtc))
                throw new RuntimeFailure("historian.backward_range", "An output tag already has a timestamp at or after the proposed start. Choose a later range or fresh tags; no historical insert or replacement is allowed.");
        }
        var baseline = model.Session.OutputTags.ToDictionary(tag => tag.Name, tag =>
            current.SingleOrDefault(item => item.Name == tag.Name)?.Points.Select(point => (long?)point.Timestamp.UtcTicks).Max(), StringComparer.Ordinal);
        return new(result, baseline);
    }

    internal void ValidateProfile(SimulationDefinition model)
    {
        if (!string.Equals(model.Session.ConnectionProfile, Connection.Profile, StringComparison.Ordinal))
            throw new RuntimeFailure("connection.profile_mismatch", "Model connectionProfile does not match TIMEBASE_PROFILE. Load the intended deployment profile; do not redirect an existing session to another Historian.");
        if (model.Session.OutputTags.Count > 32)
            throw new RuntimeFailure("historian.tag_limit", "This first production adapter supports at most 32 output tags per session. Split larger models into sessions with disjoint tags.");
    }

    internal async Task Publish(string dataset, string payload, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        stop = deadline.Token;
        // Authenticate before durable claim; never acquire another token or retry
        // the write here. Any response other than documented HTTP 200 is uncertain.
        using var request = Request(HttpMethod.Post, DatasetPath(dataset) + "/data");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        RequireOk(response, "publish");
        // The documented successful response is empty. Drain a bounded body so
        // an interrupted response cannot be mistaken for normal completion.
        byte[] body = await ReadBytes(response, 64 * 1024, stop);
        if (body.Any(value => value is not (9 or 10 or 13 or 32))) throw ProtocolFailure();
    }

    internal async Task<IReadOnlyList<TagRead>> Read(string dataset, IEnumerable<string> tags,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken stop)
    {
        await Authenticate(stop);
        string[] requested = tags.ToArray();
        // This installation returns 404 for an all-new tag query. Establish
        // absence from a successful bounded inventory read, never from 404 alone.
        using var inventory = await GetJson(DatasetPath(dataset) + "/tags", stop);
        var inventoryRoot = inventory.RootElement;
        IEnumerable<JsonElement> entries = inventoryRoot.ValueKind == JsonValueKind.Array
            ? inventoryRoot.EnumerateArray()
            : inventoryRoot.GetProperty("User").EnumerateArray().Concat(inventoryRoot.GetProperty("System").EnumerateArray());
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string name = entry.GetProperty("n").GetString() ?? throw ProtocolFailure();
            if (requested.Contains(name, StringComparer.Ordinal)) existing.Add(name);
            else if (requested.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new RuntimeFailure("historian.tag_alias", "A requested tag differs only by case from an existing tag. Use its exact existing name or fresh disjoint names; aliases cannot bypass ownership or ordering.");
        }
        string[] names = requested.Where(existing.Contains).ToArray();
        if (names.Length == 0) return Array.Empty<TagRead>();
        string query = string.Join("&", names.Select(name => "tagname=" + Uri.EscapeDataString(name)));
        if (start is not null) query += "&start=" + Uri.EscapeDataString(start.Value.ToString("O")) + "&end=" + Uri.EscapeDataString(end!.Value.ToString("O"));
        if (query.Length > 12000) throw new RuntimeFailure("historian.query_limit", "Encoded tag query exceeds 12000 characters. Use fewer or shorter tag names per session; no write was retried.");
        using var json = await GetJson(DatasetPath(dataset) + "/data?" + query, stop);
        if (!json.RootElement.TryGetProperty("tl", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > names.Length) throw ProtocolFailure();
        var result = new List<TagRead>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            var metadata = entry.GetProperty("t");
            string name = metadata.GetProperty("n").GetString() ?? throw ProtocolFailure();
            if (!names.Contains(name, StringComparer.Ordinal) || !seen.Add(name)) throw ProtocolFailure();
            var points = new List<ObservedPoint>();
            foreach (var point in entry.GetProperty("d").EnumerateArray())
            {
                if (points.Count >= 20000) throw ProtocolFailure();
                var timestamp = point.GetProperty("t").GetDateTimeOffset();
                var value = point.TryGetProperty("v", out var v) ? v.Clone() : JsonSerializer.SerializeToElement<object?>(null);
                int quality = point.GetProperty("q").GetInt32();
                if (timestamp == DateTimeOffset.MinValue && value.ValueKind == JsonValueKind.Null && quality == 0)
                    continue; // Documented empty-query placeholder, never arrival evidence.
                points.Add(new(timestamp, value, quality));
            }
            result.Add(new(name, metadata.TryGetProperty("t", out var type) ? type.GetString() : null, points));
        }
        return result;
    }

    private async Task<JsonDocument> GetJson(string path, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        stop = deadline.Token;
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        RequireOk(response, "read");
        return await ReadJson(response, 4 * 1024 * 1024, stop);
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Connection.Historian, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private static string DatasetPath(string dataset) => "api/datasets/" + Uri.EscapeDataString(dataset);
    private static void RequireOk(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new RuntimeFailure("historian.http_status", $"Historian {operation} returned HTTP {(int)response.StatusCode}. Check service availability, authentication and permissions. Inspect durable state before further writes; no request is automatically retried.");
    }
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int limit, CancellationToken stop) =>
        JsonDocument.Parse(await ReadBytes(response, limit, stop), new JsonDocumentOptions { MaxDepth = 32 });
    private static async Task<byte[]> ReadBytes(HttpResponseMessage response, int limit, CancellationToken stop)
    {
        using var stream = await response.Content.ReadAsStreamAsync(stop);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, stop);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new RuntimeFailure("historian.response_limit", "Historian response exceeded the bounded read limit. Reduce the session tag count or observation window; inspect publish state and do not replay.");
            output.Write(buffer, 0, count);
        }
    }
    internal static RuntimeFailure ProtocolFailure() => new("historian.invalid_response", "Historian or Pulse response does not match the expected API shape. Check service/API compatibility; preserve state and do not retry an ambiguous publish.");
    public void Dispose() { token = null; http.Dispose(); }
}
