using System.Text.Json;
using System.Text.Json.Serialization;
using Chatbot.Core.Domain;

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

/// <summary>
/// One step of the run (model call, tool call, guardrail, handoff or error), sent as soon as it completes. It is also the
/// shape of the events in <c>GET /api/sessions/{id}/trace</c>, so the live panel and a reloaded one read the same JSON.
/// </summary>
public sealed record Trace(
    Guid RunId,
    int Sequence,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TraceEventType>))] TraceEventType Type,
    string Name,
    JsonElement? Input,
    JsonElement? Output,
    int? TokensIn,
    int? TokensOut,
    int DurationMs,
    decimal? CostUsd) : AgentEvent
{
    public static Trace From(TraceEvent e) =>
        new(e.RunId, e.Sequence, e.Type, e.Name, e.Input, e.Output, e.TokensIn, e.TokensOut, e.DurationMs, e.CostUsd);
}

/// <summary>The session moved to <c>AwaitingHuman</c> during this run.</summary>
public sealed record Handoff(string Reason) : AgentEvent;

/// <summary>Last event of a successful run.</summary>
public sealed record RunCompleted(
    Guid RunId, long TokensIn, long TokensOut, decimal CostUsd, int DurationMs, string Model, string Provider) : AgentEvent;

/// <summary>Last event of a failed run. <paramref name="Message"/> is safe to show to the user.</summary>
public sealed record Error(string Message, bool Retryable) : AgentEvent;
