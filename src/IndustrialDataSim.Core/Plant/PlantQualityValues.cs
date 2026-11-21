namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// The one value function for product quality. The line's in-line analyzer
/// tag reads it continuously while the line runs, and the lab result for a lot
/// is the same function at the sample time, so records and signals agree.
/// </summary>
public static class PlantQualityValues
{
    public static double Value(PlantModel model, PlantQualityParameter parameter, string line, string sku, DateTimeOffset utc)
    {
        double value = parameter.Target + parameter.SkuOffsets.GetValueOrDefault(sku);
        foreach (var excursion in model.Quality!.Excursions)
            if (excursion.Parameter == parameter.Name && (excursion.Sku is null || excursion.Sku == sku) &&
                utc >= excursion.FromUtc && utc < excursion.UntilUtc)
                value += excursion.Shift;
        value += parameter.Sigma * PlantRandom.Noise(model.Seed, $"quality:{line}:{parameter.Name}", utc.UtcTicks);
        return Math.Round(value, 3);
    }

    public static bool InSpec(PlantQualityParameter parameter, double value) => value >= parameter.Lower && value <= parameter.Upper;
}
