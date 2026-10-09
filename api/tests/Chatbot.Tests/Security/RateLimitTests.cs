using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using static Chatbot.Tests.Security.SecurityHost;

namespace Chatbot.Tests.Security;

/// <summary>SPEC 9: per-IP, per-session and daily limits, through the real HTTP pipeline with a fake model.</summary>
[Collection(PostgresCollection.Name)]
public class RateLimitTests(PostgresFixture postgres)
{
    private const string ClientA = "203.0.113.5";
    private const string ClientB = "203.0.113.6";

    private static async Task<(string Code, string Message)> ReadLimitAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("code").GetString()!, body.GetProperty("message").GetString()!);
    }

    private static int RetryAfterSeconds(HttpResponseMessage response) =>
        int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")));

    [Fact]
    public async Task The_per_ip_limit_trips_at_n_plus_1_with_a_json_body_and_retry_after()
    {
        var llm = AlwaysOk();
        var world = await AgentWorld.CreateAsync(postgres, llm);
        using var factory = Start(world, new Dictionary<string, string> { ["RateLimit:IpMessages"] = "3", ["RateLimit:IpWindowMinutes"] = "10" });
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: ClientA)).StatusCode);

        var rejected = await SendAsync(client, session, remoteIp: ClientA);

        var (code, message) = await ReadLimitAsync(rejected);
        Assert.Equal("ip_rate_limit", code);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.InRange(RetryAfterSeconds(rejected), 1, 600);
        Assert.Equal(3, llm.Calls.Count);                                  // the model was never reached
        Assert.Equal(3, await UserMessageCountAsync(world, session));      // and nothing was stored

        // Another client is not affected.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: ClientB)).StatusCode);
    }

    [Fact]
    public async Task The_rejection_also_carries_the_cors_headers_so_the_browser_can_read_it()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string>
        {
            ["RateLimit:IpMessages"] = "1",
            ["Cors:AllowedOrigin"] = "https://demo.example.com",
        });
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();
        await SendAsync(client, session, remoteIp: ClientA, origin: "https://demo.example.com");

        var rejected = await SendAsync(client, session, remoteIp: ClientA, origin: "https://demo.example.com");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("https://demo.example.com", Assert.Single(rejected.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("Retry-After", Assert.Single(rejected.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Fact]
    public async Task Creating_sessions_has_its_own_looser_per_ip_limit()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string> { ["RateLimit:IpMessages"] = "1", ["RateLimit:IpSessions"] = "2" });
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();

        // Using up the message limit does not block creating sessions...
        await SendAsync(client, session, remoteIp: ClientA);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, session, remoteIp: ClientA)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionAsync(client, remoteIp: ClientA)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionAsync(client, remoteIp: ClientA)).StatusCode);

        // ...but sessions cannot be minted without end.
        var rejected = await CreateSessionAsync(client, remoteIp: ClientA);
        Assert.Equal("ip_rate_limit", (await ReadLimitAsync(rejected)).Code);
        Assert.InRange(RetryAfterSeconds(rejected), 1, 600);
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionAsync(client, remoteIp: ClientB)).StatusCode);
    }

    [Fact]
    public async Task The_session_limit_is_counted_from_the_database_and_survives_a_new_host()
    {
        var llm = AlwaysOk();
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var settings = new Dictionary<string, string> { ["RateLimit:SessionMessages"] = "3", ["RateLimit:IpMessages"] = "1000" };
        var session = await world.NewSessionAsync();
        var other = await world.NewSessionAsync();

        using (var first = Start(world, settings))
        {
            var client = first.CreateClient();
            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: $"198.51.100.{i + 1}")).StatusCode);
        }

        // A fresh host (a restart) and a brand new client IP: the session is still full.
        using var second = Start(world, settings);
        var rejected = await SendAsync(second.CreateClient(), session, remoteIp: "192.0.2.77");

        var (code, message) = await ReadLimitAsync(rejected);
        Assert.Equal("session_limit", code);
        Assert.Contains("3 messages", message);
        Assert.False(rejected.Headers.Contains("Retry-After")); // waiting does not help: the session is over
        Assert.Equal(3, llm.Calls.Count);
        Assert.Equal(3, await UserMessageCountAsync(world, session));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(second.CreateClient(), other)).StatusCode);
    }

    [Fact]
    public async Task The_default_session_limit_is_25_user_messages_and_only_user_rows_count()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world);
        var client = factory.CreateClient();
        var full = await world.NewSessionAsync();
        var almostFull = await world.NewSessionAsync();
        await AddMessagesAsync(world, full, MessageRole.User, 25);
        await AddMessagesAsync(world, almostFull, MessageRole.User, 24);
        await AddMessagesAsync(world, almostFull, MessageRole.Assistant, 30);

        Assert.Equal("session_limit", (await ReadLimitAsync(await SendAsync(client, full))).Code);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, almostFull)).StatusCode);
        Assert.Equal("session_limit", (await ReadLimitAsync(await SendAsync(client, almostFull))).Code);
    }

    [Fact]
    public async Task The_daily_cap_is_checked_before_a_run_is_created_and_survives_a_new_host()
    {
        var llm = AlwaysOk();
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 23, 59, 0, TimeSpan.Zero));
        var settings = new Dictionary<string, string> { ["RateLimit:DailyRequests"] = "2", ["RateLimit:IpMessages"] = "1000" };
        var first = await world.NewSessionAsync();
        var second = await world.NewSessionAsync();
        var third = await world.NewSessionAsync();

        using (var host = Start(world, settings, clock))
        {
            var client = host.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, first)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, second)).StatusCode);

            var rejected = await SendAsync(client, third);

            var (code, message) = await ReadLimitAsync(rejected);
            Assert.Equal("daily_cap", code);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Equal(60, RetryAfterSeconds(rejected)); // until 00:00 UTC
        }

        Assert.Equal(2, llm.Calls.Count);
        Assert.Equal(2, await RunCountAsync(world));
        Assert.Equal(0, await UserMessageCountAsync(world, third)); // refused before anything was created

        // Restart: the count comes from the database, so the cap still holds.
        using (var restarted = Start(world, settings, clock))
            Assert.Equal("daily_cap", (await ReadLimitAsync(await SendAsync(restarted.CreateClient(), third))).Code);

        // The next UTC day starts a new count.
        clock.Now = new DateTimeOffset(2026, 10, 10, 0, 0, 1, TimeSpan.Zero);
        using var tomorrow = Start(world, settings, clock);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(tomorrow.CreateClient(), third)).StatusCode);
    }

    [Fact]
    public async Task Failed_runs_count_towards_the_daily_cap()
    {
        var llm = new ScriptedChatClient((_, _) => Turn.Fail(new HttpRequestException("down", null, HttpStatusCode.BadGateway)));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        using var factory = Start(world, new Dictionary<string, string> { ["RateLimit:DailyRequests"] = "1" });
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();

        Assert.Equal(AgentRunStatus.Error, await RunStatusAfterAsync(client, session, world));
        Assert.Equal("daily_cap", (await ReadLimitAsync(await SendAsync(client, session))).Code);
    }

    private static async Task<AgentRunStatus> RunStatusAfterAsync(HttpClient client, Guid session, AgentWorld world)
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session)).StatusCode);
        return (await world.SingleRunAsync(session)).Status;
    }

    [Fact]
    public async Task Limits_do_not_apply_to_get_endpoints_or_health()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string>
        {
            ["RateLimit:IpMessages"] = "1",
            ["RateLimit:IpSessions"] = "1",
            ["RateLimit:SessionMessages"] = "1",
            ["RateLimit:DailyRequests"] = "1",
        });
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();
        await SendAsync(client, session, remoteIp: ClientA);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, session, remoteIp: ClientA)).StatusCode);

        foreach (var path in new[] { "/api/niches", "/health", "/health/ready", $"/api/sessions/{session}/trace", $"/api/sessions/{session}/messages" })
        {
            for (var i = 0; i < 5; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add(FakeRemoteIpStartupFilter.Header, ClientA);
                var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} #{i} returned {(int)response.StatusCode}");
            }
        }

    }

    [Fact]
    public async Task Admin_requests_are_limited_per_ip_so_the_key_cannot_be_guessed_at_full_speed()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string> { ["Admin:Key"] = "right", ["RateLimit:AdminRequests"] = "3" });
        var client = factory.CreateClient();

        HttpRequestMessage Guess(string key, string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/bookings");
            request.Headers.Add("X-Admin-Key", key);
            request.Headers.Add(FakeRemoteIpStartupFilter.Header, ip);
            return request;
        }

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Guess($"wrong-{i}", ClientA))).StatusCode);

        var rejected = await client.SendAsync(Guess("right", ClientA)); // even the right key waits: the limit comes first
        Assert.Equal("ip_rate_limit", (await ReadLimitAsync(rejected)).Code);
        Assert.InRange(RetryAfterSeconds(rejected), 1, 600);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Guess("right", ClientB))).StatusCode);
    }

    [Fact]
    public async Task A_zero_limit_is_refused_at_startup_instead_of_disabling_the_cap()
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string> { ["RateLimit:DailyRequests"] = "0" });

        AssertFailsToStart(factory, "RateLimit");
    }
}
