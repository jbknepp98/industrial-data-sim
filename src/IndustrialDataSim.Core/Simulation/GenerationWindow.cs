using System.Text;
using System.Text.Json;
using IndustrialDataSim.Core.Configuration;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Simulation;

public sealed record GenerationWindowResult(long NextSlot, int PointCount, string Payload,
    IReadOnlyDictionary<string, long> LastTicks, ValidationError? Error);

/// <summary>
/// Generates a bounded slice on the original sample grid. A flattened cursor
/// visits tags in declaration order at each sample time. Suppression advances
/// the cursor without producing a point; a full batch leaves the next slot unconsumed.
/// </summary>
public static class GenerationWindow
{
    public static long TotalSlots(SimulationDefinition model)
    {
        long duration = (model.Session.EndUtc - model.Session.StartUtc).Ticks;
        long interval = model.SamplingIntervalMs * TimeSpan.TicksPerMillisecond;
        return checked(((duration - 1) / interval + 1) * model.Session.OutputTags.Count);
    }

    public static GenerationWindowResult Generate(SimulationDefinition model, long nextSlot,
        int maximumSlots, int maximumPoints, int maximumBytes)
    {
        long total = TotalSlots(model);
        if (nextSlot < 0 || nextSlot > total || maximumSlots < 1 || maximumPoints < 1 || maximumBytes < 2)
            throw new ArgumentOutOfRangeException(nameof(nextSlot), "Supply a valid cursor and positive window limits (at least two bytes).");
        var data = new Dictionary<string, List<TvqPoint>>(StringComparer.Ordinal);
        var positions = new Dictionary<string, long>(StringComparer.Ordinal);
        int pointCount = 0;
        int bytes = 2; // Outer braces; property/point sizes below include JSON escaping.
        long cursor = nextSlot;
        long stop = nextSlot + Math.Min((long)maximumSlots, total - nextSlot);
        for (; cursor < stop && pointCount < maximumPoints; cursor++)
        {
            var tag = model.Session.OutputTags[(int)(cursor % model.Session.OutputTags.Count)];
            long sample = cursor / model.Session.OutputTags.Count;
            long elapsed = sample * (model.SamplingIntervalMs * TimeSpan.TicksPerMillisecond);
            var generator = model.Generators[tag.Name];
            if (!generator.EmitsAt(elapsed)) continue;
            var value = generator.Evaluate(elapsed);
            if (value is null)
                return Failure(nextSlot, "generation.non_finite_value",
                    "Generator arithmetic produced a non-finite value. Reduce the range, start value, or rate.");
            var point = new TvqPoint(model.Session.StartUtc.UtcDateTime.AddTicks(elapsed), value.Value, 192);
            bool existing = data.TryGetValue(tag.Name, out var points);
            int extra;
            try
            {
                // Count serialization with a non-buffering stream. Even an escaped
                // string larger than the batch cap cannot allocate an oversized payload.
                extra = EncodedSize(point, maximumBytes);
                extra = checked(extra + (existing ? 1 : EncodedSize(tag.Name, maximumBytes) + 3 + (data.Count > 0 ? 1 : 0)));
            }
            catch (WindowSizeException)
            {
                extra = maximumBytes;
            }
            if (extra > maximumBytes - bytes)
            {
                if (pointCount == 0)
                    return Failure(nextSlot, "generation.point_too_large",
                        "One encoded tag/value exceeds the batch byte limit. Increase that limit or shorten the tag name or string value.");
                break;
            }
            if (!existing) data.Add(tag.Name, points = []);
            points!.Add(point);
            positions[tag.Name] = point.Timestamp.Ticks;
            bytes += extra;
            pointCount++;
        }
        return new(cursor, pointCount, JsonSerializer.Serialize(data), positions, null);
    }

    private static GenerationWindowResult Failure(long cursor, string code, string message) =>
        new(cursor, 0, "{}", new Dictionary<string, long>(), new(code, "$", message));

    private static int EncodedSize<T>(T value, int limit)
    {
        using var counter = new CountingStream(limit);
        JsonSerializer.Serialize(counter, value);
        return (int)counter.Length;
    }

    private sealed class WindowSizeException : Exception;

    private sealed class CountingStream(int limit) : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Count(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);
        private void Count(int count)
        {
            if (count > limit - length) throw new WindowSizeException();
            length += count;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
