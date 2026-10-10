using System.Runtime.CompilerServices;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Infrastructure.Llm;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Chatbot.Tests.Data;
using Chatbot.Tests.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Chatbot.Tests.Agent;

/// <summary>
/// One scripted model answer: what the fake streams for one call. Every content becomes its own update, so
/// <see cref="Reply"/> with three pieces produces three <c>Token</c> events.
/// </summary>
internal sealed record Turn(
    IReadOnlyList<AIContent> Contents, Exception? Failure = null, long TokensIn = 0, long TokensOut = 0, string? Model = null, string? Provider = null)
{
    public static Turn Reply(params string[] pieces) => new([.. pieces.Select(p => new TextContent(p))]);

    public static Turn Call(string tool, Dictionary<string, object?>? arguments = null) =>
        new([new FunctionCallContent($"call-{Guid.NewGuid():N}", tool, arguments ?? [])]);

    public static Turn Fail(Exception failure) => new([], failure);

    public Turn WithUsage(long tokensIn, long tokensOut) => this with { TokensIn = tokensIn, TokensOut = tokensOut };

    /// <summary>Makes a different model and provider answer this turn, like <c>FallbackChatClient</c> does after a failure.</summary>
    public Turn AnsweredBy(string model, string provider) => this with { Model = model, Provider = provider };
}

/// <summary>
/// Stands in for the raw "llm" client. It answers each call with the scripted <see cref="Turn"/>, tags updates
/// the way <c>FallbackChatClient</c> does, and records what it received.
/// </summary>
internal sealed class ScriptedChatClient(Func<int, IReadOnlyList<AiMessage>, Turn> script) : IChatClient
{
    /// <summary>Given the zero-based call number and the messages of the call, returns what the model answers.</summary>
    public Func<int, IReadOnlyList<AiMessage>, Turn> Script { get; set; } = script;

    /// <summary>Same model as the seeded <c>ModelPricing</c> row, so costs are not zero.</summary>
    public const string Model = "gemini-3.5-flash-lite";

    public const string Provider = "gemini";

    public ScriptedChatClient(params Turn[] turns)
        : this((call, _) => call < turns.Length
            ? turns[call]
            : throw new InvalidOperationException($"The script has no turn for call {call}."))
    {
    }

    /// <summary>Snapshot of the messages of every call, in order.</summary>
    public List<IReadOnlyList<AiMessage>> Calls { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    /// <summary>When set, the stream waits for the caller to cancel after the first update (a client that disconnects).</summary>
    public bool HangAfterFirstUpdate { get; init; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        Calls.Add(snapshot);
        Options.Add(options);
        var turn = Script(Calls.Count - 1, snapshot);

        await Task.Delay(15, cancellationToken); // makes DurationMs > 0 without slowing the suite
        if (turn.Failure is { } failure)
            throw failure;

        foreach (var content in turn.Contents)
        {
            yield return Tagged(content, turn);
            if (HangAfterFirstUpdate)
                await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (turn.TokensIn > 0 || turn.TokensOut > 0)
        {
            yield return Tagged(new UsageContent(new UsageDetails { InputTokenCount = turn.TokensIn, OutputTokenCount = turn.TokensOut }), turn);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<AiMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The agent always streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatResponseUpdate Tagged(AIContent content, Turn turn) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [content],
        AdditionalProperties = new()
        {
            [FallbackChatClient.ProviderKey] = turn.Provider ?? Provider,
            [FallbackChatClient.ModelKey] = turn.Model ?? Model,
        },
    };
}

/// <summary>Moves one second forward on every read, so messages saved in the same test have distinct, ordered timestamps.</summary>
internal sealed class TickingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
}

/// <summary>A migrated and seeded database plus the scripted model, with helpers to run the agent the way a request would.</summary>
internal sealed class AgentWorld
{
    private AgentWorld(string connectionString, ScriptedChatClient llm, AgentOptions options)
    {
        ConnectionString = connectionString;
        Llm = llm;
        Options = options;
    }

    public string ConnectionString { get; }
    public ScriptedChatClient Llm { get; }
    public AgentOptions Options { get; }
    public TimeProvider Clock { get; } = new TickingClock(DateTimeOffset.UtcNow);

    /// <summary>The deterministic embeddings behind every knowledge search of this world.</summary>
    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public KnowledgeOptions KnowledgeSettings { get; set; } = TestKnowledge.SearchSettings();

    public Embedder Embedder => field ??= TestKnowledge.NewEmbedder(Embeddings);

    public KnowledgeSearch NewKnowledge(ChatbotDbContext db) => TestKnowledge.NewSearch(db, Embedder, KnowledgeSettings);

    public static async Task<AgentWorld> CreateAsync(PostgresFixture postgres, ScriptedChatClient llm, AgentOptions? options = null)
    {
        var world = new AgentWorld(await postgres.CreateMigratedDatabaseAsync(), llm, options ?? new AgentOptions());
        await using var db = world.NewDb();
        await new DatabaseSeeder(db, TimeProvider.System, NullLogger<DatabaseSeeder>.Instance).SeedAsync(CancellationToken.None);
        return world;
    }

    public ChatbotDbContext NewDb() => PostgresFixture.CreateContext(ConnectionString);

    public AgentService NewService(ChatbotDbContext db) =>
        new(db, Llm, Microsoft.Extensions.Options.Options.Create(Options), Clock, NewKnowledge(db), NullLogger<AgentService>.Instance);

    public async Task<Guid> NewSessionAsync(Niche niche = Niche.Clinic, SessionStatus status = SessionStatus.Active, string? language = null)
    {
        await using var db = NewDb();
        var business = await db.Businesses.SingleAsync(b => b.Niche == niche);
        var session = new ChatSession { BusinessId = business.Id, ClientIpHash = "test", Status = status, Language = language };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>Runs one user message with its own DbContext (like a request) and collects every event.</summary>
    public async Task<List<AgentEvent>> RunAsync(Guid sessionId, string? text, CancellationToken ct = default)
    {
        await using var db = NewDb();
        var events = new List<AgentEvent>();
        await foreach (var e in NewService(db).RunAsync(sessionId, text, ct))
            events.Add(e);
        return events;
    }

    public async Task<AgentRun> SingleRunAsync(Guid sessionId)
    {
        await using var db = NewDb();
        return await db.AgentRuns.AsNoTracking().SingleAsync(r => r.SessionId == sessionId);
    }

    public async Task<List<TraceEvent>> TraceAsync(Guid runId)
    {
        await using var db = NewDb();
        return await db.TraceEvents.AsNoTracking().Where(e => e.RunId == runId).OrderBy(e => e.Sequence).ToListAsync();
    }

    public async Task<List<Chatbot.Core.Domain.ChatMessage>> MessagesAsync(Guid sessionId)
    {
        await using var db = NewDb();
        return await db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt).ToListAsync();
    }
}
