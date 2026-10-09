using System.ComponentModel;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary><c>handoff_to_human</c>: records the request and moves the session to <c>AwaitingHuman</c>.</summary>
internal sealed class HandoffTools(ChatbotDbContext db, ToolContext context, TimeProvider clock)
{
    private const int MaxReasonLength = 500;
    private const int MaxSummaryLength = 2000;

    public async Task<object> HandoffToHumanAsync(
        [Description("Why a person is needed, e.g. 'customer asked to talk to a person'.")]
        string? reason = null,
        [Description("Short summary of the conversation so far and what the customer needs, for the person who takes over.")]
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        reason = reason?.Trim();
        summary = summary?.Trim();
        if (string.IsNullOrEmpty(reason))
            return ToolSupport.Error("invalid_reason", "reason is required.");
        if (string.IsNullOrEmpty(summary))
            return ToolSupport.Error("invalid_summary", "summary is required.");

        var session = await db.ChatSessions.FirstOrDefaultAsync(
            s => s.Id == context.SessionId && s.BusinessId == context.BusinessId, cancellationToken);
        if (session is null)
            return ToolSupport.Error("session_not_found", "Session not found.");
        if (session.Status == SessionStatus.Closed)
            return ToolSupport.Error("session_closed", "The session is closed.");

        // One open request per session: asking twice returns the first one.
        var open = await db.HandoffRequests.AsNoTracking()
            .Where(h => h.SessionId == session.Id && h.Status == HandoffStatus.Open)
            .OrderBy(h => h.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var alreadyRequested = open is not null;
        open ??= db.HandoffRequests.Add(new HandoffRequest
        {
            SessionId = session.Id,
            Reason = Truncate(reason, MaxReasonLength),
            Summary = Truncate(summary, MaxSummaryLength),
            Status = HandoffStatus.Open,
            CreatedAt = clock.GetUtcNow(),
        }).Entity;

        session.Status = SessionStatus.AwaitingHuman;
        await db.SaveChangesAsync(cancellationToken);
        context.HandoffReason = open.Reason;

        return new
        {
            handoffId = open.Id,
            status = "Open",
            alreadyRequested,
            message = "A person from the business will continue this conversation. Tell the customer that, and do not start new flows.",
        };
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
