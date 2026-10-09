using System.Net;
using System.Net.Http.Json;
using Chatbot.Core.Domain;
using Chatbot.Tests.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chatbot.Tests.Security;

/// <summary>A clock that stands still until the test moves it.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// TestServer has no network peer, so <c>Connection.RemoteIpAddress</c> is null. This runs first in the pipeline and sets
/// it from the <c>X-Test-Remote-Ip</c> header: the address a real proxy or client would connect from.
/// </summary>
internal sealed class FakeRemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var ip))
                context.Connection.RemoteIpAddress = ip;
            return nextMiddleware(context);
        });
        next(app);
    };
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>The real HTTP pipeline over an existing <see cref="AgentWorld"/> (so a second host can share the first one's database).</summary>
internal static class SecurityHost
{
    public static WebApplicationFactory<Program> Start(
        AgentWorld world,
        IReadOnlyDictionary<string, string>? settings = null,
        TimeProvider? clock = null,
        ILoggerProvider? logs = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // "Testing": Development would migrate and seed the database at startup.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", world.ConnectionString);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.AddKeyedSingleton<IChatClient>("llm", world.Llm);
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
                if (clock is not null)
                    services.AddSingleton(clock);
            });
            if (logs is not null)
                builder.ConfigureLogging(l => l.AddProvider(logs));
        });

    /// <summary>The host must refuse to start, and the error must name the setting that is wrong.</summary>
    public static void AssertFailsToStart(WebApplicationFactory<Program> factory, string setting)
    {
        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(setting, failure.ToString());
    }

    /// <summary>A model that always answers "ok".</summary>
    public static ScriptedChatClient AlwaysOk() => new((_, _) => Turn.Reply("ok"));

    /// <summary>Sends a message and reads the whole response (an SSE stream must be consumed to finish the run).</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid sessionId, string text = "hello there", string? remoteIp = null, string? forwardedFor = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/messages") { Content = JsonContent.Create(new { text }) };
        Decorate(request, remoteIp, forwardedFor, origin);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    public static async Task<HttpResponseMessage> CreateSessionAsync(
        HttpClient client, string niche = "dental", string? remoteIp = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new { niche }) };
        Decorate(request, remoteIp, forwardedFor, null);
        return await client.SendAsync(request);
    }

    private static void Decorate(HttpRequestMessage request, string? remoteIp, string? forwardedFor, string? origin)
    {
        if (remoteIp is not null)
            request.Headers.Add(FakeRemoteIpStartupFilter.Header, remoteIp);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
    }

    /// <summary>Stores <paramref name="count"/> messages of a role in a session, bypassing the API.</summary>
    public static async Task AddMessagesAsync(AgentWorld world, Guid sessionId, MessageRole role, int count)
    {
        await using var db = world.NewDb();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < count; i++)
            db.ChatMessages.Add(new Chatbot.Core.Domain.ChatMessage { SessionId = sessionId, Role = role, Content = $"{role} {i}", CreatedAt = start.AddSeconds(i) });
        await db.SaveChangesAsync();
    }

    public static async Task<int> UserMessageCountAsync(AgentWorld world, Guid sessionId)
    {
        await using var db = world.NewDb();
        return await db.ChatMessages.CountAsync(m => m.SessionId == sessionId && m.Role == MessageRole.User);
    }

    public static async Task<int> RunCountAsync(AgentWorld world)
    {
        await using var db = world.NewDb();
        return await db.AgentRuns.CountAsync();
    }
}
