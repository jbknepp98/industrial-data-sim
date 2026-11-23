using System.Text.Json.Nodes;

namespace IndustrialDataSim.Interview;

/// <summary>
/// The plants the interview page created, as the interviewer may see them. The
/// web container implements it from its registry, the plant store and the
/// hosting service; plants created by other tools or containers are never
/// listed. Read-only: the interviewer can only propose a stop or start, which
/// the user confirms on the page.
/// </summary>
public interface IPlantDirectory
{
    /// <summary>One summary object per page-created plant: simulation, status, storylines, history window, Dataset.</summary>
    JsonArray List();
    /// <summary>Details of one page-created plant (lines, tables, tag root, delivery progress); null when the page did not create it.</summary>
    JsonObject? Describe(string simulation);
    /// <summary>The interview that built the plant, whose history a change continues; null when the page did not create it.</summary>
    Guid? InterviewOf(string simulation);
}
