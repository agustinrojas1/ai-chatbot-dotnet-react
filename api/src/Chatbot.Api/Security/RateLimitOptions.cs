namespace Chatbot.Api.Security;

/// <summary>
/// Bound from the <c>RateLimit</c> configuration section (SPEC 9). Every value must be positive; the defaults are
/// what the public demo runs with.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Messages (<c>POST /api/sessions/{id}/messages</c>) one client IP may send per <see cref="IpWindowMinutes"/>.</summary>
    public int IpMessages { get; set; } = 30;

    /// <summary>Length of the sliding window of the per-IP limits, in minutes.</summary>
    public int IpWindowMinutes { get; set; } = 10;

    /// <summary>Sessions (<c>POST /api/sessions</c>) one client IP may create per <see cref="IpWindowMinutes"/>. Looser than the messages limit.</summary>
    public int IpSessions { get; set; } = 20;

    /// <summary>User messages one session accepts in total, counted from the database.</summary>
    public int SessionMessages { get; set; } = 25;

    /// <summary>Agent runs (each one is a paid model call) accepted per UTC day across all clients, counted from the database.</summary>
    public int DailyRequests { get; set; } = 500;

    public static bool IsValid(RateLimitOptions o) =>
        o.IpMessages > 0 && o.IpWindowMinutes > 0 && o.IpSessions > 0 && o.SessionMessages > 0 && o.DailyRequests > 0;
}
