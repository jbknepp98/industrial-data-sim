using System.Text.Json;
using IndustrialDataSim.Core.Simulation;

namespace IndustrialDataSim.Runtime;

public enum FakeWriteBehavior { Accept, FailBeforeAcceptance, LoseResponseAfterAcceptance, PartialAcceptance, AmbiguousResponse }

/// <summary>
/// Deterministic in-memory test destination. Its whole-batch acceptance contract
/// is explicit and must never be inferred for the production Historian API.
/// It rejects equal/backward submissions even when repeats were not retained.
/// </summary>
public sealed class FakeHistorian
{
    private readonly object sync = new();
    private readonly Dictionary<string, Queue<FakeWriteBehavior>> behavior = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TvqPoint>> accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TvqPoint>> retained = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);
    public bool SuppressRepeats { get; init; } = true;
    /// <summary>False keeps only the latest accepted/retained point per tag for bounded worker diagnostics.</summary>
    public bool KeepHistory { get; init; } = true;
    internal Func<Task>? BeforeWrite { get; set; }

    public void NextWrite(string sessionId, FakeWriteBehavior next)
    {
        lock (sync)
        {
            if (!behavior.TryGetValue(sessionId, out var queue)) behavior[sessionId] = queue = new();
            queue.Enqueue(next);
        }
    }

    public int Calls(string sessionId) { lock (sync) return calls.GetValueOrDefault(sessionId); }
    public IReadOnlyList<TvqPoint> Accepted(string profile, string dataset, string tag)
    { lock (sync) return accepted.TryGetValue(Key(profile, dataset, tag), out var points) ? points.ToArray() : []; }
    public IReadOnlyList<TvqPoint> Retained(string profile, string dataset, string tag)
    { lock (sync) return retained.TryGetValue(Key(profile, dataset, tag), out var points) ? points.ToArray() : []; }

    internal async Task<bool> WriteAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        if (BeforeWrite is { } wait) await wait();
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            string id = work.Batch.SessionId;
            calls[id] = calls.GetValueOrDefault(id) + 1;
            var mode = behavior.TryGetValue(id, out var queue) && queue.Count > 0 ? queue.Dequeue() : FakeWriteBehavior.Accept;
            if (mode == FakeWriteBehavior.FailBeforeAcceptance) throw new TimeoutException();
            var payload = JsonSerializer.Deserialize<Dictionary<string, List<TvqPoint>>>(work.Batch.Payload!)!;
            // Validate complete ordering before applying an ordinary accepted request.
            foreach (var (tag, points) in payload)
            {
                string key = Key(work.Profile, work.Dataset, tag);
                long previous = accepted.TryGetValue(key, out var old) && old.Count > 0 ? old[^1].Timestamp.Ticks : -1;
                foreach (var point in points)
                {
                    if (point.Timestamp.Ticks <= previous)
                        throw new RuntimeFailure("fake.ordering_conflict", "Fake Historian rejected a repeated or backward timestamp. Inspect local progress and external writes; do not replay.");
                    previous = point.Timestamp.Ticks;
                }
            }
            int applied = 0;
            foreach (var (tag, points) in payload)
            {
                string key = Key(work.Profile, work.Dataset, tag);
                if (!accepted.ContainsKey(key)) { accepted[key] = []; retained[key] = []; }
                foreach (var point in points)
                {
                    if (!KeepHistory) accepted[key].Clear();
                    accepted[key].Add(point);
                    var history = retained[key];
                    if (!SuppressRepeats || history.Count == 0 || history[^1].Quality != point.Quality ||
                        history[^1].Value.GetRawText() != point.Value.GetRawText())
                    {
                        if (!KeepHistory) history.Clear();
                        history.Add(point);
                    }
                    if (++applied == 1 && mode == FakeWriteBehavior.PartialAcceptance) throw new TimeoutException();
                }
            }
            if (mode == FakeWriteBehavior.LoseResponseAfterAcceptance) throw new TimeoutException();
            return mode != FakeWriteBehavior.AmbiguousResponse;
        }
    }

    private static string Key(string profile, string dataset, string tag) =>
        JsonSerializer.Serialize(new[] { profile.ToUpperInvariant(), dataset.ToUpperInvariant(), tag.ToUpperInvariant() });
}
