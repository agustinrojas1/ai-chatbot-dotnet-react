using Chatbot.Evals.Reporting;

namespace Chatbot.Evals.Retrieval;

/// <summary>Recall and MRR of a group of answerable questions.</summary>
internal sealed record RetrievalMetrics(string Name, int Questions, int Hits, double ReciprocalRankSum)
{
    /// <summary>Share of the questions whose expected document has a chunk in the top 4.</summary>
    public double RecallAt4 => Questions == 0 ? 0 : (double)Hits / Questions;

    /// <summary>Mean of 1 / (rank of the expected document among the documents of the top 4), 0 when it is missing.</summary>
    public double Mrr => Questions == 0 ? 0 : ReciprocalRankSum / Questions;

    public static RetrievalMetrics Of(string name, IReadOnlyList<RetrievalResult> results) =>
        new(name, results.Count, results.Count(r => r.Hit), results.Sum(r => r.Rank is { } rank ? 1.0 / rank : 0));
}

/// <summary>The target of one metric (M8, SPEC 12): at least <paramref name="Required"/>.</summary>
internal sealed record RetrievalTarget(string Name, double Required, double Actual)
{
    public bool Met => Actual >= Required;
}

/// <summary>
/// The numbers of a retrieval run. Questions whose search failed (embeddings provider down) are left out of every metric
/// and listed apart, as the conversation cases the provider refused are. The threshold figures are informational: the
/// threshold decides what the model gets, it is tuned with these numbers but not a target.
/// </summary>
internal sealed class RetrievalSummary
{
    public const double RecallTarget = 0.9;
    public const double MrrTarget = 0.75;

    public RetrievalSummary(IReadOnlyList<RetrievalResult> results)
    {
        Results = results;
        NotRun = [.. results.Where(r => r.NotRun)];
        var answerable = results.Where(r => !r.NotRun && !r.Case.Unanswerable).ToList();
        var unanswerable = results.Where(r => !r.NotRun && r.Case.Unanswerable).ToList();

        Overall = RetrievalMetrics.Of("overall", answerable);
        ByPair = [.. new[] { "es->es", "en->en", "es->en", "en->es" }
            .Select(pair => RetrievalMetrics.Of(pair, [.. answerable.Where(r => r.Pair == pair)]))
            .Where(m => m.Questions > 0)];
        Misses = [.. answerable.Where(r => !r.Hit)];

        Unanswerable = unanswerable;
        UnanswerableBelowThreshold = unanswerable.Count(r => !r.ThresholdPassed);
        AnswerableAboveThreshold = answerable.Count(r => r.ThresholdPassed);
        AnswerableCount = answerable.Count;

        Targets =
        [
            new("recall@4", RecallTarget, Overall.RecallAt4),
            new("MRR", MrrTarget, Overall.Mrr),
        ];
    }

    public IReadOnlyList<RetrievalResult> Results { get; }
    public IReadOnlyList<RetrievalResult> NotRun { get; }
    public RetrievalMetrics Overall { get; }
    public IReadOnlyList<RetrievalMetrics> ByPair { get; }
    public IReadOnlyList<RetrievalResult> Misses { get; }
    public IReadOnlyList<RetrievalResult> Unanswerable { get; }

    /// <summary>Unanswerable questions for which no passage passed the threshold (<c>thresholdPassed = false</c>): the search said "nothing relevant".</summary>
    public int UnanswerableBelowThreshold { get; }

    /// <summary>Answerable questions for which at least one passage passed the threshold: the agent was given something to answer with.</summary>
    public int AnswerableAboveThreshold { get; }

    public int AnswerableCount { get; }
    public IReadOnlyList<RetrievalTarget> Targets { get; }

    /// <summary>With no answerable question judged (all not run) there is nothing to measure.</summary>
    public bool TargetsMet => AnswerableCount == 0 || Targets.All(t => t.Met);

    public long EmbeddingTokens => Results.Sum(r => r.EmbeddingTokens);
}
