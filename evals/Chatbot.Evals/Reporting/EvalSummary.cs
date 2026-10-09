using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;

namespace Chatbot.Evals.Reporting;

/// <summary>How a run was started, for the header of the report.</summary>
internal sealed record RunInfo(
    DateTimeOffset StartedAt, TimeSpan Duration, bool Fake, int Repeat, string? Filter, string? Category, string? Niche);

internal sealed record CategoryStats(EvalCategory Category, int Runs, int Passed)
{
    public double Rate => Runs == 0 ? 0 : (double)Passed / Runs;
}

/// <summary>
/// One MVP target (SPEC 12): the share of the runs that have checks of a dimension and pass all of them.
/// A dimension with no checks at all (a filtered run) has nothing to measure and is met.
/// </summary>
internal sealed record TargetResult(string Name, double Required, int Runs, int Passed)
{
    public double Actual => Runs == 0 ? 1 : (double)Passed / Runs;
    public bool Met => Actual >= Required;
}

/// <summary>The numbers of a run: pass rate by category and overall, usage, and the MVP targets.</summary>
internal sealed class EvalSummary
{
    private EvalSummary(IReadOnlyList<CaseResult> results)
    {
        Results = results;
        Categories = results
            .GroupBy(r => r.Transcript.Case.Category)
            .OrderBy(g => g.Key)
            .Select(g => new CategoryStats(g.Key, g.Count(), g.Count(r => r.Passed)))
            .ToList();

        TokensIn = results.Sum(r => r.TokensIn);
        TokensOut = results.Sum(r => r.TokensOut);
        CostUsd = results.Sum(r => r.CostUsd);

        var turns = results.SelectMany(r => r.Transcript.Turns).ToList();
        Models = turns.Select(t => t.Model).Where(m => m is not null).Select(m => m!).Distinct().Order().ToList();
        Providers = turns.Select(t => t.Provider).Where(p => p is not null).Select(p => p!).Distinct().Order().ToList();

        Targets =
        [
            Target("Language (every reply in the user's language)", 1.0, CheckDimension.Language),
            Target("Forbidden phrases (none said)", 1.0, CheckDimension.ForbiddenPhrases),
            Target("Correct tool use", 0.9, CheckDimension.ToolUse),
        ];
    }

    public static EvalSummary Create(IReadOnlyList<CaseResult> results) => new(results);

    public IReadOnlyList<CaseResult> Results { get; }
    public IReadOnlyList<CategoryStats> Categories { get; }
    public IReadOnlyList<TargetResult> Targets { get; }
    public IReadOnlyList<string> Models { get; }
    public IReadOnlyList<string> Providers { get; }
    public long TokensIn { get; }
    public long TokensOut { get; }
    public decimal CostUsd { get; }

    public int Runs => Results.Count;
    public int Passed => Results.Count(r => r.Passed);
    public double PassRate => Runs == 0 ? 0 : (double)Passed / Runs;
    public bool TargetsMet => Targets.All(t => t.Met);

    /// <summary>The case id, with the run number when a case ran more than once (<c>--repeat</c>).</summary>
    public string RunName(CaseResult result) =>
        Results.Any(r => r.Transcript.Iteration > 1) ? $"{result.Transcript.Case.Id}#{result.Transcript.Iteration}" : result.Transcript.Case.Id;

    private TargetResult Target(string name, double required, CheckDimension dimension)
    {
        var withChecks = Results.Where(r => r.Checks.Any(c => c.Dimension == dimension)).ToList();
        var passed = withChecks.Count(r => r.Checks.Where(c => c.Dimension == dimension).All(c => c.Passed));
        return new TargetResult(name, required, withChecks.Count, passed);
    }
}
