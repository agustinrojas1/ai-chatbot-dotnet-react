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
    Guardrail,
    Handoff,
    Error,
}
