using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Core.Validation;

namespace IndustrialDataSim.Interview;

/// <summary>
/// Everything the interview reads from the storyline folder
/// (examples/storylines): the library, the base plants and the interview
/// script. The same folder feeds the Claude Code skill, so both front ends
/// offer the same storylines.
/// </summary>
public sealed class InterviewLibrary
{
    private InterviewLibrary(PlantStorylineLibrary storylines, IReadOnlyDictionary<string, string> bases, string script)
    {
        Storylines = storylines;
        Bases = bases;
        Script = script;
    }

    public PlantStorylineLibrary Storylines { get; }

    /// <summary>Base plant JSON by base id (the file name in bases/ without .json).</summary>
    public IReadOnlyDictionary<string, string> Bases { get; }

    /// <summary>The interview script (interview.md), written as instructions to the interviewer.</summary>
    public string Script { get; }

    /// <summary>
    /// Loads the folder. Every available storyline's base must exist, so a
    /// broken library is reported when the service starts, not mid-interview.
    /// Errors name the file and field; file contents are never echoed.
    /// </summary>
    public static (InterviewLibrary? Library, IReadOnlyList<ValidationError> Errors) Load(string folder)
    {
        string? Read(string relative, string code, List<ValidationError> errors)
        {
            try { return File.ReadAllText(Path.Combine(folder, relative)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add(new(code, relative, $"Could not read {relative} in the storyline folder. Check that the folder holds storylines.json, interview.md and bases/, and that it is readable."));
                return null;
            }
        }

        var errors = new List<ValidationError>();
        string? libraryJson = Read("storylines.json", "interview.library_unreadable", errors);
        string? script = Read("interview.md", "interview.script_unreadable", errors);
        if (libraryJson is null || script is null) return (null, errors);

        var loaded = PlantStorylines.Load(libraryJson);
        if (!loaded.IsValid)
            return (null, loaded.Errors.Select(e => e with { Path = "storylines.json " + e.Path }).ToList());

        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string baseId in loaded.Library!.Storylines.Where(s => s.Base is not null).Select(s => s.Base!).Distinct())
        {
            if (Read(Path.Combine("bases", baseId + ".json"), "interview.base_unreadable", errors) is not { } json) continue;
            try { using var _ = JsonDocument.Parse(json); bases[baseId] = json; }
            catch (JsonException)
            {
                errors.Add(new("interview.base_invalid", $"bases/{baseId}.json", $"The base plant \"{baseId}\" is not valid JSON. Fix the file, then run plant validate on a composed model."));
            }
        }
        return errors.Count == 0 ? (new InterviewLibrary(loaded.Library, bases, script), []) : (null, errors);
    }
}
