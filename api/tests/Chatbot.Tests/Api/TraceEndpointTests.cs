using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Chatbot.Tests.Tracing;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Api;

/// <summary>The trace and messages endpoints, and the order of the SSE events of a run that uses tools.</summary>
[Collection(PostgresCollection.Name)]
public class TraceEndpointTests(PostgresFixture postgres)
{
    private const string Phone = "+54 11 5555 4444";

    private static ScriptedChatClient SearchThenAnswer() => new(
        Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(400, 20),
        Turn.Reply("A cleaning ", "costs USD 60.").WithUsage(500, 30));

    [Fact]
    public async Task The_sse_stream_interleaves_trace_events_with_tokens_and_ends_with_the_final_call()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, SearchThenAnswer());
        using var host = factory;
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        var response = await factory.CreateClient().PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "How much is a cleaning?" });

        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["run_started", "trace", "trace", "token", "token", "trace", "run_completed"], events.Select(e => e.Name));

        var traces = events.Where(e => e.Name == "trace").Select(e => e.Data).ToList();
        Assert.Equal([0, 1, 2], traces.Select(t => t.GetProperty("sequence").GetInt32()));
        Assert.Equal(["LlmCall", "ToolCall", "LlmCall"], traces.Select(t => t.GetProperty("type").GetString()));
        Assert.Equal(["search_catalog"], traces.Where(t => t.GetProperty("type").GetString() == "ToolCall").Select(t => t.GetProperty("name").GetString()));

        // Contract fields, camelCase, nulls kept.
        var llm = traces[0];
        var runId = events[0].Data.GetProperty("runId").GetGuid();
        Assert.Equal(runId, llm.GetProperty("runId").GetGuid());
        Assert.Equal(ScriptedChatClient.Model, llm.GetProperty("name").GetString());
        Assert.Equal((400, 20), (llm.GetProperty("tokensIn").GetInt32(), llm.GetProperty("tokensOut").GetInt32()));
        Assert.Equal(0.00017m, llm.GetProperty("costUsd").GetDecimal());
        Assert.True(llm.GetProperty("durationMs").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, traces[1].GetProperty("tokensIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, traces[1].GetProperty("costUsd").ValueKind);
        Assert.Equal("cleaning", traces[1].GetProperty("input").GetProperty("query").GetString());

        var completed = events[^1].Data;
        Assert.Equal(traces.Sum(t => t.GetProperty("costUsd").ValueKind == JsonValueKind.Null ? 0m : t.GetProperty("costUsd").GetDecimal()), completed.GetProperty("costUsd").GetDecimal());
    }

    [Fact]
    public async Task The_trace_endpoint_returns_the_runs_with_the_same_events_that_were_streamed()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, SearchThenAnswer());
        using var host = factory;
        var client = factory.CreateClient();
        var sessionId = await world.NewSessionAsync(Niche.Clinic);
        var sse = SseEvent.Parse(await (await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = $"Price of a cleaning? My number is {Phone}" }))
            .Content.ReadAsStringAsync());

        var trace = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/trace");

        Assert.Equal(sessionId, trace.GetProperty("sessionId").GetGuid());
        var run = Assert.Single(trace.GetProperty("runs").EnumerateArray());
        Assert.Equal(sse[0].Data.GetProperty("runId").GetGuid(), run.GetProperty("id").GetGuid());
        Assert.Equal("Ok", run.GetProperty("status").GetString());
        Assert.Equal(ScriptedChatClient.Model, run.GetProperty("model").GetString());
        Assert.Equal(ScriptedChatClient.Provider, run.GetProperty("provider").GetString());
        Assert.Equal((900, 50), (run.GetProperty("tokensIn").GetInt32(), run.GetProperty("tokensOut").GetInt32()));
        Assert.True(run.GetProperty("durationMs").GetInt32() > 0);
        Assert.Equal(sse[^1].Data.GetProperty("costUsd").GetDecimal(), run.GetProperty("costUsd").GetDecimal());
        Assert.True(run.TryGetProperty("startedAt", out _));
        // The public trace shows the user message with the phone masked.
        Assert.Equal("Price of a cleaning? My number is +54 11 **** 4444", run.GetProperty("userMessage").GetString());

        var stored = run.GetProperty("events").EnumerateArray().ToList();
        var streamed = sse.Where(e => e.Name == "trace").Select(e => e.Data).ToList();
        Assert.Equal(streamed.Count, stored.Count);
        foreach (var (s, t) in streamed.Zip(stored))
        {
            Assert.True(JsonEquality.Equal(s, t), $"streamed {s} differs from stored {t}");
        }
    }

    [Fact]
    public async Task The_trace_lists_every_run_of_the_session_in_order_and_unknown_sessions_are_404()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, new ScriptedChatClient(Turn.Reply("one"), Turn.Reply("two")));
        using var host = factory;
        var client = factory.CreateClient();
        var sessionId = await world.NewSessionAsync();
        await (await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "first question" })).Content.ReadAsStringAsync();
        await (await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "second question" })).Content.ReadAsStringAsync();

        var trace = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/trace");
        var missing = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/trace");

        Assert.Equal(["first question", "second question"], trace.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("userMessage").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_session_without_runs_has_an_empty_trace()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, new ScriptedChatClient());
        using var host = factory;
        var sessionId = await world.NewSessionAsync();

        var trace = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/trace");

        Assert.Equal(0, trace.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task Messages_exclude_tool_rows_and_report_status_and_language()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, new ScriptedChatClient(
            Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "wants a person", ["summary"] = "Asked for staff." }),
            Turn.Reply("A person will continue.")));
        using var host = factory;
        var client = factory.CreateClient();
        var sessionId = await world.NewSessionAsync();
        await (await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "I want to talk to a person, please" })).Content.ReadAsStringAsync();

        // The tool exchange is stored, but it is model context only.
        await using (var db = world.NewDb())
            Assert.Equal(1, await db.ChatMessages.CountAsync(m => m.SessionId == sessionId && m.Role == MessageRole.Tool));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/messages");

        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal([("user", "I want to talk to a person, please"), ("assistant", "A person will continue.")],
            messages.Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("text").GetString()!)));
        Assert.All(messages, m =>
        {
            Assert.NotEqual(Guid.Empty, m.GetProperty("id").GetGuid());
            Assert.True(m.TryGetProperty("createdAt", out _));
        });
        Assert.Equal("AwaitingHuman", body.GetProperty("sessionStatus").GetString());
        Assert.Equal("en", body.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Messages_of_a_new_session_are_empty_with_a_null_language_and_unknown_sessions_are_404()
    {
        var (factory, world) = await ApiHost.StartAsync(postgres, new ScriptedChatClient());
        using var host = factory;
        var client = factory.CreateClient();
        var sessionId = await world.NewSessionAsync();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/messages");
        var missing = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/messages");

        Assert.Equal(0, body.GetProperty("messages").GetArrayLength());
        Assert.Equal("Active", body.GetProperty("sessionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("language").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
