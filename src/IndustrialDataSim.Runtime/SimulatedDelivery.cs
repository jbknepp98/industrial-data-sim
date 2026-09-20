namespace IndustrialDataSim.Runtime;

/// <summary>
/// Exercises durable delivery only against the sealed in-memory fake. There is
/// no URL, credential, HTTP adapter, or production transport switch in this API.
/// </summary>
public sealed class SimulatedDelivery(DurableRuntime runtime, FakeHistorian historian)
{
    internal Action<string>? FaultPoint { get; set; }

    public async Task<int> RunRoundAsync(CancellationToken cancellationToken = default)
    {
        int delivered = 0;
        foreach (string id in runtime.DeliveryOrder())
            if (await DeliverOneAsync(id, cancellationToken)) delivered++;
        return delivered;
    }

    public async Task<bool> DeliverOneAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        runtime.RequireMode(ExecutionMode.Simulation);
        cancellationToken.ThrowIfCancellationRequested(); // No durable Sending yet.
        var work = runtime.Claim(sessionId);
        if (work is null) return false;
        bool accepted;
        try { accepted = await historian.WriteAsync(work, cancellationToken); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or RuntimeFailure)
        {
            // Even a failure that the fake knows happened before acceptance is
            // treated as uncertain: no such production guarantee is established.
            accepted = false;
        }
        FaultPoint?.Invoke("after_transport_before_acknowledgement");
        runtime.Finish(work, accepted);
        return accepted;
    }
}
