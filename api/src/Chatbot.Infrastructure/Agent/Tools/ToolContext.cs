using Chatbot.Infrastructure.Knowledge;

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

    /// <summary>
    /// Set by <c>search_knowledge_base</c> with the full outcome of the search (scores, thresholds, embedding usage), which is
    /// more than the model sees. The tool tracer takes it right after the call to record the <c>Retrieval</c> event.
    /// </summary>
    public KnowledgeSearchResult? PendingRetrieval { get; set; }

    /// <summary>Returns <see cref="PendingRetrieval"/> and clears it, so each search is recorded once.</summary>
    public KnowledgeSearchResult? TakeRetrieval()
    {
        var retrieval = PendingRetrieval;
        PendingRetrieval = null;
        return retrieval;
    }
}
