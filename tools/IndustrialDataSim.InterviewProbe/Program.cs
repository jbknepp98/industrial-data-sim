// Interview probe: runs the phase 6 interview from a terminal against the
// live Pulse Gage proxy, with sessions saved in flow_data_sim.
//
//   InterviewProbe [--library <storyline folder>] [--script <file>] [--resume <interview id>] [--memory]
//
// --script plays one user message per non-empty line; on --resume the lines
// already sent are skipped, so a killed run can be restarted with the same
// script. Without --script it reads messages from standard input (an empty
// line or end of input ends the interview). --memory keeps the session in
// process instead of Postgres.
//
// Settings come from the environment, as for the simulator container:
// TIMEBASE_PULSE_URL, TIMEBASE_CLIENT_ID, TIMEBASE_CLIENT_SECRET,
// TIMEBASE_CA_BUNDLE, optional FLOWSIM_GAGE_MODEL, and FLOWSIM_PG_CONNECTION.
// Secrets are never printed. Nothing is built or run: the probe ends at the
// preview, like the interview itself.
using System.Text.Json;
using IndustrialDataSim.Core.Plant;
using IndustrialDataSim.Interview;
using IndustrialDataSim.Runtime;

string? libraryFolder = null, scriptFile = null, resumeId = null;
bool memory = false;
for (int i = 0; i < args.Length; i++)
{
    string? Value() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--library": libraryFolder = Value(); break;
        case "--script": scriptFile = Value(); break;
        case "--resume": resumeId = Value(); break;
        case "--memory": memory = true; break;
        default: return Usage($"Unknown argument \"{args[i]}\".");
    }
}

libraryFolder ??= new[] { "examples/storylines", "/work/storylines" }.FirstOrDefault(Directory.Exists);
if (libraryFolder is null) return Usage("Pass --library with the storyline folder (examples/storylines).");
var (library, libraryErrors) = InterviewLibrary.Load(libraryFolder);
if (library is null)
{
    foreach (var error in libraryErrors) Console.Error.WriteLine($"[library] {error.Code} {error.Path}: {error.Message}");
    return 3;
}

List<string>? script = null;
if (scriptFile is not null)
{
    try { script = File.ReadAllLines(scriptFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Usage("Could not read the script file. Check that it exists and is readable."); }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

try
{
    var settings = GageSettings.FromEnvironment();
    using var gage = new GageClient(settings);
    IInterviewStore store = memory ? new InMemoryInterviewStore() : PostgresInterviewStore.FromEnvironment();
    var model = new GageInterviewModel(gage);

    InterviewSession session;
    if (resumeId is not null)
    {
        if (!Guid.TryParse(resumeId, out var id)) return Usage("--resume needs the interview id the probe printed.");
        session = InterviewSession.Resume(id, library, model, store);
        Console.WriteLine($"[probe] resumed interview {session.Id} with {session.UserMessageCount} user message(s) stored; model {settings.Model}");
        if (session.NeedsContinue)
        {
            Console.WriteLine("[probe] the previous run stopped mid-turn; finishing that turn");
            if (!Report(await session.Continue(Print, stop.Token))) return 1;
        }
    }
    else
    {
        session = InterviewSession.Start(library, model, store);
        Console.WriteLine($"[probe] interview {session.Id} started; model {settings.Model}");
    }

    int sent = session.UserMessageCount;
    while (true)
    {
        string? text;
        if (script is not null)
        {
            if (sent >= script.Count) break;
            text = script[sent];
        }
        else
        {
            Console.Write("[you] ");
            text = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(text)) break;
        }
        if (script is not null) Console.WriteLine($"[you] {text}");
        sent++;
        if (!Report(await session.Send(text, Print, stop.Token))) return 1;
    }

    // Summary: draft validity, preview, and the interview's cost in tokens.
    var draft = session.State.DraftModelJson is { } json ? PlantModelLoader.Load(json, DateTimeOffset.UtcNow) : null;
    var u = session.TotalUsage;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        interviewId = session.Id,
        draftValid = draft?.IsValid,
        simulation = draft?.Model?.Simulation,
        previewStored = session.State.PreviewJson is not null,
        previewBytes = session.State.PreviewJson?.Length,
        storylines = session.State.Needs["chosenStorylines"]?.ToJsonString(),
        requests = InterviewTools.Requests(session.State).ToJsonString(), // unmet asks, recorded from find_storylines askedFor
        usage = new { inputTokens = u.InputTokens, outputTokens = u.OutputTokens, cacheReadTokens = u.CacheReadTokens, cacheWriteTokens = u.CacheWriteTokens }
    }));
    return 0;
}
catch (RuntimeFailure failure)
{
    Console.Error.WriteLine($"[stopped] {failure.Error.Code}: {failure.Message}");
    return 1;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.Error.WriteLine("[probe] interrupted; rerun with --resume to continue");
    return 130;
}

static void Print(InterviewEvent e) => Console.WriteLine(e.Kind switch
{
    InterviewEventKind.AssistantText => "[interviewer] " + e.Text,
    InterviewEventKind.ToolActivity => "[tool] " + e.Text,
    InterviewEventKind.PreviewReady => "[preview] " + e.Text,
    InterviewEventKind.Stopped => $"[stopped] {e.Code}: {e.Text}",
    InterviewEventKind.Refusal => "[refusal] " + e.Text,
    _ => $"[error] {e.Code}: {e.Text}"
});

static bool Report(InterviewTurnResult turn)
{
    Console.WriteLine($"[turn] {turn.Outcome}: {turn.ModelCalls} model call(s), {turn.ToolRounds} tool round(s), tokens in {turn.Usage.InputTokens} / out {turn.Usage.OutputTokens} / cache read {turn.Usage.CacheReadTokens}");
    return turn.Outcome != InterviewOutcome.Stopped;
}

static int Usage(string problem)
{
    Console.Error.WriteLine(problem + " Usage: InterviewProbe [--library <storyline folder>] [--script <file>] [--resume <interview id>] [--memory]");
    return 2;
}
