using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Runtime;

/// <summary>
/// Write tallies returned by Historian 2.0.0 and later. Accepted counts points
/// stored, stored late or deduplicated; the three drop buckets are the server's
/// write-policy rejections. This is server evidence, not a per-point receipt.
/// </summary>
public sealed record WriteReceipt(int? Accepted, int ClockSkewDrops, int PastPurgeDrops, int PastLateAgeDrops)
{
    public long Dropped => (long)ClockSkewDrops + PastPurgeDrops + PastLateAgeDrops;
}

public sealed record ProductionPreflight(DatasetSettings Settings, IReadOnlyDictionary<string, long?> BaselineTicks);
public sealed record DatasetSettings(int PurgeAgeDays, int PurgeSizeGb, int? LateToleranceMs, int? LateAgeDays);
internal sealed record ObservedPoint(DateTimeOffset Timestamp, JsonElement Value, int Quality);
internal sealed record TagRead(string Name, string? Type, IReadOnlyList<ObservedPoint> Points);

/// <summary>Bounded HTTP operations with no automatic write retry or response-body logging.</summary>
/// <summary>One point of a tag read for display: time, raw JSON value, quality.</summary>
public sealed record TagPoint(DateTimeOffset Time, JsonElement Value, int Quality);
/// <summary>A tag's points in a read range, oldest first.</summary>
public sealed record TagSeries(string Tag, IReadOnlyList<TagPoint> Points);

public sealed class HistorianClient : IDisposable
{
    internal HistorianConnection Connection { get; }
    private readonly HttpClient http;
    private readonly PulseTokenSource tokens;
    /// <summary>The clock used for token renewal and retention checks; tests replace it.</summary>
    internal Func<DateTimeOffset> UtcNow { get => tokens.UtcNow; set => tokens.UtcNow = value; }
    internal HistorianClient(HistorianConnection connection, HttpClient http)
    {
        connection.Validate();
        Connection = connection;
        this.http = http;
        tokens = new PulseTokenSource(http, connection.Pulse, connection.ClientId, connection.ClientSecret, connection.Audience);
    }
    public HistorianClient(HistorianConnection connection) : this(connection, connection.CreateClient()) { }

    internal async Task Authenticate(CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { await tokens.GetToken(deadline.Token); }
        catch (PulseTokenException failure)
        {
            // Same codes and messages the Historian adapter has always returned.
            throw failure.Problem switch
            {
                PulseTokenProblem.HttpStatus => StatusFailure("authentication", failure.Status!.Value),
                PulseTokenProblem.ResponseTooLarge => ResponseLimitFailure(),
                _ => ProtocolFailure()
            };
        }
    }

    internal async Task<ProductionPreflight> Preflight(SimulationDefinition model, CancellationToken stop = default)
    {
        ValidateProfile(model);
        await Authenticate(stop);
        string dataset = DatasetPath(model.Session.Dataset);
        using var exists = await GetJson(dataset + "/exists", stop);
        if (exists.RootElement.ValueKind != JsonValueKind.True)
            throw new RuntimeFailure("historian.dataset_missing", "Dataset existence was not confirmed. Check the configured Dataset and permissions; no session was admitted or written.");
        var result = await ReadDatasetSettings(model.Session.Dataset, stop);
        ValidateRetention(result, model.Session.StartUtc);
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

    internal async Task<DatasetSettings> ReadDatasetSettings(string dataset, CancellationToken stop)
    {
        using var settings = await GetJson(DatasetPath(dataset), stop);
        var root = settings.RootElement;
        int Number(string name) => root.TryGetProperty(name, out var element) && element.TryGetInt32(out int number) && number >= 0 ? number : throw ProtocolFailure();
        return new DatasetSettings(Number("pa"), Number("ps"), root.TryGetProperty("ldt", out _) ? Number("ldt") : null,
            root.TryGetProperty("lda", out _) ? Number("lda") : null);
    }

    /// <summary>Whether a Dataset with exactly this name exists (<c>GET /api/datasets/{name}/exists</c>).</summary>
    public async Task<bool> DatasetExists(string dataset, CancellationToken stop)
    {
        using var exists = await GetJson(DatasetPath(dataset) + "/exists", stop);
        return exists.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw ProtocolFailure()
        };
    }

    /// <summary>
    /// Creates a Dataset with the given purge age (<c>POST /api/datasets</c>
    /// with <c>{"n", "pa"}</c>). Historian refuses a name that any Dataset
    /// already has, in any letter case; that becomes historian.dataset_exists,
    /// so a caller can never take over a Dataset it did not create. Like every
    /// POST here it is sent once and never retried.
    /// </summary>
    public async Task CreateDataset(string dataset, int purgeAgeDays, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        stop = deadline.Token;
        await Authenticate(stop);
        using var request = Request(HttpMethod.Post, "api/datasets");
        request.Content = new StringContent(JsonSerializer.Serialize(new { n = dataset, pa = purgeAgeDays }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode == HttpStatusCode.BadRequest && await ErrorKey(response, stop) == "Error.DatasetAlreadyExists")
            throw new RuntimeFailure("historian.dataset_exists",
                $"Historian already has a Dataset named {dataset} (names are compared ignoring case). Nothing was written to it. Choose another name, or delete that Dataset in Historian if it is no longer needed.");
        RequireOk(response, "Dataset create");
    }

    /// <summary>
    /// Deletes a Dataset and all its history (<c>DELETE /api/datasets/{name}</c>).
    /// Returns false when it was already gone. Callers delete only Datasets
    /// they created.
    /// </summary>
    public async Task<bool> DeleteDataset(string dataset, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        stop = deadline.Token;
        await Authenticate(stop);
        using var request = Request(HttpMethod.Delete, DatasetPath(dataset));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode == HttpStatusCode.NotFound && await ErrorKey(response, stop) == "Error.DatasetNotFound") return false;
        RequireOk(response, "Dataset delete");
        return true;
    }

    /// <summary>The <c>key</c> of a small Historian error body, or null. The body itself is never logged.</summary>
    private static async Task<string?> ErrorKey(HttpResponseMessage response, CancellationToken stop)
    {
        try
        {
            using var json = JsonDocument.Parse(await ReadBytes(response, 16 * 1024, stop), new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString() : null;
        }
        catch (Exception error) when (error is JsonException or RuntimeFailure) { return null; }
    }

    internal void ValidateRetention(DatasetSettings settings, DateTimeOffset earliestPendingUtc)
    {
        // Compare ages instead of subtracting an untrusted day count from the
        // clock, which could underflow DateTimeOffset for very large settings.
        if (settings.PurgeAgeDays > 0 && (UtcNow() - earliestPendingUtc).TotalDays > settings.PurgeAgeDays)
            throw new RuntimeFailure("historian.retention_conflict",
                "The proposed values begin outside the Dataset purge-age window. Review Dataset retention settings before retrying preflight; queued timestamps will not be shifted or discarded automatically.");
    }

    internal void ValidateProfile(SimulationDefinition model)
    {
        if (!string.Equals(model.Session.ConnectionProfile, Connection.Profile, StringComparison.Ordinal))
            throw new RuntimeFailure("connection.profile_mismatch", "Model connectionProfile does not match TIMEBASE_PROFILE. Load the intended deployment profile; do not redirect an existing session to another Historian.");
        if (model.Session.OutputTags.Count > 32)
            throw new RuntimeFailure("historian.tag_limit", "This first production adapter supports at most 32 output tags per session. Split larger models into sessions with disjoint tags.");
    }

    /// <returns>The server's write tallies, or null for an empty success body.</returns>
    internal async Task<WriteReceipt?> Publish(string dataset, string payload, CancellationToken stop)
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
        // Drain a bounded body so an interrupted response cannot be mistaken
        // for normal completion.
        byte[] body = await ReadBytes(response, 64 * 1024, stop);
        return ParseWriteReceipt(body);
    }

    /// <summary>
    /// Historian before 2.0.0 answered a successful write with an empty body;
    /// 2.0.0 returns accepted/dropped tallies. Both mean the write completed.
    /// Any other body is still an unexpected response, which the caller treats
    /// as an ambiguous publish (Uncertain), exactly as before.
    /// </summary>
    internal static WriteReceipt? ParseWriteReceipt(byte[] body)
    {
        if (body.All(value => value is 9 or 10 or 13 or 32)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            int? accepted = null;
            (int ClockSkew, int PastPurge, int PastLateAge) drops = (0, 0, 0);
            foreach (var member in root.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "a": accepted = Count(member.Value); break;
                    case "r": drops = Drops(member.Value); break;
                    case "t": ValidatePerTagResults(member.Value); break;
                    default: throw ProtocolFailure();
                }
            }
            return new WriteReceipt(accepted, drops.ClockSkew, drops.PastPurge, drops.PastLateAge);
        }
        catch (JsonException) { throw ProtocolFailure(); }

        static int Count(JsonElement element) =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) && number >= 0 ? number : throw ProtocolFailure();

        static (int, int, int) Drops(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            int clockSkew = 0, pastPurge = 0, pastLateAge = 0;
            foreach (var member in element.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "cs": clockSkew = Count(member.Value); break;
                    case "pp": pastPurge = Count(member.Value); break;
                    case "lda": pastLateAge = Count(member.Value); break;
                    default: throw ProtocolFailure();
                }
            }
            return (clockSkew, pastPurge, pastLateAge);
        }

        // Per-tag results repeat the totals; check their shape so an unrelated
        // body cannot pass as a receipt, but take counts from the totals.
        static void ValidatePerTagResults(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return;
            if (element.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            foreach (var tag in element.EnumerateObject())
            {
                if (tag.Value.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
                foreach (var member in tag.Value.EnumerateObject())
                {
                    if (member.Name == "a") Count(member.Value);
                    else if (member.Name == "r") Drops(member.Value);
                    else throw ProtocolFailure();
                }
            }
        }
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

    /// <summary>Largest tag-read answer accepted for the page's trends.</summary>
    public const int MaxTagReadBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Reads raw points for a few tags between startUtc and endUtc, for display
    /// (GET /api/datasets/{dataset}/data). Observed on Historian 2.0.0: the
    /// answer can begin with the last point before the range, which is kept as
    /// the value at startUtc (Historian stores changes only, so it is the value
    /// in force); the empty-range placeholder (year 1, quality 0, no value) is
    /// dropped. Tags Historian does not know, or that are missing from the
    /// answer, come back with no points. Up to 32 MB and 30 seconds; error
    /// bodies are never echoed and nothing is retried except one token refresh.
    /// </summary>
    public async Task<IReadOnlyList<TagSeries>> ReadTagData(string dataset, IReadOnlyList<string> tags,
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken stop)
    {
        string query = string.Join("&", tags.Select(t => "tagname=" + Uri.EscapeDataString(t))) +
                       "&start=" + Uri.EscapeDataString(startUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")) +
                       "&end=" + Uri.EscapeDataString(endUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        JsonDocument? json;
        try { json = await GetJson(DatasetPath(dataset) + "/data?" + query, stop, MaxTagReadBytes, TimeSpan.FromSeconds(30), allowNotFound: true); }
        catch (RuntimeFailure failure) when (failure.Error.Code == "historian.response_limit")
        {
            throw new RuntimeFailure("historian.read_too_large", "Historian returned more than 32 MB for this range. Choose a shorter time range or fewer tags.");
        }
        catch (JsonException)
        {
            throw ReadUnexpected();
        }
        var byTag = tags.ToDictionary(t => t, _ => new List<TagPoint>(), StringComparer.Ordinal);
        if (json is null) return tags.Select(t => new TagSeries(t, byTag[t])).ToList(); // 404: tags not written yet
        using (json)
        {
            try
            {
                if (!json.RootElement.TryGetProperty("tl", out var list) || list.ValueKind != JsonValueKind.Array) throw ReadUnexpected();
                foreach (var entry in list.EnumerateArray())
                {
                    string? name = entry.GetProperty("t").GetProperty("n").GetString();
                    if (name is null || !byTag.TryGetValue(name, out var points)) continue;
                    foreach (var point in entry.GetProperty("d").EnumerateArray())
                    {
                        var time = point.GetProperty("t").GetDateTimeOffset();
                        bool hasValue = point.TryGetProperty("v", out var value);
                        int quality = point.GetProperty("q").GetInt32();
                        if (time == DateTimeOffset.MinValue && !hasValue) continue; // empty-range placeholder
                        if (!hasValue) continue;
                        if (time < startUtc)
                        {
                            // The value in force when the range starts.
                            if (points.Count > 0 && points[^1].Time == startUtc) points.RemoveAt(points.Count - 1);
                            points.Add(new TagPoint(startUtc, value.Clone(), quality));
                            continue;
                        }
                        if (time > endUtc) continue;
                        points.Add(new TagPoint(time, value.Clone(), quality));
                    }
                }
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw ReadUnexpected();
            }
        }
        return tags.Select(t => new TagSeries(t, byTag[t])).ToList();
    }

    private static RuntimeFailure ReadUnexpected() =>
        new("historian.read_unexpected", "Historian answered the tag read in an unexpected format. Check that TIMEBASE_BASE_URL points at Historian 2.0.0.");

    private async Task<JsonDocument> GetJson(string path, CancellationToken stop) =>
        (await GetJson(path, stop, 4 * 1024 * 1024, TimeSpan.FromSeconds(20), allowNotFound: false))!;

    /// <returns>The parsed body, or null for HTTP 404 when allowNotFound.</returns>
    private async Task<JsonDocument?> GetJson(string path, CancellationToken stop, int limit, TimeSpan timeout, bool allowNotFound)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(timeout);
        stop = deadline.Token;
        // A token can be rejected before its advertised expiry, or expire
        // between related reads. Only this GET is eligible for one refresh and
        // retry. Both attempts and authentication share the original deadline.
        // Never move this policy into Publish: a POST failure remains uncertain.
        await Authenticate(stop);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                tokens.Invalidate();
                if (attempt == 1)
                    throw new RuntimeFailure("historian.http_status",
                        "Historian read returned HTTP 401 after one token refresh. Check that the Pulse client is enabled, its audience and Dataset permissions are correct, and the services trust the same issuer. No write was retried; inspect durable status before continuing.");
                // Release the rejected response before requesting another token.
                // Its body is not needed and must not appear in diagnostics.
                response.Dispose();
                await Authenticate(stop);
                continue;
            }
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            RequireOk(response, "read");
            return await ReadJson(response, limit, stop);
        }
        throw new InvalidOperationException("Bounded read attempts did not return or fail.");
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Connection.Historian, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Current);
        return request;
    }
    private static string DatasetPath(string dataset) => "api/datasets/" + Uri.EscapeDataString(dataset);
    private static void RequireOk(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode != HttpStatusCode.OK) throw StatusFailure(operation, response.StatusCode);
    }
    private static RuntimeFailure StatusFailure(string operation, HttpStatusCode status) =>
        new("historian.http_status", $"Historian {operation} returned HTTP {(int)status}. Check service availability, authentication and permissions. Inspect durable state before further writes; no write is automatically retried.");
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
            if (output.Length + count > limit) throw ResponseLimitFailure();
            output.Write(buffer, 0, count);
        }
    }
    private static RuntimeFailure ResponseLimitFailure() =>
        new("historian.response_limit", "Historian response exceeded the bounded read limit. Reduce the session tag count or observation window; inspect publish state and do not replay.");
    internal static RuntimeFailure ProtocolFailure() => new("historian.invalid_response", "Historian or Pulse response does not match the expected API shape. Check service/API compatibility; preserve state and do not retry an ambiguous publish.");
    public void Dispose() { tokens.Invalidate(); http.Dispose(); }
}
