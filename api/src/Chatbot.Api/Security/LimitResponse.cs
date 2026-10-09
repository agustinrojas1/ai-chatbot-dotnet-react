using System.Globalization;

namespace Chatbot.Api.Security;

/// <summary>Machine-readable reason of a 429, in the <c>code</c> field of <see cref="LimitBody"/>.</summary>
internal static class LimitCodes
{
    public const string IpRateLimit = "ip_rate_limit";
    public const string SessionLimit = "session_limit";
    public const string DailyCap = "daily_cap";
}

/// <summary>Body of every 429: <c>{ "message": "...", "code": "ip_rate_limit" | "session_limit" | "daily_cap" }</c>.</summary>
internal sealed record LimitBody(string Message, string Code);

internal static class LimitResponse
{
    /// <summary>
    /// A 429 with the JSON body and, when waiting helps, <c>Retry-After</c> in whole seconds. The session limit never
    /// passes, so it has no <c>Retry-After</c>.
    /// </summary>
    public static IResult TooManyRequests(HttpContext http, string code, string message, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return Results.Json(new LimitBody(message, code), statusCode: StatusCodes.Status429TooManyRequests);
    }
}
