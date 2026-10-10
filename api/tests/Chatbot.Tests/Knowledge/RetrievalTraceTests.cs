using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Infrastructure.Agent.Tracing;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Api;
using Chatbot.Tests.Data;
using Chatbot.Tests.Tracing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Chatbot.Tests.Knowledge;

/// <summary>
/// <c>search_knowledge_base</c> inside the agent loop: one <c>Retrieval</c> event (not a <c>ToolCall</c>) with the contract the panel
/// and the evals read, in the run, over SSE and in <c>GET /trace</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RetrievalTraceTests(PostgresFixture postgres)
{
    private const string Question = "Can appointments be cancelled 24 hours before without any fee?";

    private static readonly IReadOnlyDictionary<string, string> HostSettings = new Dictionary<string, string>
    {
        ["Embeddings:Model"] = TestKnowledge.Model,
        ["Knowledge:MinSemanticSimilarity"] = "0.3",
        ["Knowledge:MinLexicalRank"] = "0.05",
    };

    private static ScriptedChatClient SearchThenAnswer(string query = Question) => new(
        Turn.Call(AgentToolFactory.SearchKnowledgeBase, new() { ["query"] = query }).WithUsage(400, 20),
        Turn.Reply("According to our cancellation policy ", "you can.").WithUsage(500, 30));

    /// <summary>Indexes the clinic documents and a price of 100 USD per million embedding tokens (a round number to assert on).</summary>
    private static async Task IndexAsync(AgentWorld world, FakeKnowledgeSource? source = null)
    {
        await using var db = world.NewDb();
        db.ModelPricings.Add(new ModelPricing { Model = TestKnowledge.Model, InputPerMillionUsd = 100m });
        await db.SaveChangesAsync();
        await (source ?? new FakeKnowledgeSource()
            .Put(Niche.Clinic, "cancellations", "Cancellation policy", "Appointments can be cancelled up to 24 hours before without any fee. After that a fee of USD 25 applies.")
            .Put(Niche.Clinic, "insurance", "Insurance plans", "We accept OSDE 210, Swiss Medical and Galeno.")).IngestAsync(db, world.Embedder);
    }

    [Fact]
    public async Task A_search_is_recorded_as_one_retrieval_event_and_not_as_a_tool_call()
    {
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await IndexAsync(world);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        var events = await world.RunAsync(sessionId, Question);

        Assert.IsType<RunCompleted>(events[^1]);
        var run = await world.SingleRunAsync(sessionId);
        var stored = await world.TraceAsync(run.Id);
        Assert.Equal(
            [(TraceEventType.LlmCall, ScriptedChatClient.Model), (TraceEventType.Retrieval, "search_knowledge_base"), (TraceEventType.LlmCall, ScriptedChatClient.Model)],
            stored.Select(e => (e.Type, e.Name)));
        Assert.DoesNotContain(stored, e => e.Type == TraceEventType.ToolCall);
        Assert.Equal([0, 1, 2], stored.Select(e => e.Sequence));
    }

    [Fact]
    public async Task The_retrieval_event_follows_the_contract()
    {
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await IndexAsync(world);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);
        await world.RunAsync(sessionId, Question);
        var run = await world.SingleRunAsync(sessionId);

        var retrieval = (await world.TraceAsync(run.Id)).Single(e => e.Type == TraceEventType.Retrieval);

        Assert.Equal(Question, retrieval.Input!.Value.GetProperty("query").GetString());
        var output = retrieval.Output!.Value;
        Assert.True(output.GetProperty("thresholdPassed").GetBoolean());
        Assert.Equal(0.3, output.GetProperty("threshold").GetDouble());
        var passages = output.GetProperty("passages").EnumerateArray().ToList();
        Assert.NotEmpty(passages);
        var first = passages[0];
        Assert.Equal("cancellations#0", first.GetProperty("sourceId").GetString());
        Assert.Equal("Cancellation policy", first.GetProperty("title").GetString());
        Assert.Equal("Cancellation policy", first.GetProperty("heading").GetString());
        Assert.Contains("24 hours", first.GetProperty("text").GetString());
        Assert.InRange(first.GetProperty("semanticScore").GetDouble(), 0.3, 1);
        Assert.True(first.GetProperty("lexicalScore").GetDouble() > 0);
        Assert.True(first.GetProperty("finalScore").GetDouble() > 0);
        Assert.True(first.GetProperty("aboveThreshold").GetBoolean());
        Assert.All(passages, p => Assert.Equal(
            ["aboveThreshold", "finalScore", "heading", "lexicalScore", "semanticScore", "sourceId", "text", "title"],
            p.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal))); // jsonb does not keep the key order

        var embeddingTokens = Question.Split(' ').Length; // the fake counts one token per word
        Assert.Equal(embeddingTokens, retrieval.TokensIn);
        Assert.Null(retrieval.TokensOut);
        Assert.Equal(embeddingTokens * 100m / 1_000_000m, retrieval.CostUsd);
        Assert.True(retrieval.DurationMs > 0);
    }

    [Fact]
    public async Task The_cost_of_a_retrieval_is_zero_when_the_embedding_model_has_no_price_and_it_counts_in_the_run_cost_when_it_has()
    {
        var priced = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await IndexAsync(priced);
        var pricedSession = await priced.NewSessionAsync(Niche.Clinic);
        await priced.RunAsync(pricedSession, Question);
        var pricedRun = await priced.SingleRunAsync(pricedSession);
        var pricedEvents = await priced.TraceAsync(pricedRun.Id);

        var unpriced = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await using (var db = unpriced.NewDb())
            await new FakeKnowledgeSource().Put(Niche.Clinic, "cancellations", "Cancellation policy", "Cancel up to 24 hours before.").IngestAsync(db, unpriced.Embedder);
        var unpricedSession = await unpriced.NewSessionAsync(Niche.Clinic);
        await unpriced.RunAsync(unpricedSession, Question);
        var unpricedRun = await unpriced.SingleRunAsync(unpricedSession);
        var unpricedEvents = await unpriced.TraceAsync(unpricedRun.Id);

        Assert.Equal(0m, unpricedEvents.Single(e => e.Type == TraceEventType.Retrieval).CostUsd);
        Assert.Equal(unpricedEvents.Sum(e => e.CostUsd ?? 0), unpricedRun.CostUsd);
        Assert.True(pricedEvents.Single(e => e.Type == TraceEventType.Retrieval).CostUsd > 0);
        Assert.Equal(pricedEvents.Sum(e => e.CostUsd ?? 0), pricedRun.CostUsd); // the run cost is the sum of every event cost
        Assert.Equal(400 + 500, pricedRun.TokensIn); // model tokens only: embedding tokens are not mixed in
    }

    [Fact]
    public async Task Long_passages_are_clipped_so_the_event_stays_a_json_object_within_the_payload_limit()
    {
        var source = new FakeKnowledgeSource();
        foreach (var i in Enumerable.Range(0, 6))
            source.Put(Niche.Clinic, $"long{i}", $"Long document {i}", "## Section\n\n" + string.Join(' ', Enumerable.Repeat("cancel appointment fee notice policy", 60)));
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await IndexAsync(world, source);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);
        await world.RunAsync(sessionId, Question);
        var run = await world.SingleRunAsync(sessionId);

        var retrieval = (await world.TraceAsync(run.Id)).Single(e => e.Type == TraceEventType.Retrieval);

        var output = retrieval.Output!.Value;
        Assert.Equal(JsonValueKind.Object, output.ValueKind); // a payload over 4 KB would have been replaced by a truncated string
        var passages = output.GetProperty("passages").EnumerateArray().ToList();
        Assert.Equal(4, passages.Count);
        Assert.All(passages, p => Assert.EndsWith(TracePayload.TruncatedMarker, p.GetProperty("text").GetString()));
        Assert.True(output.GetRawText().Length < TracePayload.MaxChars);
    }

    [Fact]
    public async Task The_tool_result_reaches_the_model_and_the_exchange_is_stored_for_the_next_turn()
    {
        var llm = SearchThenAnswer();
        var world = await AgentWorld.CreateAsync(postgres, llm);
        await IndexAsync(world);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        await world.RunAsync(sessionId, Question);

        var result = llm.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        var tool = Assert.IsType<JsonElement>(result.Result);
        Assert.True(tool.GetProperty("found").GetBoolean());
        Assert.StartsWith("<passage sourceId=\"cancellations#0\">", tool.GetProperty("passages")[0].GetProperty("text").GetString());
        var stored = (await world.MessagesAsync(sessionId)).Where(m => m.Role == MessageRole.Tool).ToList();
        var row = Assert.Single(stored);
        Assert.Contains("search_knowledge_base", row.Content);
    }

    [Fact]
    public async Task A_provider_outage_is_a_tool_call_with_the_error_and_the_run_still_completes()
    {
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await IndexAsync(world);
        world.Embeddings.Failure = _ => new HttpRequestException("down", null, System.Net.HttpStatusCode.BadRequest);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        var events = await world.RunAsync(sessionId, Question);

        Assert.IsType<RunCompleted>(events[^1]);
        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Ok, run.Status);
        var tool = (await world.TraceAsync(run.Id)).Single(e => e.Name == "search_knowledge_base");
        Assert.Equal(TraceEventType.ToolCall, tool.Type);
        Assert.Equal("knowledge_unavailable", tool.Output!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_empty_query_is_a_tool_call_and_a_guardrail_like_every_other_invalid_argument()
    {
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer(query: "   "));
        await IndexAsync(world);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        await world.RunAsync(sessionId, Question);

        var run = await world.SingleRunAsync(sessionId);
        var stored = await world.TraceAsync(run.Id);
        Assert.Equal(
            [TraceEventType.LlmCall, TraceEventType.ToolCall, TraceEventType.Guardrail, TraceEventType.LlmCall],
            stored.Select(e => e.Type));
        Assert.Equal("invalid_query", stored[2].Name);
    }

    [Fact]
    public async Task The_sse_stream_and_the_trace_endpoint_carry_the_retrieval_event()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, SearchThenAnswer(), HostSettings);
        using var host = factory;
        await IndexAsync(world);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = Question });

        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["run_started", "trace", "trace", "token", "token", "trace", "run_completed"], events.Select(e => e.Name));
        var traces = events.Where(e => e.Name == "trace").Select(e => e.Data).ToList();
        Assert.Equal(["LlmCall", "Retrieval", "LlmCall"], traces.Select(t => t.GetProperty("type").GetString()));

        var streamed = traces[1];
        Assert.Equal("search_knowledge_base", streamed.GetProperty("name").GetString());
        Assert.Equal(Question, streamed.GetProperty("input").GetProperty("query").GetString());
        Assert.True(streamed.GetProperty("output").GetProperty("thresholdPassed").GetBoolean());
        Assert.Equal("cancellations#0", streamed.GetProperty("output").GetProperty("passages")[0].GetProperty("sourceId").GetString());
        Assert.True(streamed.GetProperty("tokensIn").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, streamed.GetProperty("tokensOut").ValueKind);
        Assert.True(streamed.GetProperty("costUsd").GetDecimal() > 0);

        // The total of the run includes the embedding cost, like the sum of the events.
        Assert.Equal(
            traces.Sum(t => t.GetProperty("costUsd").ValueKind == JsonValueKind.Null ? 0m : t.GetProperty("costUsd").GetDecimal()),
            events[^1].Data.GetProperty("costUsd").GetDecimal());

        var trace = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/trace");
        var stored = Assert.Single(trace.GetProperty("runs").EnumerateArray()).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["LlmCall", "Retrieval", "LlmCall"], stored.Select(e => e.GetProperty("type").GetString()));
        Assert.True(JsonEquality.Equal(streamed, stored[1]), $"streamed {streamed} differs from stored {stored[1]}");
    }

    [Fact]
    public async Task The_database_accepts_the_retrieval_type_and_still_refuses_unknown_ones()
    {
        var world = await AgentWorld.CreateAsync(postgres, SearchThenAnswer());
        await using var db = world.NewDb();
        var session = await world.NewSessionAsync(Niche.Clinic);
        var message = new Chatbot.Core.Domain.ChatMessage { SessionId = session, Role = MessageRole.User, Content = "x" };
        var run = new AgentRun { SessionId = session, UserMessageId = message.Id, Model = "m" };
        db.AddRange(message, run);
        await db.SaveChangesAsync();

        db.TraceEvents.Add(new TraceEvent { RunId = run.Id, Sequence = 0, Type = TraceEventType.Retrieval, Name = "search_knowledge_base" });
        await db.SaveChangesAsync();

        var bad = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO trace_events (\"Id\", \"RunId\", \"Sequence\", \"Type\", \"Name\", \"DurationMs\", \"CreatedAt\") VALUES (gen_random_uuid(), {0}, 1, 'Made up', 'x', 0, now())",
                run.Id);
        });
        Assert.Equal("ck_trace_events_type_closed_set", bad.ConstraintName);
    }
}
