using System.Text.Json;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Api.Endpoints;

/// <summary>What the UI needs to restore a session after a reload: the chat bubbles and the trace panel.</summary>
internal static class SessionHistoryEndpoints
{
    public static IEndpointRouteBuilder MapSessionHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{id:guid}/messages", ListMessagesAsync);
        app.MapGet("/api/sessions/{id:guid}/trace", GetTraceAsync);
        return app;
    }

    /// <summary>User and assistant messages in order. <c>Tool</c> rows are model context only and never listed.</summary>
    private static async Task<IResult> ListMessagesAsync(Guid id, ChatbotDbContext db, CancellationToken ct)
    {
        var session = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
            return SessionNotFound();

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == id && m.Role != MessageRole.Tool)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            messages = messages.Select(m => new
            {
                id = m.Id,
                role = m.Role == MessageRole.User ? "user" : "assistant",
                text = m.Content,
                createdAt = m.CreatedAt,
            }),
            sessionStatus = session.Status.ToString(),
            language = session.Language,
        });
    }

    /// <summary>The runs of the session by start time, each with its events in sequence order (the same JSON as the SSE <c>trace</c> event).</summary>
    private static async Task<IResult> GetTraceAsync(Guid id, ChatbotDbContext db, CancellationToken ct)
    {
        if (!await db.ChatSessions.AnyAsync(s => s.Id == id, ct))
            return SessionNotFound();

        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.SessionId == id)
            .OrderBy(r => r.StartedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var runIds = runs.Select(r => r.Id).ToList();
        var events = (await db.TraceEvents.AsNoTracking()
                .Where(e => runIds.Contains(e.RunId))
                .OrderBy(e => e.RunId).ThenBy(e => e.Sequence)
                .ToListAsync(ct))
            .ToLookup(e => e.RunId);

        var messageIds = runs.Select(r => r.UserMessageId).ToList();
        var userMessages = await db.ChatMessages.AsNoTracking()
            .Where(m => messageIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Content, ct);

        return Results.Ok(new
        {
            sessionId = id,
            runs = runs.Select(r =>
            {
                var runEvents = events[r.Id].ToList();
                return new
                {
                    id = r.Id,
                    // The public trace never shows a phone number, also not the one the customer typed.
                    userMessage = PhoneMasker.MaskInText(userMessages.GetValueOrDefault(r.UserMessageId) ?? ""),
                    status = r.Status.ToString(),
                    model = r.Model,
                    provider = ProviderLabel(runEvents),
                    tokensIn = r.TokensIn,
                    tokensOut = r.TokensOut,
                    costUsd = r.CostUsd,
                    durationMs = r.DurationMs,
                    startedAt = r.StartedAt,
                    events = runEvents.Select(Trace.From),
                };
            }),
        });
    }

    /// <summary>Providers that answered the run, in order of first use. They are only stored inside each <c>LlmCall</c> output.</summary>
    private static string ProviderLabel(List<TraceEvent> events)
    {
        var providers = events
            .Where(e => e.Type == TraceEventType.LlmCall && e.Output is { ValueKind: JsonValueKind.Object } output
                && output.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String)
            .Select(e => e.Output!.Value.GetProperty("provider").GetString()!)
            .Distinct()
            .ToList();
        return providers.Count == 0 ? "unknown" : string.Join(", ", providers);
    }

    private static IResult SessionNotFound() =>
        Results.Problem(title: "Session not found", detail: "Session not found.", statusCode: StatusCodes.Status404NotFound);
}
