using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Api;

/// <summary>The whole HTTP pipeline against a real database, with the model replaced by a scripted fake.</summary>
[Collection(PostgresCollection.Name)]
public class MessageEndpointTests(PostgresFixture postgres)
{
    private async Task<(WebApplicationFactory<Program> Factory, AgentWorld World)> StartAsync(ScriptedChatClient llm, Action<Chatbot.Infrastructure.Agent.AgentOptions>? configure = null)
    {
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // "Testing": Development would migrate and seed the database at startup.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", world.ConnectionString);
            builder.ConfigureServices(services =>
            {
                services.AddKeyedSingleton<IChatClient>("llm", llm);
                if (configure is not null)
                    services.Configure(configure);
            });
        });
        return (factory, world);
    }

    [Fact]
    public async Task Post_message_streams_the_events_in_order_with_parsable_payloads()
    {
        var llm = new ScriptedChatClient(Turn.Reply("Hello ", "there!").WithUsage(100, 10));
        var (factory, world) = await StartAsync(llm);
        using var _ = factory;
        var client = factory.CreateClient();
        var sessionId = await world.NewSessionAsync(Niche.Restaurant);

        var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "Hi, send me the menu" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));

        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["run_started", "token", "token", "trace", "run_completed"], events.Select(e => e.Name));

        var runId = events[0].Data.GetProperty("runId").GetGuid();
        Assert.Equal(["Hello ", "there!"], events.Where(e => e.Name == "token").Select(e => e.Data.GetProperty("text").GetString()));

        var completed = events[^1].Data;
        Assert.Equal(runId, completed.GetProperty("runId").GetGuid());
        Assert.Equal(100, completed.GetProperty("tokensIn").GetInt64());
        Assert.Equal(10, completed.GetProperty("tokensOut").GetInt64());
        Assert.True(completed.GetProperty("costUsd").GetDecimal() > 0);
        Assert.True(completed.GetProperty("durationMs").GetInt32() > 0);
        Assert.Equal(ScriptedChatClient.Model, completed.GetProperty("model").GetString());
        Assert.Equal(ScriptedChatClient.Provider, completed.GetProperty("provider").GetString());

        Assert.Equal(AgentRunStatus.Ok, (await world.SingleRunAsync(sessionId)).Status);
    }

    [Fact]
    public async Task Handoff_arrives_as_an_event_before_run_completed()
    {
        var llm = new ScriptedChatClient(
            Turn.Call("handoff_to_human", new() { ["reason"] = "wants a person", ["summary"] = "Asked for staff." }),
            Turn.Reply("A person will continue."));
        var (factory, world) = await StartAsync(llm);
        using var _ = factory;
        var sessionId = await world.NewSessionAsync();

        var response = await factory.CreateClient().PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "I want a person" });

        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["run_started", "trace", "trace", "trace", "token", "trace", "handoff", "run_completed"], events.Select(e => e.Name));
        Assert.Equal("wants a person", events.Single(e => e.Name == "handoff").Data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Provider_failure_ends_the_stream_with_an_error_event()
    {
        var llm = new ScriptedChatClient(Turn.Fail(new HttpRequestException("down", null, HttpStatusCode.BadGateway)));
        var (factory, world) = await StartAsync(llm);
        using var _ = factory;
        var sessionId = await world.NewSessionAsync();

        var response = await factory.CreateClient().PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { text = "hello there" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["run_started", "trace", "trace", "error"], events.Select(e => e.Name));
        Assert.True(events[^1].Data.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(events[^1].Data.GetProperty("message").GetString()));
        Assert.Equal(AgentRunStatus.Error, (await world.SingleRunAsync(sessionId)).Status);
    }

    [Fact]
    public async Task Unknown_session_is_404_problem_details()
    {
        var (factory, _) = await StartAsync(new ScriptedChatClient(Turn.Reply("never")));
        using var _ = factory;

        var response = await factory.CreateClient().PostAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/messages", new { text = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Invalid_text_is_400_and_closed_session_is_409_before_any_streaming()
    {
        var llm = new ScriptedChatClient(Turn.Reply("never"));
        var (factory, world) = await StartAsync(llm);
        using var _ = factory;
        var client = factory.CreateClient();
        var open = await world.NewSessionAsync();
        var closed = await world.NewSessionAsync(status: SessionStatus.Closed);

        var empty = await client.PostAsJsonAsync($"/api/sessions/{open}/messages", new { text = "  " });
        var tooLong = await client.PostAsJsonAsync($"/api/sessions/{open}/messages", new { text = new string('x', 1001) });
        var onClosed = await client.PostAsJsonAsync($"/api/sessions/{closed}/messages", new { text = "hello" });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("application/problem+json", tooLong.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Conflict, onClosed.StatusCode);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task Niches_list_the_three_businesses_with_avatar_greeting_and_quick_replies()
    {
        var (factory, _) = await StartAsync(new ScriptedChatClient());
        using var _ = factory;

        var niches = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/niches");

        Assert.Equal(["realestate", "dental", "restaurant"], niches.EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        var dental = niches[1];
        Assert.Equal("Alex", dental.GetProperty("persona").GetString());
        Assert.Equal("🦷", dental.GetProperty("avatar").GetProperty("emoji").GetString());
        Assert.Equal(2, dental.GetProperty("avatar").GetProperty("colors").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(dental.GetProperty("greeting").GetString()));
        Assert.True(dental.GetProperty("quickReplies").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Create_session_returns_greeting_and_quick_replies_and_stores_a_hashed_ip()
    {
        var (factory, world) = await StartAsync(new ScriptedChatClient());
        using var _ = factory;

        var response = await factory.CreateClient().PostAsJsonAsync("/api/sessions", new { niche = "dental" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = body.GetProperty("sessionId").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("greeting").GetString()));
        Assert.True(body.GetProperty("quickReplies").GetArrayLength() > 0);

        await using var db = world.NewDb();
        var session = await db.ChatSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Matches("^[0-9a-f]{32}$", session.ClientIpHash);
        var business = await db.Businesses.SingleAsync(b => b.Id == session.BusinessId);
        Assert.Equal(Niche.Clinic, business.Niche);
    }

    [Fact]
    public async Task Create_session_with_an_unknown_niche_is_400_problem_details()
    {
        var (factory, _) = await StartAsync(new ScriptedChatClient());
        using var _ = factory;

        var response = await factory.CreateClient().PostAsJsonAsync("/api/sessions", new { niche = "bakery" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_conversation_through_the_api_can_start_from_a_created_session()
    {
        var llm = new ScriptedChatClient(Turn.Reply("Sure!"));
        var (factory, _) = await StartAsync(llm);
        using var _ = factory;
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/sessions", new { niche = "restaurant" })).Content.ReadFromJsonAsync<JsonElement>();
        var response = await client.PostAsJsonAsync($"/api/sessions/{created.GetProperty("sessionId").GetGuid()}/messages", new { text = "Send me the menu" });

        var events = SseEvent.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("run_completed", events[^1].Name);
        Assert.Contains("Luigi", llm.Calls.Single()[0].Text); // the restaurant persona reached the model
    }
}

/// <summary>One parsed Server-Sent Event: the <c>event:</c> name and the JSON of <c>data:</c>.</summary>
internal sealed record SseEvent(string Name, JsonElement Data)
{
    public static List<SseEvent> Parse(string body) => body
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(block =>
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            return new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone());
        })
        .ToList();
}
