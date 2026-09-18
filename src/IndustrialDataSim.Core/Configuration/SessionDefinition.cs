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
    IReadOnlyList<OutputTagDefinition> OutputTags);

/// <summary>Declares an output's identity and JSON value kind, not a generator.</summary>
public sealed record OutputTagDefinition(string Name, string ValueType);
