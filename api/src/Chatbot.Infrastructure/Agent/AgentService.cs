using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
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
    private const int ModelColumnLength = 200; // agent_runs.Model

    /// <summary>Consecutive tool rounds that may fail with an exception before the run is aborted.</summary>
    private const int MaxConsecutiveToolErrors = 3;

    /// <summary>
    /// Processes <paramref name="text"/> in the session. The stream is either a single <see cref="Rejected"/>
    /// (nothing was stored), or <see cref="RunStarted"/>, <see cref="Token"/>s, an optional <see cref="Handoff"/>
    /// and <see cref="RunCompleted"/>, or the same up to <see cref="Error"/>. Provider and tool failures never
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
                run.PendingFailure = FailureTraceFor(failure!);
                await TryFailAsync(run);

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
    private IChatClient BuildPipeline() =>
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
            .UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = options.Value.MaxIterations;
                client.MaximumConsecutiveErrorsPerRequest = MaxConsecutiveToolErrors;
                client.IncludeDetailedErrors = true;
            })
            // M3: add TracingChatClient here. ChatClientBuilder wraps in call order (first Use = outermost), so a
            // `.Use(...)` placed below UseFunctionInvocation sees every individual model call.
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

        // The language instruction goes last so it is the freshest thing the model reads (SPEC 7.3).
        List<AiMessage> messages =
        [
            new(ChatRole.System, PromptBuilder.BuildSystemPrompt(business, session.Status, now)),
            .. history.SelectMany(ToModelMessages),
            new(ChatRole.System, PromptBuilder.LanguageInstruction(language)),
        ];

        var tools = new ToolContext(business.Id, session.Id, business.Timezone);
        var pricing = await db.ModelPricings.AsNoTracking().ToDictionaryAsync(p => p.Model, ct);
        return Prepared.Accept(new RunState(agentRun.Id, session.Id, messages, tools, pricing, clock.GetTimestamp(), text.Length));
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

        await foreach (var update in BuildPipeline().GetStreamingResponseAsync(run.Messages, chatOptions, ct))
        {
            run.Observe(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case UsageContent usage:
                        run.AddUsage(usage.Details);
                        break;
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

        var agentRun = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
        var summary = Close(agentRun, AgentRunStatus.Ok, run);
        await db.ChatSessions.Where(s => s.Id == run.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActivityAt, now));
        await db.SaveChangesAsync();
        run.Closed = true;

        return new RunCompleted(run.Id, run.TokensIn, run.TokensOut, run.CostUsd, summary.DurationMs, summary.Model, summary.Provider);
    }

    /// <summary>Closes the run as <c>Error</c> with one trace event for the failure (an <c>Error</c>, or a <c>Guardrail</c> for a limit).</summary>
    private async Task FailAsync(RunState run, FailureTrace failure)
    {
        // A failed tool call may have left half-saved entities in the shared context.
        db.ChangeTracker.Clear();

        // Tools that already ran keep their effects (a booking, a handoff): the next turn must know about them.
        AddToolMessages(run, clock.GetUtcNow(), out _);

        var agentRun = await db.AgentRuns.FirstAsync(r => r.Id == run.Id);
        var summary = Close(agentRun, AgentRunStatus.Error, run);

        var lastSequence = await db.TraceEvents.Where(e => e.RunId == run.Id).MaxAsync(e => (int?)e.Sequence);
        db.TraceEvents.Add(new TraceEvent
        {
            RunId = run.Id,
            Sequence = (lastSequence ?? -1) + 1,
            Type = failure.Type,
            Name = failure.Name,
            Input = JsonSerializer.SerializeToElement(new { textLength = run.InputLength }),
            Output = JsonSerializer.SerializeToElement(failure.Output),
            DurationMs = summary.DurationMs,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        run.Closed = true;
    }

    /// <summary>Closes the run as failed, or as cancelled if no failure was recorded. Never throws: nobody could handle it.</summary>
    private async Task TryFailAsync(RunState run)
    {
        try
        {
            await FailAsync(run, run.PendingFailure ?? new FailureTrace(TraceEventType.Error, "agent_cancelled", new { reason = "client_disconnected" }));
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
        agentRun.TokensIn = (int)Math.Min(run.TokensIn, int.MaxValue);
        agentRun.TokensOut = (int)Math.Min(run.TokensOut, int.MaxValue);
        agentRun.CostUsd = run.CostUsd;
        agentRun.DurationMs = duration;
        return (model, run.ProviderLabel, duration);
    }

    /// <summary>The text for the user and whether retrying can help. Never includes exception details.</summary>
    private static (string Message, bool Retryable) Describe(Exception ex) => ex switch
    {
        AgentRunException e => (e.Message, e.Retryable),
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
        ClientResultException e => new(TraceEventType.Error, "agent_error", new { error = e.GetType().Name, status = e.Status }),
        HttpRequestException e => new(TraceEventType.Error, "agent_error", new { error = e.GetType().Name, status = (int?)e.StatusCode }),
        _ => new(TraceEventType.Error, "agent_error", new { error = ex.GetType().Name }),
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
        Dictionary<string, ModelPricing> pricing, long startTimestamp, int inputLength)
    {
        public Guid Id { get; } = id;
        public Guid SessionId { get; } = sessionId;
        public List<AiMessage> Messages { get; } = messages;
        public ToolContext Tools { get; } = tools;
        public long StartTimestamp { get; } = startTimestamp;
        public int InputLength { get; } = inputLength;

        public StringBuilder Answer { get; } = new();
        public long TokensIn { get; private set; }
        public long TokensOut { get; private set; }

        /// <summary>Sum of the cost of every model call, each priced with the model that answered it.</summary>
        public decimal CostUsd { get; private set; }

        /// <summary>Function calls seen without a result yet. Not empty at the end means the iteration limit cut the loop.</summary>
        public Dictionary<string, FunctionCallContent> PendingCalls { get; } = [];

        /// <summary>Tool calls that ran, with their results, in order.</summary>
        public List<ToolExchange> Exchanges { get; } = [];

        /// <summary>Set when the run failed but could not be closed yet, so the last attempt (in <c>finally</c>) records the real cause.</summary>
        public FailureTrace? PendingFailure { get; set; }

        /// <summary>True once the run row was written as Ok or Error. Before that the row says Error.</summary>
        public bool Closed { get; set; }

        // FallbackChatClient falls back per model call, so one run can be answered by several models.
        private readonly List<string> _models = [];
        private readonly List<string> _providers = [];
        private string? _currentModel;

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
                _currentModel = model;
                if (!_models.Contains(model))
                    _models.Add(model);
            }
        }

        public void AddUsage(UsageDetails usage)
        {
            long tokensIn = usage.InputTokenCount ?? 0, tokensOut = usage.OutputTokenCount ?? 0;
            TokensIn += tokensIn;
            TokensOut += tokensOut;
            CostUsd += CostCalculator.Compute(_currentModel is null ? null : pricing.GetValueOrDefault(_currentModel), tokensIn, tokensOut);
        }
    }

    /// <summary>A failure whose message is already written for the user.</summary>
    private sealed class AgentRunException(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }
}
