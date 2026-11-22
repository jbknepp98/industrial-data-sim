// flow-data-simulator-web: the interview page and in-container plant hosting.
//
// Environment (configuration is environment variables only, like the CLI):
//   FLOWSIM_PG_CONNECTION      flow_data_sim connection string (required)
//   TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET, TIMEBASE_CA_BUNDLE
//                              the Pulse client: Gage for the interview, Historian for plant tags
//   TIMEBASE_HISTORIAN_URL     Historian for plant tags (see HistorianConnection)
//   TIMEBASE_DATASET           the Dataset approved plants write their tags to
//   FLOWSIM_GAGE_MODEL         Gage handle, default anthropic/claude
//   FLOWSIM_STORYLINES         storyline folder, default ./storylines next to the program
//   FLOWSIM_WEB_URLS           listen address, default http://127.0.0.1:8480
//   FLOWSIM_WEB_ALLOWED_HOSTS  accepted Host names, default localhost;127.0.0.1;[::1]
// Secrets are never logged or echoed.
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;
using IndustrialDataSim.Web;

var log = new JsonLog(Console.Out);
string? Setting(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : null;

string? connection = Setting("FLOWSIM_PG_CONNECTION");
if (connection is null)
{
    log.Write("web.missing_setting", null, new { code = "web.database_unconfigured", message = "Set FLOWSIM_PG_CONNECTION to the flow_data_sim connection string (for example in the private env file passed with --env-file). Interviews and plants are stored there." });
    return 2;
}

string libraryFolder = Setting("FLOWSIM_STORYLINES") ?? Path.Combine(AppContext.BaseDirectory, "storylines");
var (library, libraryErrors) = InterviewLibrary.Load(libraryFolder);
if (library is null)
{
    foreach (var error in libraryErrors) log.Write("web.library_invalid", null, new { code = error.Code, path = error.Path, message = error.Message });
    return 3;
}
string template;
try { template = File.ReadAllText(Path.Combine(libraryFolder, "demo-guide.md")); }
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    log.Write("web.library_invalid", null, new { code = "web.demo_guide_unreadable", message = "Could not read demo-guide.md in the storyline folder. Check that the folder is complete and readable." });
    return 3;
}

// One Gage client for the process, created on first use so a missing Pulse
// setting is reported by the Gage check and the page banner, not at startup.
GageClient? gage = null;
var gageLock = new object();
GageClient Gage()
{
    lock (gageLock) return gage ??= new GageClient(GageSettings.FromEnvironment());
}

using var interviews = new PostgresInterviewStore(connection);
using var plants = new PlantStore(connection);
using var registry = new PostgresWebPlantRegistry(connection);
var services = new WebServices
{
    Library = library,
    Interviews = interviews,
    Plants = plants,
    Registry = registry,
    Model = () => new GageInterviewModel(Gage()),
    Gage = new GageHealth(stop => Gage().Check(stop), Setting("FLOWSIM_GAGE_MODEL") ?? GageSettings.DefaultModel),
    Historian = _ => new HistorianClient(HistorianConnection.FromEnvironment()),
    Dataset = Setting("TIMEBASE_DATASET"),
    Log = log,
    DemoGuideTemplate = template
};
var options = new WebOptions
{
    Urls = Setting("FLOWSIM_WEB_URLS") ?? new WebOptions().Urls,
    AllowedHosts = Setting("FLOWSIM_WEB_ALLOWED_HOSTS") ?? new WebOptions().AllowedHosts
};

var app = WebApp.Build(options, services);
await app.RunAsync();
gage?.Dispose();
return 0;
