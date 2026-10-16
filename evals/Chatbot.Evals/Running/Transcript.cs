using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Evals.Cases;

namespace Chatbot.Evals.Running;

/// <summary>A passage a <c>Retrieval</c> event returned. <paramref name="Relevant"/>: it passed the threshold, so the model was given it.</summary>
internal sealed record RetrievedPassage(string SourceId, bool Relevant)
{
    /// <summary>The document: <c>cancellation-and-no-show#1</c> is in <c>cancellation-and-no-show</c>.</summary>
    public string Slug => SourceId.Contains('#') ? SourceId[..SourceId.LastIndexOf('#')] : SourceId;
}

/// <summary>A <c>search_knowledge_base</c> call as the trace stores it (a <c>Retrieval</c> event).</summary>
internal sealed record RetrievalRecord(string Query, bool ThresholdPassed, IReadOnlyList<RetrievedPassage> Passages);

/// <summary>One tool call of the agent: what the model sent and what the tool answered (both as the trace stores them).</summary>
internal sealed record ToolCallRecord(string Name, JsonElement? Arguments, JsonElement? Result);

/// <summary>
/// A booking row of the session after a turn. <paramref name="ServerTotal"/> is the total recomputed from the
/// catalog prices and the order lines, independently of the stored <paramref name="Total"/>; null for bookings without lines.
/// </summary>
internal sealed record BookingSnapshot(BookingStatus Status, decimal? Total, decimal? ServerTotal, string CustomerName);

/// <summary>Everything observed while the agent processed one user message.</summary>
internal sealed record TurnResult
{
    public required string User { get; init; }
    public string Reply { get; init; } = "";
    public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = [];

    /// <summary>The knowledge base searches of the turn. Each one is also in <see cref="ToolCalls"/> (as <c>search_knowledge_base</c>).</summary>
    public IReadOnlyList<RetrievalRecord> Retrievals { get; init; } = [];

    /// <summary>Why the turn produced no answer (provider error, rejected message, skipped); null when the run completed.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Set when the provider refused the turn (quota) even after the retries. It says nothing about the agent, so
    /// the case is reported as not run instead of failed.
    /// </summary>
    public string? InfrastructureError { get; init; }

    public long TokensIn { get; init; }
    public long TokensOut { get; init; }
    public decimal CostUsd { get; init; }
    public int DurationMs { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }

    /// <summary>Bookings of the session after this turn.</summary>
    public IReadOnlyList<BookingSnapshot> Bookings { get; init; } = [];

    /// <summary>Handoff requests of the session after this turn.</summary>
    public int Handoffs { get; init; }
}

/// <summary>One run of one case: the conversation as it happened. The evaluator turns it into a verdict.</summary>
/// <param name="ProtectedTerms">
/// Names of the business, its offerings and their neighbourhoods. The language check ignores them, because a
/// reply in English may name "Núñez" and one in Spanish "Teeth Cleaning" without being in the wrong language.
/// </param>
internal sealed record Transcript(EvalCase Case, int Iteration, IReadOnlyList<TurnResult> Turns, IReadOnlyList<string> ProtectedTerms)
{
    /// <summary>What kept the case from running (<c>infrastructure: provider quota</c>), or null when it ran.</summary>
    public string? NotRunReason => Turns.Select(t => t.InfrastructureError).FirstOrDefault(e => e is not null);
}
