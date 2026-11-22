using System.Text.Json;

namespace IndustrialDataSim.Web;

/// <summary>
/// One JSON object per line on standard output: a stable event code, the
/// simulation it concerns, and reviewed fields only (counts, times, error codes
/// and the plain-language messages the simulator writes for people). Never
/// configuration, connection strings, secrets, model content or raw exceptions.
/// A failing writer is ignored, so logging can never change plant delivery.
/// </summary>
public sealed class JsonLog(TextWriter output)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();

    public void Write(string eventCode, string? simulation, object? fields = null)
    {
        try
        {
            var line = new Dictionary<string, object?> { ["time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["event"] = eventCode };
            if (simulation is not null) line["simulation"] = simulation;
            if (fields is not null)
                foreach (var property in JsonSerializer.SerializeToElement(fields, Options).EnumerateObject())
                    line[property.Name] = property.Value;
            string text = JsonSerializer.Serialize(line, Options);
            lock (gate)
            {
                output.WriteLine(text);
                output.Flush();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException or JsonException) { }
    }
}
