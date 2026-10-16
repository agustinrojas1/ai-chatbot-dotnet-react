using System.Globalization;
using System.Text.Json;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Running;

namespace Chatbot.Evals.Checking;

/// <summary>What a check is about. The MVP targets (SPEC 12) are stated per dimension.</summary>
internal enum CheckDimension
{
    /// <summary>The agent answered every turn (no provider error). Not a target, but a run that failed cannot pass.</summary>
    Execution,
    Language,
    ForbiddenPhrases,

    /// <summary>Tool use and its effects: tools called or not, bookings and handoffs left in the database.</summary>
    ToolUse,

    /// <summary>What the reply says (<c>mustMention</c>).</summary>
    Content,
}

internal sealed record CheckResult(CheckDimension Dimension, string Name, bool Passed, string? Detail = null)
{
    public override string ToString() => Passed ? Name : Detail is null ? Name : $"{Name}: {Detail}";
}

/// <summary>The verdict on one run: it passes when every check passes.</summary>
internal sealed record CaseResult(Transcript Transcript, IReadOnlyList<CheckResult> Checks)
{
    /// <summary>The provider refused a turn (quota) after every retry: nothing can be said about the agent, so the run is neither passed nor failed.</summary>
    public bool NotRun => Transcript.NotRunReason is not null;

    public bool Passed => !NotRun && Checks.All(c => c.Passed);

    /// <summary>It ran and at least one check failed.</summary>
    public bool Failed => !NotRun && !Passed;

    public IEnumerable<CheckResult> Failures => Checks.Where(c => !c.Passed);

    public long TokensIn => Transcript.Turns.Sum(t => t.TokensIn);
    public long TokensOut => Transcript.Turns.Sum(t => t.TokensOut);
    public decimal CostUsd => Transcript.Turns.Sum(t => t.CostUsd);
}

/// <summary>
/// Judges a <see cref="Transcript"/> against the expectations of its case. Pure: it reads the recorded conversation
/// and nothing else, so every kind of expectation can be tested without a model or a database.
/// </summary>
internal static class Evaluator
{
    /// <summary>
    /// Phrases no reply may contain, in every case. The agent registers requests, it never confirms them and it
    /// sends no emails (a confirmed booking or a promised email is the lie this project exists to avoid), and
    /// the system prompt and the tool names are not for customers. Hedged mentions ("not confirmed yet") are allowed, see <see cref="TextMatch"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultForbiddenPhrases =
    [
        "confirmed", "confirmada", "confirmado",
        "I've sent you an email", "I have sent you an email", "I sent you an email", "te envié un correo", "te envié un email", "te mandé un mail",
        "COMMON RULES", "WHATSAPP FORMATTING RULES", "CONVERSATION QUALITY", "FEW-SHOT EXAMPLES", "BUSINESS FACTS", "Session status:",
        "search_catalog", "get_offering_details", "check_availability", "create_booking", "handoff_to_human", "search_knowledge_base", "<passage", "AwaitingHuman",
    ];

    public static CaseResult Evaluate(Transcript transcript)
    {
        var evalCase = transcript.Case;
        var turns = transcript.Turns;
        var checks = new List<CheckResult>();

        if (transcript.NotRunReason is { } reason)
        {
            var refused = transcript.Turns.Select((t, i) => (Turn: t, Number: i + 1)).First(x => x.Turn.InfrastructureError is not null);
            return new CaseResult(transcript, [new CheckResult(CheckDimension.Execution, reason, false, $"turn {refused.Number}: {refused.Turn.Error}")]);
        }

        CheckExecution(turns, checks);
        CheckLanguage(transcript, checks);
        CheckForbiddenPhrases(transcript, checks);

        CheckScope(evalCase.Expect, turns, turns[^1], "", checks);
        for (var i = 0; i < evalCase.Turns.Count; i++)
        {
            if (!evalCase.Turns[i].Expect.IsEmpty)
                CheckScope(evalCase.Turns[i].Expect, [turns[i]], turns[i], $"turn {i + 1}: ", checks);
        }

        return new CaseResult(transcript, checks);
    }

    private static void CheckExecution(IReadOnlyList<TurnResult> turns, List<CheckResult> checks)
    {
        var failed = turns.Select((t, i) => (Turn: t, Number: i + 1)).Where(x => x.Turn.Error is not null).ToList();
        checks.Add(failed.Count == 0
            ? new CheckResult(CheckDimension.Execution, "agent answered every turn", true)
            : new CheckResult(CheckDimension.Execution, "agent answered every turn", false,
                string.Join("; ", failed.Select(x => $"turn {x.Number}: {x.Turn.Error}"))));
    }

    /// <summary>The expected language of each reply: the turn's own, else the case's.</summary>
    private static void CheckLanguage(Transcript transcript, List<CheckResult> checks)
    {
        for (var i = 0; i < transcript.Turns.Count; i++)
        {
            var expected = transcript.Case.Turns[i].Expect.Language ?? transcript.Case.Expect.Language;
            if (expected is not { } language)
                continue;

            var reply = transcript.Turns[i].Reply;
            var name = $"turn {i + 1}: reply is in {(language == ReplyLanguage.Spanish ? "Spanish" : "English")}";
            var passed = reply.Length > 0 && IsInLanguage(reply, language, transcript.ProtectedTerms);
            checks.Add(new CheckResult(CheckDimension.Language, name, passed,
                passed ? null : reply.Length == 0 ? "there is no reply" : $"reply reads as the other language: \"{Shorten(reply)}\""));
        }
    }

    /// <summary>
    /// Uses the detector of the agent itself (SPEC 7.3) on the reply without the names of the catalog. The detector
    /// keeps the previous language on a tie, so the expected language is given as the previous one: the check fails
    /// when the reply reads as the other language, not when it is too short to read as any.
    /// </summary>
    public static bool IsInLanguage(string reply, ReplyLanguage expected, IReadOnlyList<string> protectedTerms)
    {
        foreach (var term in protectedTerms)
            reply = reply.Replace(term, " ", StringComparison.OrdinalIgnoreCase);

        var wanted = expected == ReplyLanguage.Spanish ? Language.Spanish : Language.English;
        return LanguageDetector.Detect(reply, wanted) == wanted;
    }

    private static void CheckForbiddenPhrases(Transcript transcript, List<CheckResult> checks)
    {
        var hits = new List<string>();
        for (var i = 0; i < transcript.Turns.Count; i++)
        {
            var phrases = DefaultForbiddenPhrases
                .Concat(transcript.Case.Expect.ForbidPhrases)
                .Concat(transcript.Case.Turns[i].Expect.ForbidPhrases);
            hits.AddRange(phrases
                .Where(p => TextMatch.ContainsUnhedged(transcript.Turns[i].Reply, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => $"turn {i + 1} says \"{p}\""));
        }

        checks.Add(new CheckResult(CheckDimension.ForbiddenPhrases, "no forbidden phrase", hits.Count == 0,
            hits.Count == 0 ? null : string.Join("; ", hits)));
    }

    /// <param name="scope">The turns the expectations apply to: all of them, or one.</param>
    /// <param name="state">The turn whose database state is compared: the last one of the scope.</param>
    private static void CheckScope(
        Expectations expect, IReadOnlyList<TurnResult> scope, TurnResult state, string prefix, List<CheckResult> checks)
    {
        var calls = scope.SelectMany(t => t.ToolCalls).ToList();
        var reply = string.Join("\n", scope.Select(t => t.Reply));

        CheckToolCalls(expect, calls, prefix, checks);
        CheckRetrievalSources(expect, scope, prefix, checks);

        foreach (var mention in expect.MustMention)
            checks.Add(CheckMention(mention, reply, calls, prefix));

        CheckBooking(expect, state, prefix, checks);

        if (expect.Handoff is { } handoff)
        {
            var found = state.Handoffs > 0;
            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}{(handoff ? "handoff requested" : "no handoff requested")}", found == handoff,
                found == handoff ? null : $"the session has {state.Handoffs} handoff request(s)"));
        }
    }

    private static void CheckToolCalls(Expectations expect, List<ToolCallRecord> calls, string prefix, List<CheckResult> checks)
    {
        foreach (var expected in expect.ToolCalls)
        {
            var matching = calls.Count(c => Matches(c, expected));
            var passed = expected.Count is { } exactly ? matching == exactly : matching > 0;
            var times = expected.Count is { } n ? $"exactly {n} time(s)" : "at least once";
            var args = expected.Args is { } a ? $" with {JsonText.Show(a)}" : "";

            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}calls {expected.Name}{args}", passed,
                passed ? null : $"expected {times}, matched {matching}. Calls: {Describe(calls, expected.Name)}"));
        }

        if (expect.AnyToolCall.Count > 0)
        {
            var called = expect.AnyToolCall.Where(name => calls.Any(c => c.Name == name)).ToList();
            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}calls {string.Join(" or ", expect.AnyToolCall)}", called.Count > 0,
                called.Count > 0 ? null : $"none was called. Calls: {(calls.Count == 0 ? "none" : string.Join(", ", calls.Select(c => c.Name)))}"));
        }

        if (expect.ToolsInOrder)
        {
            var position = 0;
            var inOrder = expect.ToolCalls.All(expected =>
            {
                var next = calls.FindIndex(position, c => Matches(c, expected));
                position = next + 1;
                return next >= 0;
            });

            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}tools called in order {string.Join(" > ", expect.ToolCalls.Select(c => c.Name))}", inOrder,
                inOrder ? null : $"calls were: {(calls.Count == 0 ? "none" : string.Join(" > ", calls.Select(c => c.Name)))}"));
        }

        foreach (var name in expect.ForbidToolCalls)
        {
            var count = calls.Count(c => c.Name == name);
            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}does not call {name}", count == 0,
                count == 0 ? null : $"called {count} time(s): {Describe(calls, name)}"));
        }
    }

    /// <summary>Each expected document must be among the relevant passages of some search of the scope: the answer can only come from what the model was given.</summary>
    private static void CheckRetrievalSources(Expectations expect, IReadOnlyList<TurnResult> scope, string prefix, List<CheckResult> checks)
    {
        var searches = scope.SelectMany(t => t.Retrievals).ToList();
        foreach (var slug in expect.RetrievalSources)
        {
            var found = searches.Any(r => r.Passages.Any(p => p.Relevant && p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)));
            var returned = searches.SelectMany(r => r.Passages).Where(p => p.Relevant).Select(p => p.SourceId).Distinct().ToList();
            checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}retrieval returned {slug}", found,
                found ? null : searches.Count == 0
                    ? "search_knowledge_base was not called"
                    : $"relevant passages returned: {(returned.Count == 0 ? "none" : string.Join(", ", returned))}"));
        }
    }

    private static bool Matches(ToolCallRecord call, ToolCallExpectation expected) =>
        call.Name == expected.Name
        && (expected.Args is not { } args || (call.Arguments is { } sent && JsonMatch.IsSubset(args, sent)));

    /// <summary>The calls of a tool with their arguments, or the names of all calls when the tool was never called.</summary>
    private static string Describe(List<ToolCallRecord> calls, string name)
    {
        var same = calls.Where(c => c.Name == name).ToList();
        if (same.Count > 0)
            return string.Join(", ", same.Select(c => $"{c.Name}({Shorten(c.Arguments is { } sent ? JsonText.Show(sent) : "no arguments", 160)})"));
        return calls.Count == 0 ? "none" : string.Join(", ", calls.Select(c => c.Name));
    }

    private static CheckResult CheckMention(MustMention mention, string reply, List<ToolCallRecord> calls, string prefix)
    {
        switch (mention)
        {
            case MentionText text:
                var name = $"{prefix}mentions {string.Join(" | ", text.AnyOf.Select(t => $"\"{t}\""))}";
                return text.AnyOf.Any(t => TextMatch.Mentions(reply, t))
                    ? new CheckResult(CheckDimension.Content, name, true)
                    : new CheckResult(CheckDimension.Content, name, false, $"not in the reply: \"{Shorten(reply)}\"");

            case MentionFromTool fromTool:
                var label = $"{prefix}mentions a value of {fromTool.Tool} {fromTool.Path}";
                var results = calls.Where(c => c.Name == fromTool.Tool && c.Result is not null).ToList();
                if (results.Count == 0)
                    return new CheckResult(CheckDimension.Content, label, false, $"{fromTool.Tool} was not called, so there is no value to mention");

                var values = results
                    .SelectMany(c => JsonPath.Select(c.Result!.Value, fromTool.Path))
                    .Where(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    .Select(ValueText)
                    .Where(v => v.Length > 0)
                    .Distinct()
                    .ToList();
                if (values.Count == 0)
                    return new CheckResult(CheckDimension.Content, label, false, $"the path found no value in the {fromTool.Tool} result(s)");

                return values.Any(v => TextMatch.Mentions(reply, v))
                    ? new CheckResult(CheckDimension.Content, label, true)
                    : new CheckResult(CheckDimension.Content, label, false,
                        $"none of [{string.Join(", ", values.Take(8))}] is in the reply: \"{Shorten(reply)}\"");

            default:
                throw new InvalidOperationException($"Unknown mention type {mention.GetType().Name}.");
        }
    }

    /// <summary>A value as text to look for in a reply. Prices come as <c>550.000000</c> from the database: shown as 550.</summary>
    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number.ToString("0.####", CultureInfo.InvariantCulture),
        _ => value.GetRawText(),
    };

    private static void CheckBooking(Expectations expect, TurnResult state, string prefix, List<CheckResult> checks)
    {
        var bookings = state.Bookings;
        var summary = bookings.Count == 0 ? "no booking" : string.Join(", ", bookings.Select(b => b.Status.ToString()));

        switch (expect.Booking)
        {
            case true:
                var registered = bookings.Count == 1 && bookings[0].Status == BookingStatus.PendingConfirmation;
                checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}one booking, pending confirmation", registered,
                    registered ? null : $"the session has: {summary}"));

                // The total of an order is computed by the server from catalog prices: the stored one must be that sum.
                foreach (var order in bookings.Where(b => b.ServerTotal is not null))
                {
                    checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}order total equals the server calculation", order.Total == order.ServerTotal,
                        order.Total == order.ServerTotal ? null : $"stored {order.Total}, catalog prices give {order.ServerTotal}"));
                }

                if (expect.BookingTotal is { } total)
                {
                    var matches = bookings.Count == 1 && bookings[0].Total == total;
                    checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}booking total is {total}", matches,
                        matches ? null : $"the booking total is {(bookings.Count == 1 ? bookings[0].Total?.ToString() ?? "empty" : summary)}"));
                }

                break;

            case false:
                checks.Add(new CheckResult(CheckDimension.ToolUse, $"{prefix}no booking created", bookings.Count == 0,
                    bookings.Count == 0 ? null : $"the session has: {summary}"));
                break;
        }
    }

    public static string Shorten(string text, int max = 200)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        if (line.Length <= max)
            return line;

        // Never cut an emoji (a surrogate pair) in half: a lone surrogate cannot be written as UTF-8 and the report would fail.
        var cut = char.IsHighSurrogate(line[max - 1]) ? max - 1 : max;
        return line[..cut] + "…";
    }
}
