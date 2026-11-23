using System.Text.Json.Nodes;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;

namespace IndustrialDataSim.Web;

/// <summary>
/// The interviewer's view of the plants this page built: plants approved from
/// one of its interviews, read from the plant store, the page registry and the
/// hosting service. Everything else in flow_data_sim (BluebirdBev, other
/// tools' plants) is left out, as on the Plants tab.
/// </summary>
public sealed class WebPlantDirectory(WebServices services, PlantHostingService hosting) : IPlantDirectory
{
    public JsonArray List()
    {
        var entries = services.Registry.List().ToDictionary(e => e.Simulation, StringComparer.Ordinal);
        return new JsonArray(PagePlants.List(services)
            .Select(r => (JsonNode)Summary(r, entries.GetValueOrDefault(r.Simulation))).ToArray());
    }

    public JsonObject? Describe(string simulation)
    {
        var registration = PagePlants.Find(services, simulation);
        if (registration is null) return null;
        var summary = Summary(registration, services.Registry.Find(simulation));
        var loaded = PlantModelLoader.Load(registration.ModelJson, registration.AnchorUtc);
        if (loaded.Model is { } model)
        {
            summary["tagRoot"] = model.TagRoot;
            summary["tables"] = new JsonArray(PlantSchema.TablesFor(model).Select(t => (JsonNode)PlantSchema.PhysicalName(model, t.Purpose)).ToArray());
            summary["lines"] = new JsonArray(model.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["name"] = l.Name,
                ["machines"] = new JsonArray(l.Equipment.Select(e => (JsonNode)$"{e.Id} {e.Name}").ToArray())
            }).ToArray());
            summary["products"] = new JsonArray(model.Skus.Select(s => (JsonNode)$"{s.Code} {s.Description}").ToArray());
        }
        return summary;
    }

    public Guid? InterviewOf(string simulation) =>
        services.Interviews.ApprovedSimulations().Contains(simulation) ? services.Registry.Find(simulation)?.InterviewId : null;

    private JsonObject Summary(Runtime.PlantRegistration r, HostedPlantEntry? entry)
    {
        var status = hosting.Status(r.Simulation);
        var storylines = new JsonArray();
        if (entry is not null && services.Interviews.Load(entry.InterviewId)?.NeedsJson is { } needs &&
            JsonNode.Parse(needs)?["chosenStorylines"] is JsonArray chosen)
            foreach (var id in chosen.Select(n => n?.GetValue<string>()).OfType<string>())
                storylines.Add(services.Library.Storylines.Find(id)?.Title ?? id);
        return new JsonObject
        {
            ["simulation"] = r.Simulation,
            ["status"] = status?.State ?? (entry?.HostingEnabled == false ? HostingState.Stopped : "not running"),
            ["statusMessage"] = status?.Message,
            ["storylines"] = storylines,
            ["historyStartUtc"] = r.StartUtc,
            ["approvedAtUtc"] = r.AnchorUtc,
            ["tagsDeliveredThroughUtc"] = r.TagCursorUtc,
            ["dataset"] = entry?.Dataset ?? services.Dataset,
            ["tablePrefix"] = r.TablePrefix
        };
    }
}
