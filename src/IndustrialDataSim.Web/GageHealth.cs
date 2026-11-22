using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Web;

/// <summary>The latest known answer to "can the interview reach a model through Pulse?".</summary>
public sealed record GageStatus(bool Ok, string State, string? Code, string? Message, string? Model, string? AnsweredBy, DateTimeOffset? CheckedUtc);

/// <summary>
/// Gage reachability for /api/health and the page's "explain and stop"
/// banner.
///
/// A full check (token, model catalog, one short test message) runs when the
/// container starts and when the page asks for a recheck, at most once per
/// <see cref="MinimumRecheck"/>, because every check is a metered model call.
/// Between checks, interview turns update the status: a gage.* stop marks it
/// failed with that turn's explanation, and a turn that got a reply marks it
/// working again.
/// </summary>
public sealed class GageHealth(Func<CancellationToken, Task<GageCheckResult>> check, string? model)
{
    public static readonly TimeSpan MinimumRecheck = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim running = new(1, 1);
    private GageStatus status = new(false, "unchecked", null, "The Gage check has not run yet.", model, null, null);

    public GageStatus Current => status;

    /// <summary>Runs the check unless one ran within MinimumRecheck (or one is running, which this waits for).</summary>
    public async Task<GageStatus> Check(bool force, CancellationToken stop)
    {
        await running.WaitAsync(stop);
        try
        {
            if (status.CheckedUtc is { } last && (!force || DateTimeOffset.UtcNow - last < MinimumRecheck)) return status;
            try
            {
                var result = await check(stop);
                status = new(true, "ok", null, null, result.Model, result.AnsweredBy, DateTimeOffset.UtcNow);
            }
            catch (RuntimeFailure failure)
            {
                status = new(false, "stopped", failure.Error.Code, failure.Message, model, null, DateTimeOffset.UtcNow);
            }
            return status;
        }
        finally { running.Release(); }
    }

    /// <summary>An interview turn stopped with a gage.* failure.</summary>
    public void ReportFailure(string code, string message) =>
        status = new(false, "stopped", code, message, model, null, DateTimeOffset.UtcNow);

    /// <summary>An interview turn got at least one reply from the model.</summary>
    public void ReportWorking()
    {
        if (!status.Ok) status = new(true, "ok", null, null, model, status.AnsweredBy, DateTimeOffset.UtcNow);
    }
}
