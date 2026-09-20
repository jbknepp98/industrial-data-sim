using System.Text.Json;
using IndustrialDataSim.Core.Configuration;

namespace IndustrialDataSim.Runtime;

/// <summary>One ordered publisher. Ambiguous outcomes stop one session, never trigger replay.</summary>
public sealed class ProductionDelivery
{
    private readonly DurableRuntime runtime;
    private readonly HistorianClient historian;
    private int running;
    internal Action<string>? FaultPoint { get; set; }
    internal TimeSpan ObservationDelay { get; set; } = TimeSpan.FromSeconds(1);

    public ProductionDelivery(DurableRuntime runtime, HistorianClient historian)
    { runtime.RequireMode(ExecutionMode.Production); runtime.BindProduction(historian.Connection); this.runtime = runtime; this.historian = historian; }

    public async Task<DatasetSettings> AdmitAsync(string configuration, CancellationToken stop = default)
    {
        var loaded = SimulationDefinitionLoader.Load(configuration);
        if (!loaded.IsValid) throw new RuntimeFailure("runtime.invalid_configuration", "Model is invalid. Run validate-simulation and correct its field diagnostics before production admission.");
        try
        {
            var settings = await historian.Preflight(loaded.Definition!, stop);
            runtime.AdmitProduction(configuration, settings);
            return settings.Settings;
        }
        catch (Exception error) when (IsTransportFailure(error)) { throw SafeFailure(error); }
    }

    public async Task<bool> DeliverOneAsync(string id, CancellationToken stop = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new RuntimeFailure("delivery.concurrent_publish", "This publisher already has an operation in flight. Wait for it to finish; do not submit concurrent requests for the same queue.");
        try
        {
            stop.ThrowIfCancellationRequested();
            var batch = runtime.PeekPending(id);
            if (batch is null) return false;
            var model = runtime.ProductionModel(id);
            Dictionary<string, List<ObservedPoint>> expected;
            try
            {
                historian.ValidateProfile(model);
                expected = ParseExpected(batch.Payload!);
                // Read-before-write also catches obvious external-writer conflicts
                // after admission/restart. It cannot exclude a racing external writer.
                var latest = await historian.Read(model.Session.Dataset, expected.Keys, null, null, stop);
                foreach (var tag in latest)
                    if (tag.Points.Any(point => point.Timestamp >= expected[tag.Name][0].Timestamp))
                        throw new RuntimeFailure("historian.external_conflict", "A tag has reached this pending batch's time range. Stop external writers and inspect the timeline; queued values were not submitted and must not be shifted or replayed.");
                await historian.Authenticate(stop);
            }
            catch (Exception error) when (IsTransportFailure(error) || error is RuntimeFailure)
            {
                if (error is OperationCanceledException && stop.IsCancellationRequested) throw;
                runtime.ProductionFailed(id, SafeFailure(error));
                return false;
            }
            var work = runtime.Claim(id);
            if (work is null) return false;
            runtime.SaveObservation(new(batch.Id, "Pending", 0, 0, 0, null, "NotReviewed"));
            bool published = false;
            RuntimeFailure? publishFailure = null;
            try
            {
                await historian.Publish(work.Dataset, work.Batch.Payload!, stop);
                published = true;
            }
            catch (Exception error) when (IsTransportFailure(error) || error is RuntimeFailure)
            {
                publishFailure = SafeFailure(error);
                // Every failure after Sending is ambiguous, even a 401 or TLS
                // failure. Keep payload/reservations and never send another batch.
            }
            FaultPoint?.Invoke("after_publish_before_commit");
            runtime.Finish(work, published, publishFailure);
            if (!published)
            {
                runtime.SaveObservation(new(batch.Id, "Unavailable", 0, 0, 0, "delivery.uncertain", "NotReviewed"));
                return false;
            }
            // Publishing is already durable. Read failures below cannot turn a
            // Published batch back into Sending or authorize another POST.
            ArrivalObservation observation = new(batch.Id, "NotYetObserved", 0, 0, 0, null, "NotReviewed");
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) await Task.Delay(ObservationDelay, stop);
                    observation = await Observe(batch.Id, work.Dataset, expected, stop);
                    if (observation.Status is "Observed" or "ConsistentWithoutNewArrival" or "Mismatch") break;
                }
            }
            catch (Exception error) when (IsTransportFailure(error) || error is RuntimeFailure)
            { observation = new(batch.Id, "Unavailable", 0, 0, 0, SafeFailure(error).Error.Code, "NotReviewed"); }
            runtime.SaveObservation(observation);
            return true;
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task<ArrivalObservation> Observe(long batchId, string dataset,
        Dictionary<string, List<ObservedPoint>> expected, CancellationToken stop)
    {
        var first = expected.Values.Min(points => points[0].Timestamp);
        var last = expected.Values.Max(points => points[^1].Timestamp);
        var range = await historian.Read(dataset, expected.Keys, first, last, stop);
        var current = await historian.Read(dataset, expected.Keys, null, null, stop);
        int matching = 0, changed = 0, unchangedCompatible = 0;
        bool mismatch = false;
        foreach (var (name, expectedPoints) in expected)
        {
            var points = expectedPoints.ToDictionary(point => point.Timestamp);
            var matches = new List<ObservedPoint>();
            var tag = range.SingleOrDefault(item => item.Name == name);
            foreach (var actual in (tag?.Points ?? Array.Empty<ObservedPoint>()).Where(point => point.Timestamp >= first && point.Timestamp <= last))
                if (points.TryGetValue(actual.Timestamp, out var wanted))
                {
                    if (Equivalent(wanted.Value, actual.Value) && wanted.Quality == actual.Quality && actual.Value.ValueKind != JsonValueKind.Null) matches.Add(actual);
                    else mismatch = true;
                }
                else mismatch = true;
            bool expectsChange = expectedPoints.Any(point => !Equivalent(expectedPoints[0].Value, point.Value));
            bool sawChange = matches.Count > 1 && matches.Any(point => !Equivalent(matches[0].Value, point.Value));
            if (matches.Count > 0 && (!expectsChange || sawChange)) matching++;
            if (sawChange) changed++;
            // Repeat suppression can leave only a leading point outside this
            // batch. Report compatibility separately, never call that new arrival.
            var latest = current.SingleOrDefault(item => item.Name == name)?.Points.OrderBy(point => point.Timestamp).LastOrDefault();
            if (matches.Count == 0 && !expectsChange && latest is not null &&
                latest.Value.ValueKind != JsonValueKind.Null && Equivalent(expectedPoints[^1].Value, latest.Value) &&
                latest.Quality == expectedPoints[^1].Quality) unchangedCompatible++;
        }
        int nonNull = current.Count(tag => tag.Points.Any(point => point.Value.ValueKind != JsonValueKind.Null));
        string status = mismatch ? "Mismatch" : matching == expected.Count && nonNull == expected.Count ? "Observed" :
            unchangedCompatible > 0 && matching + unchangedCompatible == expected.Count && nonNull == expected.Count ? "ConsistentWithoutNewArrival" : "NotYetObserved";
        return new(batchId, status, matching, nonNull, changed, mismatch ? "historian.pattern_mismatch" : null, "NotReviewed");
    }

    private static Dictionary<string, List<ObservedPoint>> ParseExpected(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.EnumerateObject().ToDictionary(tag => tag.Name,
            tag => tag.Value.EnumerateArray().Select(point => new ObservedPoint(point.GetProperty("t").GetDateTimeOffset(),
                point.GetProperty("v").Clone(), point.GetProperty("q").GetInt32())).ToList(), StringComparer.Ordinal);
    }
    private static bool Equivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.ValueKind == JsonValueKind.Number)
            return actual.TryGetDouble(out double number) && number == (expected.GetBoolean() ? 1 : 0);
        return JsonElement.DeepEquals(expected, actual);
    }
    internal static bool IsTransportFailure(Exception error) => error is HttpRequestException or IOException or OperationCanceledException or
        JsonException or InvalidOperationException or KeyNotFoundException or FormatException;
    internal static RuntimeFailure SafeFailure(Exception error) => error as RuntimeFailure ?? new("historian.connection_failed",
        "The authenticated Historian operation could not complete or its response was malformed. Check service availability, trusted certificates, credentials and API compatibility. Inspect durable batch status before continuing; no publish is automatically retried.");
}
