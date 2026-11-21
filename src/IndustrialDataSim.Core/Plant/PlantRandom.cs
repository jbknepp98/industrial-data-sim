using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// A named, seeded random stream. Each plant concern (demand on a given day, one
/// failure mode, one technician's response times …) draws from its own stream,
/// so adding a line or a failure mode never changes the draws of another one,
/// and the same plant model, seed and anchor always reproduce the same history.
/// The generator is SplitMix64, seeded from SHA-256 of the stream name and seed.
/// It is deterministic simulation randomness, not a security primitive.
/// </summary>
internal sealed class PlantRandom
{
    private ulong state;

    internal PlantRandom(ulong seed, string stream)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"plant-v1:{seed}:{stream}"));
        state = BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    internal ulong NextUInt64()
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1) with 53 bits of precision.</summary>
    internal double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    internal double Uniform(double minimum, double maximum) => minimum + (maximum - minimum) * NextDouble();

    internal double Uniform(PlantRange range) => Uniform(range.Minimum, range.Maximum);

    /// <summary>Exponential with the given mean; used for time between failures.</summary>
    internal double Exponential(double mean) => -mean * Math.Log(1.0 - NextDouble());

    /// <summary>Integer in [minimum, maximum] inclusive.</summary>
    internal int Integer(int minimum, int maximum) => minimum + (int)(NextDouble() * (maximum - minimum + 1));

    /// <summary>Approximately normal (sum of four uniforms), mean 0, standard deviation 1.</summary>
    internal double Gaussian()
    {
        double sum = NextDouble() + NextDouble() + NextDouble() + NextDouble();
        return (sum - 2.0) * Math.Sqrt(3.0);
    }

    /// <summary>
    /// Stateless noise for a tag at a sample time: the same tag and time always
    /// give the same value, regardless of window size, restarts or sample order.
    /// </summary>
    internal static double Noise(ulong seed, string tag, long sampleTicks)
    {
        var random = new PlantRandom(seed, $"noise:{tag}:{sampleTicks}");
        return random.Gaussian();
    }
}
