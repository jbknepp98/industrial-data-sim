using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndustrialDataSim.Core.Simulation;

/// <summary>
/// A generated observation in Historian wire shape. The engine supplies UTC
/// DateTime values so JSON timestamps end in Z. JsonElement retains scalar type.
/// </summary>
public sealed record TvqPoint(
    [property: JsonPropertyName("t")] DateTime Timestamp,
    [property: JsonPropertyName("v")] JsonElement Value,
    [property: JsonPropertyName("q")] int Quality);
