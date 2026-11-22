using System.Text.Json;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Interview;

/// <summary>
/// Reads one tool call's input and collects every problem with its field
/// path. Strict schemas make the provider return well-shaped input, but the
/// model behind Gage may not honour strict mode, so every rule is checked
/// again here: types, required fields, unknown fields, lengths and counts.
/// Messages never echo the offending value.
/// </summary>
internal sealed class ToolInput
{
    public const int MaxText = 200;
    public const int MaxListItems = 12;

    private readonly JsonElement root;
    public List<ValidationError> Errors { get; } = [];

    public ToolInput(JsonElement input, IReadOnlyList<string> fields)
    {
        root = input;
        if (input.ValueKind != JsonValueKind.Object)
        {
            Errors.Add(new("tool.input_object", "$", "The tool input must be a JSON object."));
            return;
        }
        foreach (var property in input.EnumerateObject())
            if (!fields.Contains(property.Name))
                Errors.Add(new("tool.unknown_field", "$." + property.Name, $"Remove this field; the allowed fields are {string.Join(", ", fields)}."));
        foreach (string field in fields)
            if (!input.TryGetProperty(field, out _))
                Errors.Add(new("tool.missing_field", "$." + field, "Supply this field. Every field is required; use an empty string or empty list when there is nothing to say."));
    }

    public bool IsValid => Errors.Count == 0;

    private JsonElement? Field(string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;

    public string Text(string name, bool allowEmpty, int maxLength = MaxText) => TextAt(Field(name), "$." + name, allowEmpty, maxLength);

    private string TextAt(JsonElement? value, string path, bool allowEmpty, int maxLength)
    {
        if (value is null) return "";
        if (value.Value.ValueKind != JsonValueKind.String)
        {
            Errors.Add(new("tool.expected_string", path, "Use a string."));
            return "";
        }
        string text = value.Value.GetString()!.Trim();
        if (!allowEmpty && text.Length == 0) Errors.Add(new("tool.empty_string", path, "Use a non-empty string."));
        if (text.Length > maxLength) Errors.Add(new("tool.string_too_long", path, $"Use at most {maxLength} characters."));
        return text;
    }

    public int Integer(string name, int minimum, int maximum)
    {
        if (Field(name) is not { } value) return minimum;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            Errors.Add(new("tool.expected_integer", "$." + name, $"Use a whole number from {minimum} through {maximum}."));
            return minimum;
        }
        if (number < minimum || number > maximum)
            Errors.Add(new("tool.out_of_range", "$." + name, $"Use a whole number from {minimum} through {maximum}."));
        return number;
    }

    /// <summary>A list of strings; <paramref name="allowed"/> restricts the values when given.</summary>
    public List<string> Strings(string name, int minimum, int maximum = MaxListItems, IReadOnlyList<string>? allowed = null)
    {
        var result = new List<string>();
        foreach (var (item, path) in Items(name, minimum, maximum))
        {
            string text = TextAt(item, path, allowEmpty: false, MaxText);
            if (text.Length == 0) continue;
            if (allowed is not null && !allowed.Contains(text))
            {
                Errors.Add(new("tool.value_not_allowed", path, $"Use one of: {string.Join(", ", allowed)}."));
                continue;
            }
            if (result.Contains(text, StringComparer.OrdinalIgnoreCase))
                Errors.Add(new("tool.duplicate_value", path, "Remove the repeated value."));
            else result.Add(text);
        }
        return result;
    }

    /// <summary>A list of {key, value} rename objects, for example lineNames [{id, name}].</summary>
    public List<(string Key, string Value, string Path)> Pairs(string name, string keyField, string valueField, int maximum)
    {
        var result = new List<(string, string, string)>();
        foreach (var (item, path) in Items(name, 0, maximum))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                Errors.Add(new("tool.expected_object", path, $"Use an object with {keyField} and {valueField}."));
                continue;
            }
            foreach (var property in item.EnumerateObject())
                if (property.Name != keyField && property.Name != valueField)
                    Errors.Add(new("tool.unknown_field", path + "." + property.Name, $"Remove this field; use only {keyField} and {valueField}."));
            string key = TextAt(item.TryGetProperty(keyField, out var k) ? k : null, path + "." + keyField, allowEmpty: false, 64);
            string value = TextAt(item.TryGetProperty(valueField, out var v) ? v : null, path + "." + valueField, allowEmpty: false, 80);
            if (!item.TryGetProperty(keyField, out _)) Errors.Add(new("tool.missing_field", path + "." + keyField, "Supply this field."));
            if (!item.TryGetProperty(valueField, out _)) Errors.Add(new("tool.missing_field", path + "." + valueField, "Supply this field."));
            if (key.Length > 0 && result.Any(r => r.Item1 == key)) Errors.Add(new("tool.duplicate_value", path + "." + keyField, "Rename each item at most once."));
            else if (key.Length > 0 && value.Length > 0) result.Add((key, value, path));
        }
        return result;
    }

    private IEnumerable<(JsonElement Item, string Path)> Items(string name, int minimum, int maximum)
    {
        if (Field(name) is not { } value) yield break;
        if (value.ValueKind != JsonValueKind.Array)
        {
            Errors.Add(new("tool.expected_list", "$." + name, "Use a list."));
            yield break;
        }
        int count = value.GetArrayLength();
        if (count < minimum || count > maximum)
            Errors.Add(new("tool.list_size", "$." + name, minimum == maximum ? $"List exactly {minimum} item(s)." : $"List from {minimum} through {maximum} items."));
        int index = 0;
        foreach (var item in value.EnumerateArray().Take(maximum))
            yield return (item, $"$.{name}[{index++}]");
    }
}
