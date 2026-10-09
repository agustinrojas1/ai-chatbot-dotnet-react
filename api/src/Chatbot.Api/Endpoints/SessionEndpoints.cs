using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Api.Endpoints;

internal sealed record CreateSessionRequest(string? Niche);

internal sealed record SendMessageRequest(string? Text);

internal static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions", CreateSessionAsync);
        app.MapPost("/api/sessions/{id:guid}/messages", SendMessageAsync);
        return app;
    }

    private static async Task<IResult> CreateSessionAsync(
        CreateSessionRequest request, ChatbotDbContext db, IConfiguration configuration, TimeProvider clock,
        HttpContext http, CancellationToken ct)
    {
        var niche = NicheCatalog.FindById(request.Niche);
        if (niche is null)
        {
            return Results.Problem(
                title: "Unknown niche",
                detail: $"niche must be one of: {string.Join(", ", NicheCatalog.All.Select(n => n.Id))}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Niche == niche.Niche, ct);
        if (business is null)
        {
            return Results.Problem(
                title: "Niche not available",
                detail: "The demo data is not loaded.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var now = clock.GetUtcNow();
        var salt = configuration["Security:IpHashSalt"] ?? ClientIp.DefaultSalt;
        var session = new ChatSession
        {
            BusinessId = business.Id,
            ClientIpHash = ClientIp.Hash(http.Connection.RemoteIpAddress?.ToString(), salt),
            CreatedAt = now,
            LastActivityAt = now,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/sessions/{session.Id}",
            new { sessionId = session.Id, greeting = business.Greeting, quickReplies = business.QuickReplies });
    }

    /// <summary>
    /// Validation problems become a normal JSON error. Otherwise the response is an SSE stream: from the first byte
    /// the status is 200, so anything that goes wrong later is reported as an <c>error</c> event.
    /// </summary>
    private static async Task<IResult> SendMessageAsync(Guid id, SendMessageRequest request, AgentService agent, HttpContext http)
    {
        var ct = http.RequestAborted;
        await using var events = agent.RunAsync(id, request.Text, ct).GetAsyncEnumerator(ct);

        // A rejection is always the only event, so the first one tells whether we can start streaming.
        if (!await events.MoveNextAsync())
            return Results.Empty;
        if (events.Current is Rejected rejected)
            return Rejection(rejected);

        try
        {
            await SseWriter.StartAsync(http.Response, ct);
            do
            {
                await SseWriter.WriteAsync(http.Response, events.Current, ct);
            }
            while (await events.MoveNextAsync());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away; disposing the enumerator closes the run.
        }

        return Results.Empty;
    }

    private static IResult Rejection(Rejected rejected) => rejected.Reason switch
    {
        RejectionReason.SessionNotFound => Results.Problem(title: "Session not found", detail: rejected.Message, statusCode: StatusCodes.Status404NotFound),
        RejectionReason.SessionClosed => Results.Problem(title: "Session closed", detail: rejected.Message, statusCode: StatusCodes.Status409Conflict),
        _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [rejected.Message] }),
    };
}
