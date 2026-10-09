using System.Runtime.CompilerServices;
using System.Text;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Infrastructure.Agent.Tracing;
using Chatbot.Infrastructure.Llm;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using DbMessage = Chatbot.Core.Domain.ChatMessage;

namespace Chatbot.Infrastructure.Agent;

/// <summary>
/// Runs the agent for one user message (SPEC 7.4): validates, creates the <see cref="AgentRun"/>, builds the model
/// context, streams the answer while the tools run, and closes the run with tokens, cost and duration.
/// One instance per request (scoped): it shares its <see cref="ChatbotDbContext"/> with the tools.
/// </summary>
public sealed class AgentService(
    ChatbotDbContext db,
    [FromKeyedServices(LlmClientFactory.ServiceKey)] IChatClient llm,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    ILogger<AgentService> logger)
{
    private const string UnknownModel = "unknown";

    /// <summary>Starts the last paragraph of the system prompt, so the language rule is easy to find and outranks the persona text.</summary>
    public const string LanguageLabel = "LANGUAGE (highest priority): ";
    private const int ModelColumnLength = 200; // agent_runs.Model

    /// <summary>Consecutive tool rounds that may fail with an exception before the run is aborted.</summary>
    private const int MaxConsecutiveToolErrors = 3;

    /// <summary>
    /// Processes <paramref name="text"/> in the session. The stream is either a single <see cref="Rejected"/>
    /// (nothing was stored), or <see cref="RunStarted"/>, <see cref="Token"/>s and <see cref="Trace"/>s interleaved
    /// (each step is sent as soon as it completes), an optional <see cref="Handoff"/> and <see cref="RunCompleted"/>,
    /// or the same up to <see cref="Error"/>. Provider and tool failures never
    /// escape as exceptions. If the caller cancels or stops enumerating, the run is closed as <c>Error</c>.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        Guid sessionId, string? text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Prepared prepared;
        try
        {
            prepared = await PrepareAsync(sessionId, text, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            yield break;
        }

        if (prepared.Rejection is { } rejection)
        {
            yield return rejection;
            yield break;
        }

        var run = prepared.Run!;
        try
        {
            yield return new RunStarted(run.Id);

            // yield is not allowed inside a try with a catch, so MoveNextAsync is guarded on its own and the events
            // are yielded outside the try.
            Exception? failure = null;
            await using (var stream = StreamModelAsync(run, ct).GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    try
                    {
                        if (!await stream.MoveNextAsync())
                            break;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }

                    yield return stream.Current;
                }
            }

            RunCompleted? completed = null;
            if (failure is null)
            {
                try
                {
                    completed = await CompleteAsync(run);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (completed is null)
            {
                // The client left: nobody to tell. The finally block closes the run as cancelled.
                if (ct.IsCancellationRequested)
                    yield break;

                logger.LogError(failure, "Agent run {RunId} failed", run.Id);
                RecordFailure(run, FailureTraceFor(failure!));
                await TryFailAsync(run);

                // The failed model call (if any) and the failure itself, which were recorded after the last drain.
                foreach (var traced in run.Recorder.TakeNew())
                    yield return Trace.From(traced);

                // A tool may already have handed the session to a person: the client still needs to show that.
                if (run.Tools.HandoffReason is { } handoffReason)
                    yield return new Handoff(handoffReason);

                var (message, retryable) = Describe(failure!);
                yield return new Error(message, retryable);
                yield break;
            }

            if (run.Tools.HandoffReason is { } reason)
                yield return new Handoff(reason);

            yield return completed;
        }
        finally
        {
            // Reached with an open run when the consumer cancelled or stopped enumerating early.
            if (!run.Closed)
                await TryFailAsync(run);
        }
    }

    /// <summary>
    /// The model pipeline for one request, outermost first. Only the bare provider client is shared (a singleton);
    /// the wrappers are cheap and per request. They are never disposed, because a delegating client disposes the
    /// client below it, which here is the shared singleton.
    /// </summary>
    private IChatClient BuildPipeline(RunState run) =>
        new ChatClientBuilder(llm)
            // Verified against Microsoft.Extensions.AI 10.10 (see the iteration limit test): the limit counts tool
            // rounds, so N allows N rounds of tool calls plus the model call that follows the last one (N + 1 calls
            // at most). When that last call asks for tools again the loop stops WITHOUT throwing and without
            // running them: the stream just ends after the function call updates, with no result. StreamModelAsync
            // turns "calls without results" into an error.
            //
            // A tool that throws (typically arguments that do not fit the parameter types, which fails before our code
            // runs) does not end the run: the exception becomes the tool result so the model can correct itself, up
            // to MaximumConsecutiveErrorsPerRequest rounds in a row, then it is rethrown. IncludeDetailedErrors puts
            // the exception text in that result. It reaches only the model; the user stream gets a generic message.
            //
            // FunctionInvoker replaces the call of each tool, which is where ToolCall events are recorded.
            .UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = options.Value.MaxIterations;
                client.MaximumConsecutiveErrorsPerRequest = MaxConsecutiveToolErrors;
                client.IncludeDetailedErrors = true;
                client.FunctionInvoker = new ToolCallTracer(run.Recorder).InvokeAsync;
            })
            // ChatClientBuilder wraps in call order (first Use = outermost), so a `.Use(...)` placed below
            // UseFunctionInvocation sees every individual model call.
            .Use(inner => new TracingChatClient(inner, run.Recorder))
            .Build();

    private async Task<Prepared> PrepareAsync(Guid sessionId, string? text, CancellationToken ct)
    {
        var limits = options.Value;

        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
            return Prepared.Reject(RejectionReason.SessionNotFound, "Session not found.");
        if (session.Status == SessionStatus.Closed)
            return Prepared.Reject(RejectionReason.SessionClosed, "This session is closed. Start a new one.");

        text = text?.Trim() ?? "";
        if (text.Length == 0)
            return Prepared.Reject(RejectionReason.EmptyText, "Message is empty.");
        if (text.Length > limits.MaxInputChars)
            return Prepared.Reject(RejectionReason.TextTooLong, $"Message is longer than {limits.MaxInputChars} characters.");

        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == session.BusinessId, ct);
        var now = clock.GetUtcNow();

        var language = LanguageDetector.Detect(text, ParseLanguage(session.Language));
        session.Language = LanguageCode(language);
        session.LastActivityAt = now;

        // Fail-closed: the run reads as Error until CompleteAsync writes the Ok, so a run that dies half way
        // (process killed, database down while closing it) is never counted as a success.
        var userMessage = new DbMessage { SessionId = session.Id, Role = MessageRole.User, Content = text, CreatedAt = now };
        var agentRun = new AgentRun
        {
            SessionId = session.Id,
            UserMessageId = userMessage.Id,
            Model = "",
            Status = AgentRunStatus.Error,
            StartedAt = now,
        };
        db.AddRange(userMessage, agentRun);
        await db.SaveChangesAsync(ct);

        var history = await LoadHistoryAsync(session.Id, ct);

        // Exactly one system message, first (ADR-014): the language instruction is its final paragraph.
        List<AiMessage> messages =
        [
            new(ChatRole.System, SystemPrompt(business, session.Status, language, now)),
            .. history.SelectMany(ToModelMessages),
        ];

        var tools = new ToolContext(business.Id, session.Id, business.Timezone);
        var pricing = await db.ModelPricings.AsNoTracking().ToDictionaryAsync(p => p.Model, ct);
        var recorder = new TraceRecorder(agentRun.Id, pricing, clock);
        return Prepared.Accept(new RunState(agentRun.Id, session.Id, messages, tools, recorder, clock.GetTimestamp(), text.Length));
    }

    /// <summary>
    /// The latest <see cref="AgentOptions.MaxHistoryMessages"/> user/assistant messages (the one just saved included)
    /// plus the tool exchanges stored since the oldest of them, oldest first. Counting only the text messages keeps a
    /// tool-heavy turn from pushing real conversation out of the window.
    /// </summary>
    private async Task<List<DbMessage>> LoadHistoryAsync(Guid sessionId, CancellationToken ct)
    {
        var oldestWanted = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.Role != MessageRole.Tool)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(Math.Max(1, options.Value.MaxHistoryMessages))
            .MinAsync(m => m.CreatedAt, ct);

        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.CreatedAt >= oldestWanted)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The business prompt followed by the language instruction (SPEC 7.3), in ONE system message. Gemini's
    /// OpenAI-compatible endpoint silently drops system messages when a request has more than one, so the instruction
    /// cannot be a second, trailing system message (ADR-014).
    /// </summary>
    private static string SystemPrompt(Business business, SessionStatus status, Language language, DateTimeOffset now) =>
        $"{PromptBuilder.BuildSystemPrompt(business, status, now)}\n\n{LanguageLabel}{PromptBuilder.LanguageInstruction(language)}";

    private static IEnumerable<AiMessage> ToModelMessages(DbMessage message) => message.Role switch
    {
        MessageRole.User => [new AiMessage(ChatRole.User, message.Content)],
        MessageRole.Assistant => [new AiMessage(ChatRole.Assistant, message.Content)],
        MessageRole.Tool => ToolExchange.TryParse(message.Content)?.ToMessages() ?? [],
        _ => [],
    };

    /// <summary>Streams the model answer (running tools on the way) and fills <paramref name="run"/>. Throws on any failure.</summary>
    private async IAsyncEnumerable<AgentEvent> StreamModelAsync(RunState run, [EnumeratorCancellation] CancellationToken ct)
    {
        var chatOptions = new ChatOptions
        {
            Tools = AgentToolFactory.Create(db, run.Tools, clock),
            MaxOutputTokens = options.Value.MaxOutputTokens,
        };

        await foreach (var update in BuildPipeline(run).GetStreamingResponseAsync(run.Messages, chatOptions, ct))
        {
            // Steps that completed before this update was produced (a model call that ended, a tool that ran) go
            // out first, so a ToolCall is always seen before the tokens of the answer that follows it.
            foreach (var traced in run.Recorder.TakeNew())
                yield return Trace.From(traced);

            run.Observe(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        run.PendingCalls[call.CallId] = call;
                        break;
                    case FunctionResultContent result when run.PendingCalls.Remove(result.CallId, out var call):
                        run.Exchanges.Add(ToolExchange.Create(call, result));
                        break;
                }
            }

            var text = update.Text;
            if (text.Length > 0)
            {
                run.Answer.Append(text);
                yield return new Token(text);
            }
        }

        // The last model call completes when its stream ends.
        foreach (var traced in run.Recorder.TakeNew())
            yield return Trace.From(traced);

        if (run.PendingCalls.Count > 0)
            throw new AgentRunException("iteration_limit", "I couldn't finish that request. Please try rephrasing it or splitting it in smaller steps.", retryable: false);
        if (run.Answer.Length == 0)
            throw new AgentRunException("empty_response", "The assistant did not produce an answer. Please try again.", retryable: true);
    }

    private async Task<RunCompleted> CompleteAsync(RunState run)
    {
        // From here on persistence must not depend on the request token: the answer was already streamed.
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();

        AddToolMessages(run, now, out var next);
        db.ChatMessages.Add(new DbMessage
        {
            SessionId = run.SessionId,
            Role = MessageRole.Assistant,
            Content = run.Answer.ToString(),
            CreatedAt = next,
        });

        db.TraceEvents.AddRange(run.Recorder.Events);
        var agentRun = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
        var summary = Close(agentRun, AgentRunStatus.Ok, run);
        await db.ChatSessions.Where(s => s.Id == run.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActivityAt, now));
        await db.SaveChangesAsync();
        run.Closed = true;

        return new RunCompleted(run.Id, run.Recorder.TokensIn, run.Recorder.TokensOut, run.Recorder.CostUsd, summary.DurationMs, summary.Model, summary.Provider);
    }

    /// <summary>Closes the run as <c>Error</c> and stores its trace, which ends with the event of the failure (an <c>Error</c>, or a <c>Guardrail</c> for a limit).</summary>
    private async Task FailAsync(RunState run)
    {
        // A failed tool call may have left half-saved entities in the shared context.
        db.ChangeTracker.Clear();

        // Tools that already ran keep their effects (a booking, a handoff): the next turn must know about them.
        AddToolMessages(run, clock.GetUtcNow(), out _);

        if (!run.FailureRecorded)
            RecordFailure(run, new FailureTrace(TraceEventType.Error, "agent_cancelled", new { reason = "client_disconnected" }));

        var agentRun = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
        Close(agentRun, AgentRunStatus.Error, run);
        db.TraceEvents.AddRange(run.Recorder.Events);
        await db.SaveChangesAsync();
        run.Closed = true;
    }

    /// <summary>Adds the closing event of a failed run to the trace, in sequence after every step that completed.</summary>
    private void RecordFailure(RunState run, FailureTrace failure)
    {
        run.FailureRecorded = true;
        run.Recorder.Record(failure.Type, failure.Name, new { textLength = run.InputLength }, failure.Output,
            durationMs: run.Recorder.Elapsed(run.StartTimestamp));
    }

    /// <summary>Closes the run as failed, or as cancelled if no failure was recorded. Never throws: nobody could handle it.</summary>
    private async Task TryFailAsync(RunState run)
    {
        try
        {
            await FailAsync(run);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent run {RunId} could not be closed as failed", run.Id);
        }
    }

    /// <summary>
    /// Stores the tool exchanges of the run, oldest first, one microsecond apart (the precision of timestamptz), so
    /// they sort after the user message and before whatever is stored next. <paramref name="next"/> is the first free instant.
    /// </summary>
    private void AddToolMessages(RunState run, DateTimeOffset now, out DateTimeOffset next)
    {
        const long TicksPerMicrosecond = 10;
        next = now;
        foreach (var exchange in run.Exchanges)
        {
            db.ChatMessages.Add(new DbMessage
            {
                SessionId = run.SessionId,
                Role = MessageRole.Tool,
                Content = exchange.Serialize(),
                CreatedAt = next,
            });
            next = next.AddTicks(TicksPerMicrosecond);
        }
    }

    private (string Model, string Provider, int DurationMs) Close(AgentRun agentRun, AgentRunStatus status, RunState run)
    {
        var model = run.ModelLabel;
        var duration = (int)clock.GetElapsedTime(run.StartTimestamp).TotalMilliseconds;

        agentRun.Status = status;
        agentRun.Model = model.Length <= ModelColumnLength ? model : model[..ModelColumnLength];
        agentRun.TokensIn = (int)Math.Min(run.Recorder.TokensIn, int.MaxValue);
        agentRun.TokensOut = (int)Math.Min(run.Recorder.TokensOut, int.MaxValue);
        agentRun.CostUsd = run.Recorder.CostUsd;
        agentRun.DurationMs = duration;
        return (model, run.ProviderLabel, duration);
    }

    /// <summary>The text for the user and whether retrying can help. Never includes exception details.</summary>
    private static (string Message, bool Retryable) Describe(Exception ex) => ex switch
    {
        AgentRunException e => (e.Message, e.Retryable),
        LlmNotConfiguredException => ("The AI provider is not configured on this server.", false),
        _ when FallbackChatClient.IsRetryable(ex) =>
            ("The assistant is at capacity right now (demo token quota). Please try again in a few minutes.", true),
        _ => ("Something went wrong while reaching the assistant. Check your connection and try again.", true),
    };

    /// <summary>
    /// What the trace stores about a failure: the kind and the HTTP status, not the message (it may echo request
    /// data). Hitting the iteration limit is a rejected behaviour, not a provider error, so it is a Guardrail.
    /// </summary>
    private FailureTrace FailureTraceFor(Exception ex) => ex switch
    {
        AgentRunException { Code: "iteration_limit" } e =>
            new(TraceEventType.Guardrail, e.Code, new { code = e.Code, maxIterations = options.Value.MaxIterations }),
        AgentRunException e => new(TraceEventType.Error, "agent_error", new { code = e.Code }),
        _ => new(TraceEventType.Error, "agent_error", TraceRecorder.DescribeFailure(ex)),
    };

    private static Language? ParseLanguage(string? code) => code switch
    {
        "es" => Language.Spanish,
        "en" => Language.English,
        _ => null,
    };

    private static string LanguageCode(Language language) => language == Language.Spanish ? "es" : "en";

    private sealed record Prepared(Rejected? Rejection, RunState? Run)
    {
        public static Prepared Reject(RejectionReason reason, string message) => new(new Rejected(reason, message), null);
        public static Prepared Accept(RunState run) => new(null, run);
    }

    private sealed record FailureTrace(TraceEventType Type, string Name, object Output);

    /// <summary>Everything one run accumulates: context sent to the model, usage, cost, answer text and bookkeeping.</summary>
    private sealed class RunState(
        Guid id, Guid sessionId, List<AiMessage> messages, ToolContext tools,
        TraceRecorder recorder, long startTimestamp, int inputLength)
    {
        public Guid Id { get; } = id;
        public Guid SessionId { get; } = sessionId;
        public List<AiMessage> Messages { get; } = messages;
        public ToolContext Tools { get; } = tools;
        public long StartTimestamp { get; } = startTimestamp;
        public int InputLength { get; } = inputLength;

        public StringBuilder Answer { get; } = new();
        public TraceRecorder Recorder { get; } = recorder;

        /// <summary>Function calls seen without a result yet. Not empty at the end means the iteration limit cut the loop.</summary>
        public Dictionary<string, FunctionCallContent> PendingCalls { get; } = [];

        /// <summary>Tool calls that ran, with their results, in order.</summary>
        public List<ToolExchange> Exchanges { get; } = [];

        /// <summary>True once the closing event of a failed run is in the trace, so it is recorded once.</summary>
        public bool FailureRecorded { get; set; }

        /// <summary>True once the run row was written as Ok or Error. Before that the row says Error.</summary>
        public bool Closed { get; set; }

        // FallbackChatClient falls back per model call, so one run can be answered by several models.
        private readonly List<string> _models = [];
        private readonly List<string> _providers = [];

        /// <summary>Models that answered, in order of first use, comma separated. A single name unless a fallback kicked in.</summary>
        public string ModelLabel => _models.Count == 0 ? UnknownModel : string.Join(", ", _models);

        public string ProviderLabel => _providers.Count == 0 ? UnknownModel : string.Join(", ", _providers);

        /// <summary>Remembers which provider and model produced <paramref name="update"/> (tagged by <see cref="FallbackChatClient"/>).</summary>
        public void Observe(ChatResponseUpdate update)
        {
            if (update.AdditionalProperties?.GetValueOrDefault(FallbackChatClient.ProviderKey) is string provider && !_providers.Contains(provider))
                _providers.Add(provider);

            var model = update.AdditionalProperties?.GetValueOrDefault(FallbackChatClient.ModelKey) as string ?? update.ModelId;
            if (model is not null)
            {
                if (!_models.Contains(model))
                    _models.Add(model);
            }
        }
    }

    /// <summary>A failure whose message is already written for the user.</summary>
    private sealed class AgentRunException(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }
}
