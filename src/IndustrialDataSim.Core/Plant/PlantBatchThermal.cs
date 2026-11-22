namespace IndustrialDataSim.Core.Plant;

/// <summary>
/// The process temperature of a batch unit, shared by the engine (which carries
/// it from segment to segment) and the signals (which sample it), so both agree:
/// - Running: the phase's target, which is the setpoint ramp minus any drift lag
///   (largest at mid-phase, zero at the phase's start and end). A unit below its
///   target (after a repair, or at the start of a batch) heats toward it at most
///   ReheatRateC per minute; a unit above it (still warm from the last batch)
///   relaxes toward it exponentially. So the temperature never jumps.
/// - Stopped mid-phase (Down): decays exponentially toward ambient.
/// </summary>
public static class PlantBatchThermal
{
    /// <summary>Ambient conditions of an idle batch unit.</summary>
    public const double AmbientC = 20;

    /// <summary>Time constants (minutes): cooling toward ambient while stopped, and settling onto a lower target while running.</summary>
    public const double CoolingMinutes = 45, SettleMinutes = 20;

    /// <summary>The fastest a running unit heats back up to its target (°C per minute).</summary>
    public const double ReheatRateC = 3;

    /// <summary>A temperature lag of this many °C counts as one unit of deviation from the golden profile.</summary>
    public const double LagScaleC = 10;

    /// <summary>The running target at a phase progress: the setpoint, trailed by up to lagC at mid-phase.</summary>
    public static double Target(PlantPhase phase, double lagC, double progress) =>
        phase.TemperatureC.At(progress) - lagC * 4 * progress * (1 - progress);

    /// <summary>The temperature (no noise) elapsedMinutes into a segment, at the given phase progress.</summary>
    public static double Temperature(PlantPhase phase, PlantBatchSegment batch, string state, double elapsedMinutes, double progress)
    {
        if (state != "Running") return AmbientC + (batch.StartC - AmbientC) * Math.Exp(-elapsedMinutes / CoolingMinutes);
        double target = Target(phase, batch.LagC, progress), gap = batch.StartC - Target(phase, batch.LagC, batch.From);
        return gap < 0 ? Math.Min(target, batch.StartC + ReheatRateC * elapsedMinutes) : target + gap * Math.Exp(-elapsedMinutes / SettleMinutes);
    }

    /// <summary>The heating rate (°C per minute, never negative) while running; 0 when stopped or cooling.</summary>
    public static double HeatRate(PlantPhase phase, PlantBatchSegment batch, string state, double elapsedMinutes, double progress)
    {
        if (state != "Running") return 0;
        double gap = batch.StartC - Target(phase, batch.LagC, batch.From);
        if (gap < 0 && batch.StartC + ReheatRateC * elapsedMinutes < Target(phase, batch.LagC, progress)) return ReheatRateC; // still catching up
        double minutes = batch.PlannedSeconds / 60.0;
        double ramp = progress < 1 ? (phase.TemperatureC.End - phase.TemperatureC.Start - batch.LagC * 4 * (1 - 2 * progress)) / minutes : 0;
        double settling = gap > 0 ? -gap / SettleMinutes * Math.Exp(-elapsedMinutes / SettleMinutes) : 0;
        return Math.Max(0, ramp + settling);
    }
}
