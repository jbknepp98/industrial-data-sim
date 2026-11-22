using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using IndustrialDataSim.Runtime;

namespace IndustrialDataSim.Interview;

/// <summary>The model behind the interview. Production uses <see cref="GageInterviewModel"/>; tests script replies.</summary>
public interface IInterviewModel
{
    /// <summary>One Messages API call. Failures are <see cref="RuntimeFailure"/>s with gage.* codes that explain what to fix.</summary>
    Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop);
}

/// <summary>Claude through the Pulse Gage proxy (phase 5).</summary>
public sealed class GageInterviewModel(GageClient client) : IInterviewModel
{
    public Task<Message> CreateMessage(GageMessageRequest request, CancellationToken stop) => client.CreateMessage(request, stop);
}

public enum InterviewEventKind
{
    /// <summary>Text the interviewer wrote, to show in the chat.</summary>
    AssistantText,
    /// <summary>A tool is running, for example "Checking the plant model…".</summary>
    ToolActivity,
    /// <summary>A new preview is stored and can be shown.</summary>
    PreviewReady,
    /// <summary>The turn ended early (round or length limit, unexpected stop). The user can send a message to continue.</summary>
    Error,
    /// <summary>Gage could not be used ("explain and stop"): Text names the Pulse setting to fix.</summary>
    Stopped,
    /// <summary>The model declined the request.</summary>
    Refusal
}

public sealed record InterviewEvent(InterviewEventKind Kind, string Text, string? Code = null);

public static class InterviewOutcome
{
    public const string Completed = "completed", Stopped = "stopped", Refused = "refused", RoundLimit = "round_limit", LengthLimit = "length_limit", UnexpectedStop = "unexpected_stop";
}

/// <summary>How one user turn ended, with the tokens it used.</summary>
public sealed record InterviewTurnResult(string Outcome, InterviewUsage Usage, int ModelCalls, int ToolRounds);

public sealed class InterviewOptions
{
    /// <summary>Tool rounds allowed in one user turn before the turn is cut off.</summary>
    public int MaxToolRounds { get; init; } = 12;
    /// <summary>Always sent; GageClient streams, so a large value does not risk an HTTP timeout.</summary>
    public int MaxTokens { get; init; } = 16000;
    public int MaxUserMessageCharacters { get; init; } = 8000;
}

/// <summary>
/// One interview: a manual tool loop on the Gage client that the app owns.
///
/// A user turn appends the user's message, then repeats: ask the model; if it
/// called tools, run them all and return every result in one user message;
/// until the model ends its turn. Every message is stored before the next step
/// (append-only), and the status says whether a turn is in progress, so a
/// process killed mid-turn resumes exactly where it stopped
/// (<see cref="NeedsContinue"/>, <see cref="Continue"/>).
///
/// Stop reasons:
/// - end_turn / stop_sequence: the turn is complete;
/// - tool_use: run the tools and continue (at most MaxToolRounds rounds);
/// - max_tokens: continue once, telling the model it was cut off; a second
///   cut-off ends the turn with an Error event;
/// - refusal: the reply is not kept in the history; the page shows a plain message;
/// - anything else: kept where valid, and the turn ends with an Error event.
/// A gage.* failure (no model reachable, policy, throttling …) ends the turn
/// with a Stopped event carrying the explanation; nothing is retried.
/// </summary>
public sealed class InterviewSession
{
    private readonly IInterviewModel model;
    private readonly IInterviewStore store;
    private readonly InterviewTools tools;
    private readonly InterviewOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly string systemPrompt;
    /// <summary>The replayable history (user and assistant messages), each as the exact JSON stored.</summary>
    private readonly List<string> history = [];
    private string status;

    private InterviewSession(Guid id, InterviewLibrary library, IInterviewModel model, IInterviewStore store, InterviewOptions? options,
        Func<DateTimeOffset>? clock, InterviewRecord record)
    {
        Id = id;
        this.model = model;
        this.store = store;
        this.options = options ?? new InterviewOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        tools = new InterviewTools(library, this.clock);
        systemPrompt = InterviewPrompt.Build(library);
        status = record.Status;
        State = new InterviewState
        {
            Needs = record.NeedsJson is { } needs ? JsonNode.Parse(needs)!.AsObject() : new JsonObject(),
            DraftModelJson = record.DraftModelJson,
            PreviewJson = record.PreviewJson
        };
        foreach (var message in record.Messages.Where(m => m.Role is "user" or "assistant")) history.Add(message.ContentJson);
        TotalUsage = record.Messages.Where(m => m.Usage is not null).Aggregate(InterviewUsage.None, (sum, m) => sum.Add(m.Usage!));
    }

    public Guid Id { get; }
    public InterviewState State { get; }
    public InterviewUsage TotalUsage { get; private set; }
    public IReadOnlyList<ToolUnion> ToolDefinitions => tools.Definitions;
    public string SystemPrompt => systemPrompt;

    /// <summary>True when the process stopped in the middle of a turn; call <see cref="Continue"/> to finish it.</summary>
    public bool NeedsContinue => status == InterviewStatus.InTurn;

    /// <summary>The number of user messages the user typed (tool results and continuation notes excluded).</summary>
    public int UserMessageCount => history.Count(IsTypedUserMessage);

    public static InterviewSession Start(InterviewLibrary library, IInterviewModel model, IInterviewStore store,
        InterviewOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        var id = Guid.NewGuid();
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        store.Create(id, now);
        return new InterviewSession(id, library, model, store, options, clock, store.Load(id)!);
    }

    public static InterviewSession Resume(Guid id, InterviewLibrary library, IInterviewModel model, IInterviewStore store,
        InterviewOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        var record = store.Load(id) ?? throw new RuntimeFailure("interview.not_found", "No interview with this id is stored in flow_data_sim. Start a new interview.");
        return new InterviewSession(id, library, model, store, options, clock, record);
    }

    /// <summary>Runs one user turn. An interrupted earlier turn is finished first.</summary>
    public async Task<InterviewTurnResult> Send(string userText, Action<InterviewEvent>? onEvent = null, CancellationToken stop = default)
    {
        string text = userText.Trim();
        if (text.Length == 0)
            throw new RuntimeFailure("interview.empty_message", "Type a message for the interviewer.");
        if (text.Length > options.MaxUserMessageCharacters)
            throw new RuntimeFailure("interview.message_too_long", $"Keep a message under {options.MaxUserMessageCharacters} characters.");
        if (NeedsContinue) await Continue(onEvent, stop);

        var content = new JsonArray();
        // The first turn carries the per-session facts, so the system prompt stays identical across sessions.
        if (history.Count == 0) content.Add(TextBlock(InterviewPrompt.SessionFacts(Id, clock())));
        content.Add(TextBlock(text));
        Append([("user", Message("user", content), null)], InterviewStatus.InTurn);
        return await RunTurn(onEvent ?? (_ => { }), stop);
    }

    /// <summary>Finishes a turn the process was killed in: runs pending tool calls or asks the model again.</summary>
    public Task<InterviewTurnResult> Continue(Action<InterviewEvent>? onEvent = null, CancellationToken stop = default) =>
        NeedsContinue && history.Count > 0
            ? RunTurn(onEvent ?? (_ => { }), stop)
            : Task.FromResult(new InterviewTurnResult(InterviewOutcome.Completed, InterviewUsage.None, 0, 0));

    private async Task<InterviewTurnResult> RunTurn(Action<InterviewEvent> emit, CancellationToken stop)
    {
        int rounds = 0, calls = 0;
        bool continuedAfterLength = false;
        var usage = InterviewUsage.None;
        InterviewTurnResult Result(string outcome) => new(outcome, usage, calls, rounds);

        while (true)
        {
            var last = JsonNode.Parse(history[^1])!.AsObject();
            if (last["role"]!.GetValue<string>() == "assistant")
            {
                var toolUses = ToolUses(last);
                if (toolUses.Count == 0)
                {
                    SetStatus(InterviewStatus.Open);
                    return Result(InterviewOutcome.Completed);
                }
                if (rounds >= options.MaxToolRounds)
                {
                    // Close the pending calls so the history stays valid, then hand the turn back to the user.
                    var closing = ErrorResults(toolUses, $"Not run: this turn reached its limit of {options.MaxToolRounds} tool rounds. Answer the user with what you have, and ask how to continue.");
                    Append([("user", Message("user", closing), null), Notice("round_limit")], InterviewStatus.Open);
                    emit(new(InterviewEventKind.Error, $"The interviewer used {options.MaxToolRounds} tool steps without finishing this answer. Send a message (for example \"continue\") to carry on.", "interview.round_limit"));
                    return Result(InterviewOutcome.RoundLimit);
                }
                rounds++;
                var results = new JsonArray();
                foreach (var (toolId, name, input) in toolUses)
                {
                    emit(new(InterviewEventKind.ToolActivity, InterviewTools.Activity(name)));
                    var outcome = tools.Execute(name, input, State);
                    if (outcome.NeedsChanged || outcome.DraftChanged || outcome.PreviewChanged)
                        store.SaveState(Id, State.Needs.ToJsonString(), State.DraftModelJson, State.PreviewJson);
                    if (outcome.PreviewChanged && State.PreviewJson is not null)
                        emit(new(InterviewEventKind.PreviewReady, "The plant preview is ready."));
                    results.Add(ToolResult(toolId, outcome.Content, outcome.IsError));
                }
                Append([("user", Message("user", results), null)], InterviewStatus.InTurn);
                continue;
            }

            // The last message is the user's (text or tool results): ask the model.
            Message reply;
            try
            {
                reply = await model.CreateMessage(new GageMessageRequest(BuildMessages(), options.MaxTokens) { System = systemPrompt, Tools = tools.Definitions }, stop);
            }
            catch (RuntimeFailure failure)
            {
                Append([Notice(failure.Error.Code)], InterviewStatus.Open);
                emit(new(InterviewEventKind.Stopped, failure.Message, failure.Error.Code));
                return Result(InterviewOutcome.Stopped);
            }
            calls++;
            var replyUsage = UsageOf(reply);
            usage = usage.Add(replyUsage);
            TotalUsage = TotalUsage.Add(replyUsage);

            var content = JsonNode.Parse(JsonSerializer.Serialize(reply.Content))!.AsArray();
            string assistantText = string.Concat(content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>()));
            string stopReason = reply.StopReason?.Raw() ?? "";
            var replyToolUses = ToolUses(new JsonObject { ["content"] = content.DeepClone() });

            if (stopReason == "refusal")
            {
                // Not kept: replaying refused content would only invite the same refusal.
                Append([Notice("refusal", replyUsage)], InterviewStatus.Open);
                emit(new(InterviewEventKind.Refusal, "The model declined to continue with that request. Rephrase it, or ask about a different part of the demo.", "interview.refusal"));
                return Result(InterviewOutcome.Refused);
            }

            if (assistantText.Length > 0) emit(new(InterviewEventKind.AssistantText, assistantText));
            var assistant = content.Count > 0 ? ("assistant", Message("assistant", content), replyUsage) : ((string, string, InterviewUsage?)?)null;

            switch (stopReason)
            {
                case "tool_use" when replyToolUses.Count > 0:
                    Append([assistant!.Value], InterviewStatus.InTurn);
                    continue;

                case "end_turn" or "stop_sequence" or "tool_use":
                    if (assistant is { } done && replyToolUses.Count == 0) Append([done], InterviewStatus.Open);
                    else if (assistant is { } withTools)
                        Append([withTools, ("user", Message("user", ErrorResults(replyToolUses, "Not run: the turn ended.")), null)], InterviewStatus.Open);
                    else Append([Notice("empty_reply", replyUsage)], InterviewStatus.Open);
                    return Result(InterviewOutcome.Completed);

                case "max_tokens" when !continuedAfterLength:
                {
                    continuedAfterLength = true;
                    // Continue once. Truncated tool calls may be incomplete, so they are not run.
                    var followUp = ErrorResults(replyToolUses, "Not run: your reply hit the length limit before this call was complete. Call it again.");
                    followUp.Add(TextBlock("[Note from the interview service] Your previous reply was cut off by the length limit. Continue from where it stopped, more briefly."));
                    var appended = new List<(string, string, InterviewUsage?)>();
                    if (assistant is { } partial) appended.Add(partial);
                    appended.Add(("user", Message("user", followUp), null));
                    Append(appended, InterviewStatus.InTurn);
                    continue;
                }

                default:
                {
                    // A second max_tokens, or a stop reason this loop does not expect (pause_turn,
                    // model_context_window_exceeded, a proxy's own value …): keep what is valid and stop.
                    var appended = new List<(string, string, InterviewUsage?)>();
                    if (assistant is { } kept) appended.Add(kept);
                    if (assistant is not null && replyToolUses.Count > 0)
                        appended.Add(("user", Message("user", ErrorResults(replyToolUses, "Not run: the turn ended early.")), null));
                    bool length = stopReason == "max_tokens";
                    appended.Add(Notice(length ? "length_limit" : "unexpected_stop", assistant is null ? replyUsage : null));
                    Append(appended, InterviewStatus.Open);
                    emit(length
                        ? new(InterviewEventKind.Error, "The interviewer's answer was cut off twice by the length limit. Send a message asking for a shorter answer to continue.", "interview.length_limit")
                        : new(InterviewEventKind.Error, $"The model stopped for a reason the interview does not handle ({SafeStopReason(stopReason)}). Send a message to continue.", "interview.unexpected_stop"));
                    return Result(length ? InterviewOutcome.LengthLimit : InterviewOutcome.UnexpectedStop);
                }
            }
        }
    }

    // ---------------- history ----------------

    private List<MessageParam> BuildMessages() => history.Select(json => JsonSerializer.Deserialize<MessageParam>(json)!).ToList();

    private void Append(IReadOnlyList<(string Role, string ContentJson, InterviewUsage? Usage)> messages, string newStatus)
    {
        store.Append(Id, messages, newStatus);
        foreach (var message in messages.Where(m => m.Role is "user" or "assistant")) history.Add(message.ContentJson);
        status = newStatus;
    }

    private void SetStatus(string newStatus)
    {
        if (status == newStatus) return;
        store.SetStatus(Id, newStatus);
        status = newStatus;
    }

    /// <summary>A notice row: what the page was told. Never replayed. Usage is kept when the reply itself is not stored.</summary>
    private static (string, string, InterviewUsage?) Notice(string code, InterviewUsage? usage = null) =>
        ("notice", new JsonObject { ["code"] = code }.ToJsonString(), usage);

    private static string Message(string role, JsonArray content) => new JsonObject { ["role"] = role, ["content"] = content }.ToJsonString();

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ToolResult(string toolUseId, string content, bool isError)
    {
        var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = content };
        if (isError) block["is_error"] = true;
        return block;
    }

    private static JsonArray ErrorResults(IEnumerable<(string Id, string Name, JsonElement Input)> toolUses, string reason) =>
        new(toolUses.Select(t => (JsonNode)ToolResult(t.Id, new JsonObject { ["valid"] = false, ["errors"] = new JsonArray(new JsonObject { ["code"] = "tool.not_run", ["path"] = "$", ["message"] = reason }) }.ToJsonString(), true)).ToArray());

    private static List<(string Id, string Name, JsonElement Input)> ToolUses(JsonObject message) =>
        (message["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "tool_use")
            .Select(b => (b["id"]!.GetValue<string>(), b["name"]!.GetValue<string>(), JsonSerializer.SerializeToElement(b["input"])))
            .ToList();

    private static bool IsTypedUserMessage(string json)
    {
        var message = JsonNode.Parse(json)!.AsObject();
        if (message["role"]?.GetValue<string>() != "user") return false;
        var blocks = (message["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        return blocks.All(b => b["type"]?.GetValue<string>() == "text") &&
            !blocks.Any(b => b["text"]?.GetValue<string>()?.StartsWith("[Note from the interview service]", StringComparison.Ordinal) == true);
    }

    /// <summary>A stop reason comes from the provider or proxy: show only a short run of safe characters.</summary>
    private static string SafeStopReason(string stopReason)
    {
        string safe = new(stopReason.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').Take(60).ToArray());
        return safe.Length == 0 ? "no stop reason" : safe;
    }

    private static InterviewUsage UsageOf(Message reply) => new(reply.Usage.InputTokens, reply.Usage.OutputTokens,
        reply.Usage.CacheReadInputTokens ?? 0, reply.Usage.CacheCreationInputTokens ?? 0);
}
