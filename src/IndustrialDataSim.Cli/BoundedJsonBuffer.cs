namespace IndustrialDataSim.Cli;

/// <summary>
/// Stages a preview before writing to stdout. A rejected output never leaves a
/// half-written JSON document behind. Limit encoded bytes, not character count.
/// </summary>
internal sealed class BoundedJsonBuffer(int maximumBytes) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckCapacity(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckCapacity(buffer.Length);
        base.Write(buffer);
    }

    private void CheckCapacity(int count)
    {
        if (Position + count > maximumBytes)
        {
            throw new OutputLimitException();
        }
    }
}

internal sealed class OutputLimitException : Exception;
