using System.Collections.Concurrent;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Web;

/// <summary>Everything the endpoints and the hosting service use. Program builds it from the environment; tests build fakes.</summary>
public sealed class WebServices
{
    public required InterviewLibrary Library { get; init; }
    public required IInterviewStore Interviews { get; init; }
    public required PlantStore Plants { get; init; }
    public required IWebPlantRegistry Registry { get; init; }
    /// <summary>The model behind interviews; throws a gage.* RuntimeFailure when Gage is not configured.</summary>
    public required Func<IInterviewModel> Model { get; init; }
    public required GageHealth Gage { get; init; }
    /// <summary>The Historian client for one hosted plant; null delivers rows only (tests).</summary>
    public required Func<string, HistorianClient?> Historian { get; init; }
    /// <summary>TIMEBASE_DATASET: the Dataset approved plants write their tags to.</summary>
    public string? Dataset { get; init; }
    public required JsonLog Log { get; init; }
    /// <summary>The demo guide template (examples/storylines/demo-guide.md).</summary>
    public required string DemoGuideTemplate { get; init; }
    public InterviewOptions? InterviewOptions { get; init; }
    public PlantHostingOptions? HostingOptions { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public InterviewGate Gate { get; } = new();
}

/// <summary>Host settings: where to listen and which Host headers to accept.</summary>
public sealed class WebOptions
{
    /// <summary>
    /// FLOWSIM_WEB_URLS. Default http://127.0.0.1:8480: loopback only. In
    /// Docker the container listens on 0.0.0.0:8480 and the host publishes it
    /// as 127.0.0.1:&lt;port&gt;:8480, so the page stays on the host (it has no login).
    /// </summary>
    public string Urls { get; init; } = "http://127.0.0.1:8480";
    /// <summary>
    /// FLOWSIM_WEB_ALLOWED_HOSTS, semicolon separated. Requests with any other
    /// Host header are refused, which blocks DNS-rebinding pages from driving
    /// the API. Add the machine name here if the page is opened by name.
    /// </summary>
    public string AllowedHosts { get; init; } = "localhost;127.0.0.1;[::1]";
    /// <summary>Run the Gage check when the container starts (one short metered message).</summary>
    public bool CheckGageAtStartup { get; init; } = true;
    public bool HostPlants { get; init; } = true;
}

/// <summary>
/// One message at a time per interview. A turn holds its interview's gate from
/// the moment the message is accepted until the turn is stored, even if the
/// browser disconnects; a second send (or an approve) meanwhile gets 409.
/// The gate is per process: one web container serves an interview.
/// </summary>
public sealed class InterviewGate
{
    private readonly ConcurrentDictionary<Guid, byte> busy = new();

    public bool IsBusy(Guid id) => busy.ContainsKey(id);

    /// <summary>Returns a releaser, or null when the interview is already busy.</summary>
    public IDisposable? TryEnter(Guid id) => busy.TryAdd(id, 0) ? new Releaser(this, id) : null;

    private sealed class Releaser(InterviewGate gate, Guid id) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.busy.TryRemove(id, out _); }
    }
}
