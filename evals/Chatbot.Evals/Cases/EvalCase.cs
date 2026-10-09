using System.Text.Json;

namespace Chatbot.Evals.Cases;

/// <summary>What a case is about; the pass rate is reported per category (SPEC 12).</summary>
internal enum EvalCategory
{
    Language,
    Search,
    Availability,
    Booking,
    Order,
    NotInCatalog,
    Handoff,
    SmallTalk,
    Injection,
    Gibberish,
}

/// <summary>The three businesses of the seed. <c>dental</c> is the clinic.</summary>
internal enum EvalNiche
{
    RealEstate,
    Dental,
    Restaurant,
}

internal enum ReplyLanguage
{
    English,
    Spanish,
}

/// <summary>One scripted conversation with the agent and what must hold afterwards.</summary>
/// <param name="Expect">Applies to the whole conversation. A turn can add its own <see cref="EvalTurn.Expect"/>.</param>
/// <param name="Rubric">Free text for a future LLM judge. The harness does not use it.</param>
/// <param name="Source">File and line the case was read from, for error messages.</param>
internal sealed record EvalCase(
    string Id,
    EvalCategory Category,
    EvalNiche Niche,
    IReadOnlyList<EvalTurn> Turns,
    Expectations Expect,
    string? Rubric,
    string Source);

/// <summary>One user message. Its <see cref="Expect"/> is checked against this turn only.</summary>
internal sealed record EvalTurn(string Say, Expectations Expect);

/// <summary>
/// Every expectation a case or a turn can have. At case level it is checked against the whole conversation (tool
/// calls of every turn, the booking and handoff state at the end); at turn level, against that turn.
/// </summary>
internal sealed record Expectations
{
    public static readonly Expectations None = new();

    public IReadOnlyList<ToolCallExpectation> ToolCalls { get; init; } = [];

    /// <summary>The <see cref="ToolCalls"/> must appear in the conversation in the order they are listed.</summary>
    public bool ToolsInOrder { get; init; }

    public IReadOnlyList<string> ForbidToolCalls { get; init; } = [];

    /// <summary>Language of the assistant replies. At case level it applies to every turn that has no language of its own.</summary>
    public ReplyLanguage? Language { get; init; }

    public IReadOnlyList<string> ForbidPhrases { get; init; } = [];

    public IReadOnlyList<MustMention> MustMention { get; init; } = [];

    /// <summary><c>true</c>: exactly one pending booking exists for the session. <c>false</c>: none exists.</summary>
    public bool? Booking { get; init; }

    /// <summary>Total the booking must have (an order).</summary>
    public decimal? BookingTotal { get; init; }

    /// <summary><c>true</c>: a handoff request exists for the session. <c>false</c>: none exists.</summary>
    public bool? Handoff { get; init; }

    public bool IsEmpty =>
        ToolCalls.Count == 0 && ForbidToolCalls.Count == 0 && Language is null && ForbidPhrases.Count == 0
        && MustMention.Count == 0 && Booking is null && BookingTotal is null && Handoff is null;
}

/// <summary>
/// A tool the agent must have called. Without <paramref name="Count"/> it is "at least once".
/// <paramref name="Args"/> is a partial match against the arguments the model sent (see <c>JsonMatch</c>).
/// </summary>
internal sealed record ToolCallExpectation(string Name, JsonElement? Args, int? Count);

/// <summary>Something the assistant must say.</summary>
internal abstract record MustMention;

/// <summary>At least one of the texts appears in the reply (case, accents and thousands separators ignored).</summary>
internal sealed record MentionText(IReadOnlyList<string> AnyOf) : MustMention;

/// <summary>
/// A value taken from the result of a tool the agent called in this conversation (for example every price of
/// <c>search_catalog</c>: <c>results[*].price</c>). It passes when the reply mentions at least one of them.
/// </summary>
internal sealed record MentionFromTool(string Tool, string Path) : MustMention;
