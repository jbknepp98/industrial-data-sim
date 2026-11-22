using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Web;

/// <summary>
/// The Historian Datasets of plants approved on the page. Each approved plant
/// gets its own Dataset, named exactly like the simulation and created with a
/// 365-day purge age (owner decision, November 22, 2026), so its tags never
/// mix with another plant's and its whole history is kept. The page only ever
/// writes to or deletes a Dataset it created; tests use an in-memory copy.
/// </summary>
public interface IPlantDatasets
{
    Task<bool> Exists(string dataset, CancellationToken stop);
    /// <summary>Creates the Dataset; throws historian.dataset_exists when the name is taken.</summary>
    Task Create(string dataset, CancellationToken stop);
    /// <summary>Deletes the Dataset and all its history; false when it was already gone.</summary>
    Task<bool> Delete(string dataset, CancellationToken stop);
}

public static class PlantDatasets
{
    /// <summary>Purge age of every Dataset the page creates.</summary>
    public const int PurgeAgeDays = 365;
}

/// <summary>Plant Datasets in the real Historian, one short-lived client per call.</summary>
public sealed class HistorianPlantDatasets(Func<HistorianClient> client) : IPlantDatasets
{
    public async Task<bool> Exists(string dataset, CancellationToken stop)
    {
        using var historian = client();
        return await historian.DatasetExists(dataset, stop);
    }

    public async Task Create(string dataset, CancellationToken stop)
    {
        using var historian = client();
        await historian.CreateDataset(dataset, PlantDatasets.PurgeAgeDays, stop);
    }

    public async Task<bool> Delete(string dataset, CancellationToken stop)
    {
        using var historian = client();
        return await historian.DeleteDataset(dataset, stop);
    }
}

/// <summary>An in-process stand-in for tests. Names compare ignoring case, as Historian does.</summary>
public sealed class InMemoryPlantDatasets : IPlantDatasets
{
    private readonly Dictionary<string, int> datasets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    /// <summary>Datasets that exist, with their purge age in days.</summary>
    public IReadOnlyDictionary<string, int> Current { get { lock (gate) return new Dictionary<string, int>(datasets, StringComparer.OrdinalIgnoreCase); } }

    /// <summary>A Dataset someone else made (the page must never touch it).</summary>
    public void AddForeign(string dataset, int purgeAgeDays = 7) { lock (gate) datasets[dataset] = purgeAgeDays; }

    public Task<bool> Exists(string dataset, CancellationToken stop) { lock (gate) return Task.FromResult(datasets.Keys.Contains(dataset, StringComparer.Ordinal)); }

    public Task Create(string dataset, CancellationToken stop)
    {
        lock (gate)
        {
            if (!datasets.TryAdd(dataset, PlantDatasets.PurgeAgeDays))
                throw new RuntimeFailure("historian.dataset_exists", $"Historian already has a Dataset named {dataset} (names are compared ignoring case).");
        }
        return Task.CompletedTask;
    }

    public Task<bool> Delete(string dataset, CancellationToken stop) { lock (gate) return Task.FromResult(datasets.Remove(dataset)); }
}
