using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Cli;

internal sealed record ControlRequest(int Version, string Action, string? SessionId = null,
    string? Configuration = null, string? Cursor = null, long AfterBatch = 0, string? Cancellation = null);
internal sealed record ControlResponse(int ExitCode, byte[] Json);

/// <summary>
/// One request and one reply per local connection. Lengths are checked before
/// allocation; a stalled or disconnected peer never causes a command retry.
/// This protocol is local process control, not a public network API.
/// </summary>
internal static class LocalControlProtocol
{
    // A 1 MiB model can expand sixfold when encoded as a JSON string.
    internal const int MaximumRequestBytes = 8 * 1024 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static string PipeName(string database)
    {
        // No database path or user name is disclosed in the endpoint name.
        // Clients must use the same absolute spelling (no symlink aliases).
        string identity = Environment.UserName + "\n" + Path.GetFullPath(database);
        // Keep the Unix socket path below macOS's 104-character limit
        // with its usual long temporary-directory prefix (128-bit digest prefix).
        return "idsim-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }

    internal static async Task WriteAsync(Stream stream, byte[] body, int code, int maximumBytes, CancellationToken stop)
    {
        if (body.Length > maximumBytes) throw InvalidFrame();
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), code);
        await stream.WriteAsync(header, stop);
        await stream.WriteAsync(body, stop);
        await stream.FlushAsync(stop);
    }

    internal static async Task<ControlResponse> ReadAsync(Stream stream, int maximumBytes, CancellationToken stop)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, stop);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        int code = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 1 || length > maximumBytes || code is < 0 or > 3) throw InvalidFrame();
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, stop);
        return new(code, body);
    }

    internal static RuntimeFailure InvalidFrame() => new("host.invalid_request",
        "The local control message is invalid, unsupported, or exceeds its size limit. Use matching CLI and host versions. Inspect session status before repeating a mutation.");

    internal static ControlRequest ParseRequest(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidFrame();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw InvalidFrame();
            return document.RootElement.Deserialize<ControlRequest>(Options) ?? throw InvalidFrame();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw InvalidFrame(); }
    }
}
