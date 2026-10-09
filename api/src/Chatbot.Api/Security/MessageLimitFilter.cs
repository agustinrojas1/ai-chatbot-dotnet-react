using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chatbot.Api.Security;

/// <summary>
/// The limits of <c>POST /api/sessions/{id}/messages</c> that need the database, checked before the agent creates a run
/// (so a refused message costs nothing and stores nothing). Both are counted from rows, so they survive a restart,
/// are shared by every API instance and cannot be dodged by rotating IPs:
/// <list type="bullet">
/// <item>per session: user <see cref="ChatMessage"/> rows of the session (<c>session_limit</c>, final);</item>
/// <item>global: <see cref="AgentRun"/> rows started since 00:00 UTC (<c>daily_cap</c>, retry after midnight).</item>
/// </list>
/// The per-IP limit is checked earlier, by the rate limiter middleware. Counting and inserting are not one atomic
/// step, so a burst of concurrent requests can overshoot a limit by a few messages. For a cost cap that is acceptable.
/// </summary>
internal sealed class MessageLimitFilter(
    ChatbotDbContext db,
    IOptions<RateLimitOptions> options,
    TimeProvider clock,
    ILogger<MessageLimitFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var limits = options.Value;

        // An unknown or malformed id is not counted here: the endpoint answers it (404) without creating anything.
        if (Guid.TryParse(http.Request.RouteValues["id"] as string, out var sessionId))
        {
            var sent = await db.ChatMessages.AsNoTracking()
                .CountAsync(m => m.SessionId == sessionId && m.Role == MessageRole.User, ct);
            if (sent >= limits.SessionMessages)
            {
                logger.LogWarning("Session limit reached: session {SessionId} has {Sent} of {Limit} messages", sessionId, sent, limits.SessionMessages);
                return LimitResponse.TooManyRequests(http, LimitCodes.SessionLimit,
                    $"This demo conversation reached its limit of {limits.SessionMessages} messages. Restart the demo to keep chatting.");
            }
        }

        var now = clock.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var today = await db.AgentRuns.AsNoTracking().CountAsync(r => r.StartedAt >= dayStart, ct);
        if (today >= limits.DailyRequests)
        {
            logger.LogWarning("Daily cap reached: {Today} of {Limit} runs since {DayStart:u}", today, limits.DailyRequests, dayStart);
            return LimitResponse.TooManyRequests(http, LimitCodes.DailyCap,
                "The demo reached its daily usage limit. Please come back tomorrow.",
                retryAfter: dayStart.AddDays(1) - now);
        }

        return await next(context);
    }
}
