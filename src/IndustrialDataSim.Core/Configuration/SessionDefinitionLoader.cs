using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Core.Configuration;

/// <summary>
/// Strict, offline parser for the v1 session-header contract. Explicit inspection
/// distinguishes missing, null, and wrong-type fields without leaking serializer
/// exception text. Unknown and duplicate properties fail rather than silently
/// changing the meaning of an agent-authored document.
/// </summary>
public static partial class SessionDefinitionLoader
{
    private static readonly string[] RootProperties =
        ["schemaVersion", "sessionId", "connectionProfile", "dataset", "startUtc", "endUtc", "outputTags"];

    public static SessionLoadResult Load(string json)
    {
        using JsonDocument? document = Parse(json, out ValidationError? parseError);
        if (parseError is not null)
        {
            return new(null, new[] { parseError });
        }

        var errors = new ValidationErrors();
        JsonElement root = document!.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new(null, new[] { new ValidationError("session.object_required", "$",
                "The session document must be a JSON object.") });
        }

        CheckProperties(root, RootProperties, "$", errors);
        if (!root.TryGetProperty("schemaVersion", out JsonElement version))
        {
            errors.Add(new("session.required", "$.schemaVersion", "This field is required."));
        }
        else if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != 1)
        {
            errors.Add(new("session.unsupported_version", "$.schemaVersion",
                "Only schemaVersion 1 is supported; use the integer literal 1."));
        }

        string? id = ReadString(root, "sessionId", "$", errors);
        string? profile = ReadString(root, "connectionProfile", "$", errors);
        CheckIdentifier(id, "$.sessionId", errors);
        CheckIdentifier(profile, "$.connectionProfile", errors);

        string? dataset = ReadString(root, "dataset", "$", errors);
        if (dataset is not null && DatasetNameValidator.Validate(dataset) is { } datasetError)
        {
            errors.Add(datasetError);
        }

        DateTimeOffset? start = ReadUtc(root, "startUtc", errors);
        DateTimeOffset? end = ReadUtc(root, "endUtc", errors);
        if (start.HasValue && end.HasValue && end <= start)
        {
            errors.Add(new("session.invalid_time_range", "$.endUtc",
                "End time must be strictly later than start time."));
        }

        List<OutputTagDefinition> tags = ReadTags(root, errors);
        if (errors.ErrorCount > 0)
        {
            return new(null, errors.AsReadOnly());
        }

        return new(new SessionDefinition(1, id!, profile!, dataset!, start!.Value,
            end!.Value, tags.AsReadOnly()), Array.Empty<ValidationError>());
    }

    internal static JsonDocument? Parse(string json, out ValidationError? error)
    {
        JsonDocument? document = null;
        try
        {
            // Default JSON parsing rejects comments and trailing commas. Limit
            // nesting as well; this header has no deeply nested rule language.
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateUnicode(document.RootElement);
            error = null;
            return document;
        }
        catch (JsonException exception)
        {
            document?.Dispose();
            // Numeric positions help locate syntax errors without exposing the
            // parser's raw message or its potentially sensitive property path.
            string location = exception.LineNumber is long line && exception.BytePositionInLine is long offset
                ? FormattableString.Invariant($" at line {line + 1}, byte {offset + 1} (both 1-based)")
                : "";
            error = new("session.invalid_json", "$",
                $"Invalid JSON{location}. Correct the syntax and keep nesting at 32 levels or fewer; comments and trailing commas are unsupported.");
            return null;
        }
        catch (InvalidOperationException)
        {
            document?.Dispose();
            error = new("session.invalid_json", "$",
                "Invalid Unicode in a JSON string or property name. Replace unpaired surrogate escapes with valid Unicode and rerun validation.");
            return null;
        }
    }

    private static void ValidateUnicode(JsonElement item)
    {
        // JsonDocument defers decoding escaped strings. An unpaired surrogate
        // can pass Parse and throw only when a property/name is later accessed.
        // Force decoding inside the parsing error boundary, including unknown
        // fields, so malformed input cannot escape as a process stack trace.
        switch (item.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in item.EnumerateObject())
                {
                    _ = property.Name;
                    ValidateUnicode(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in item.EnumerateArray())
                {
                    ValidateUnicode(child);
                }
                break;
            case JsonValueKind.String:
                _ = item.GetString();
                break;
        }
    }

    internal static void CheckProperties(JsonElement item, string[] allowed, string path,
        ValidationErrors errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            // Unknown property names may themselves contain sensitive content.
            // Report the containing object, not the untrusted name or value.
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(new("session.unknown_property", path,
                    "This object contains an unsupported property. Remove fields not listed for this object in the version 1 schema."));
            }
            else if (!seen.Add(property.Name))
            {
                errors.Add(new("session.duplicate_property", path + "." + property.Name,
                    "A property must occur only once."));
            }
        }
    }

    internal static string? ReadString(JsonElement item, string name, string path,
        ValidationErrors errors)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
        {
            errors.Add(new("session.required", path + "." + name, "This field is required."));
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new("session.string_required", path + "." + name, "Expected a JSON string."));
            return null;
        }
        return value.GetString()!;
    }

    private static void CheckIdentifier(string? value, string path, ValidationErrors errors)
    {
        if (value is not null && !IdentifierPattern().IsMatch(value))
        {
            errors.Add(new("session.invalid_identifier", path,
                "Use 1–64 ASCII letters, digits, hyphens, or underscores."));
        }
    }

    private static DateTimeOffset? ReadUtc(JsonElement root, string name, ValidationErrors errors)
    {
        string? value = ReadString(root, name, "$", errors);
        if (value is null)
        {
            return null;
        }
        // Requiring Z avoids machine-local timezone interpretation. The lexical
        // check also prevents the permissive date parser from accepting extra text.
        if (UtcPattern().IsMatch(value) && DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
        {
            return timestamp;
        }
        errors.Add(new("session.invalid_utc", "$." + name,
            "Use a valid UTC timestamp ending in Z, with seconds and at most seven fractional digits."));
        return null;
    }

    private static List<OutputTagDefinition> ReadTags(JsonElement root, ValidationErrors errors)
    {
        var tags = new List<OutputTagDefinition>();
        if (!root.TryGetProperty("outputTags", out JsonElement array))
        {
            errors.Add(new("session.required", "$.outputTags", "This field is required."));
            return tags;
        }
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            errors.Add(new("session.tags_required", "$.outputTags", "Expected a nonempty array of output tags."));
            return tags;
        }

        // Until server case semantics are verified, reject case-only collisions
        // within a document. Do not normalize the actual names sent downstream.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement tag in array.EnumerateArray())
        {
            string path = $"$.outputTags[{index++}]";
            if (tag.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("session.tag_object_required", path, "Expected an output tag object."));
                continue;
            }
            CheckProperties(tag, ["name", "valueType"], path, errors);
            string? name = ReadString(tag, "name", path, errors);
            string? type = ReadString(tag, "valueType", path, errors);
            if (name is not null)
            {
                // Dataset restrictions do not apply to tags: dots, for example,
                // are already used by our verified Historian test tags.
                if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Any(char.IsControl))
                {
                    errors.Add(new("session.invalid_tag_name", path + ".name",
                        "Tag names must be nonempty, without surrounding whitespace or control characters."));
                }
                if (!names.Add(name))
                {
                    errors.Add(new("session.duplicate_tag", path + ".name",
                        "Output tag names must be unique, including when compared without case."));
                }
            }
            if (type is not null && type is not ("number" or "boolean" or "string"))
            {
                errors.Add(new("session.invalid_value_type", path + ".valueType",
                    "Supported value types are number, boolean, and string."));
            }
            if (name is not null && type is not null)
            {
                tags.Add(new(name, type));
            }
        }
        return tags;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex UtcPattern();
}
