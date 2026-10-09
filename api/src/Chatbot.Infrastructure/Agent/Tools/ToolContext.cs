namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary>
/// Server-side identity of the conversation the tools run for. The business and session ids are set by the
/// server when the request starts and are never visible to the model as tool parameters.
/// </summary>
public sealed class ToolContext(Guid businessId, Guid sessionId, string timezone)
{
    public Guid BusinessId { get; } = businessId;
    public Guid SessionId { get; } = sessionId;

    /// <summary>IANA time zone id of the business; every time returned to the model uses it.</summary>
    public string Timezone { get; } = timezone;

    /// <summary>Set by <c>handoff_to_human</c> so the agent loop knows a handoff happened during this run.</summary>
    public string? HandoffReason { get; set; }
}
