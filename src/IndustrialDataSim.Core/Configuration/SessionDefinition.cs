namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Validated session header, not yet an executable simulation. ConnectionProfile
/// is a reference to locally supplied settings; this document never embeds them.
/// The time range is half-open: StartUtc is included and EndUtc is excluded.
/// </summary>
public sealed record SessionDefinition(
    int SchemaVersion,
    string SessionId,
    string ConnectionProfile,
    string Dataset,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<OutputTagDefinition> OutputTags)
{
    /// <summary>
    /// Internal end used for open-ended sessions (schemaVersion 2 without endUtc).
    /// Generation of an open-ended session never passes the wall clock, so this
    /// far-future value only keeps slot arithmetic finite.
    /// </summary>
    public static readonly DateTimeOffset OpenEndUtc = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>True when the session runs until stopped (no endUtc).</summary>
    public bool OpenEnded { get; init; }
}

/// <summary>Declares an output's identity and JSON value kind, not a generator.</summary>
public sealed record OutputTagDefinition(string Name, string ValueType);
