namespace Chatbot.Core.Domain;

public enum Niche
{
    RealEstate,
    Clinic,
    Restaurant,
}

public enum OfferingKind
{
    Property,
    Treatment,
    MenuItem,
}

public enum BookingStatus
{
    PendingConfirmation,
    Confirmed,
    Cancelled,
}

public enum SessionStatus
{
    Active,
    AwaitingHuman,
    Closed,
}

public enum HandoffStatus
{
    Open,
    Closed,
}

public enum MessageRole
{
    User,
    Assistant,

    /// <summary>One executed tool call and its result (JSON). Context for the model only: it is never shown as a chat bubble.</summary>
    Tool,
}

public enum AgentRunStatus
{
    Ok,
    Error,
}

/// <summary>
/// Closed set of trace event types. Persisted as a string and guarded by a CHECK constraint,
/// so the trace panel can aggregate metrics by type without tools inventing their own.
/// </summary>
public enum TraceEventType
{
    LlmCall,
    ToolCall,

    /// <summary>One <c>search_knowledge_base</c> call (M8): the query, the passages with their scores and the embedding cost. Not also a <c>ToolCall</c>.</summary>
    Retrieval,
    Guardrail,
    Handoff,
    Error,
}
