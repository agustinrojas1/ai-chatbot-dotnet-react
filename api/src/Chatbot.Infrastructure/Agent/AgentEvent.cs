namespace Chatbot.Infrastructure.Agent;

/// <summary>
/// What <see cref="AgentService.RunAsync"/> reports while a message is processed. The Api maps each record to one
/// SSE event (SPEC 10); the public property names are the JSON field names of that event's <c>data</c>.
/// </summary>
public abstract record AgentEvent;

/// <summary>Why a message was refused before any run was created.</summary>
public enum RejectionReason
{
    EmptyText,
    TextTooLong,
    SessionNotFound,
    SessionClosed,
}

/// <summary>Always the only event of the stream. It is an HTTP error for the caller, never an SSE event.</summary>
public sealed record Rejected(RejectionReason Reason, string Message) : AgentEvent;

/// <summary>First event of every accepted message.</summary>
public sealed record RunStarted(Guid RunId) : AgentEvent;

/// <summary>A piece of the assistant answer, in the order the model produced it.</summary>
public sealed record Token(string Text) : AgentEvent;

/// <summary>The session moved to <c>AwaitingHuman</c> during this run.</summary>
public sealed record Handoff(string Reason) : AgentEvent;

/// <summary>Last event of a successful run.</summary>
public sealed record RunCompleted(
    Guid RunId, long TokensIn, long TokensOut, decimal CostUsd, int DurationMs, string Model, string Provider) : AgentEvent;

/// <summary>Last event of a failed run. <paramref name="Message"/> is safe to show to the user.</summary>
public sealed record Error(string Message, bool Retryable) : AgentEvent;
