using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Versioned deterministic timing choices, not security credentials. Hash each
/// step independently so sampling, other tags, and execution order cannot consume
/// a shared random stream. The byte convention is part of generatorVersion 1.
/// </summary>
internal static class StableDurationChoice
{
    internal static long Choose(uint seed, int stepIndex, long minimum, long maximum)
        => Choose("staircase-duration-v1", seed, stepIndex, minimum, maximum);

    internal static long Choose(string stream, uint seed, int stepIndex, long minimum, long maximum)
    {
        if (minimum == maximum) return minimum;
        ulong width = (ulong)(maximum - minimum + 1);
        // Rejection avoids modulo bias when width does not divide 2^64.
        ulong threshold = unchecked(0UL - width) % width;
        for (ulong attempt = 0; ; attempt++)
        {
            string input = string.Create(CultureInfo.InvariantCulture,
                $"{stream}:{seed}:{stepIndex}:{attempt}");
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            ulong draw = BinaryPrimitives.ReadUInt64LittleEndian(digest);
            if (draw >= threshold) return minimum + (long)(draw % width);
        }
    }
}
