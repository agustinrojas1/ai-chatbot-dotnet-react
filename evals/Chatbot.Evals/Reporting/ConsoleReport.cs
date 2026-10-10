using System.Globalization;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Retrieval;

namespace Chatbot.Evals.Reporting;

/// <summary>The table printed at the end of a run.</summary>
internal static class ConsoleReport
{
    private const int MaxReasonChars = 90;

    public static void Write(TextWriter output, EvalSummary summary)
    {
        var rows = summary.Results.Select(r => new[]
        {
            summary.RunName(r),
            CaseLoader.Name(r.Transcript.Case.Category),
            r.NotRun ? "NOT RUN" : r.Passed ? "PASS" : "FAIL",
            Reasons(r),
        }).ToList();

        WriteTable(output, ["Case", "Category", "Result", "Reasons"], rows);

        output.WriteLine();
        output.WriteLine("Pass rate by category");
        WriteTable(output, ["Category", "Passed", "Rate"],
        [
            .. summary.Categories.Select(c => new[] { CaseLoader.Name(c.Category), $"{c.Passed}/{c.Runs}", Percent(c.Rate) }),
            ["OVERALL", $"{summary.Passed}/{summary.Runs}", Percent(summary.PassRate)],
        ]);

        if (summary.NotRun.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"NOT RUN: {summary.NotRun.Count} of {summary.Results.Count} run(s) were refused by the provider (quota) and are left out of every rate above. Run them again later, or with a higher --min-interval-ms.");
        }

        output.WriteLine();
        output.WriteLine($"Tokens: {summary.TokensIn:N0} in, {summary.TokensOut:N0} out. Cost: ${summary.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}");
        output.WriteLine($"Model: {Join(summary.Models)}. Provider: {Join(summary.Providers)}.");

        output.WriteLine();
        output.WriteLine("MVP targets");
        WriteTable(output, ["Target", "Required", "Actual", "Result"],
            summary.Targets.Select(t => new[]
            {
                t.Name,
                $">= {Percent(t.Required)}",
                $"{Percent(t.Actual)} ({t.Passed}/{t.Runs})",
                t.Met ? "MET" : "NOT MET",
            }).ToList());
    }

    /// <summary>The table of a retrieval run: misses, recall and MRR overall and by language pair, the threshold figures and the targets.</summary>
    public static void WriteRetrieval(TextWriter output, RetrievalSummary summary)
    {
        output.WriteLine($"Questions: {summary.Results.Count} ({summary.AnswerableCount} answerable, {summary.Unanswerable.Count} unanswerable, {summary.NotRun.Count} not run)");

        if (summary.Misses.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Misses (expected document not in the top 4)");
            WriteTable(output, ["Question", "Niche", "Expected", "Top 4 documents"],
                summary.Misses.Select(r => new[]
                {
                    r.Case.Id, CaseLoader.Name(r.Case.Niche), string.Join(", ", r.Case.ExpectSources), string.Join(", ", r.DocumentRanking),
                }).ToList());
        }

        output.WriteLine();
        output.WriteLine("Recall@4 and MRR");
        WriteTable(output, ["Group", "Questions", "Recall@4", "MRR"],
            [.. summary.ByPair.Append(summary.Overall).Select(m => new[] { m.Name, m.Questions.ToString(CultureInfo.InvariantCulture), Fixed(m.RecallAt4), Fixed(m.Mrr) })]);

        output.WriteLine();
        output.WriteLine("Relevance threshold (informational)");
        output.WriteLine($"  unanswerable: thresholdPassed=false for {summary.UnanswerableBelowThreshold}/{summary.Unanswerable.Count} (the search said 'nothing relevant')");
        output.WriteLine($"  answerable:   thresholdPassed=true  for {summary.AnswerableAboveThreshold}/{summary.AnswerableCount}");
        output.WriteLine($"  query embedding tokens: {summary.EmbeddingTokens:N0}");

        if (summary.NotRun.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"NOT RUN: {summary.NotRun.Count} question(s) could not be searched (embeddings provider) and are left out of every metric: {summary.NotRun[0].Error}");
        }

        output.WriteLine();
        output.WriteLine("Retrieval targets");
        WriteTable(output, ["Target", "Required", "Actual", "Result"],
            [.. summary.Targets.Select(t => new[] { t.Name, $">= {Fixed(t.Required)}", Fixed(t.Actual), t.Met ? "MET" : "NOT MET" })]);
    }

    public static string Fixed(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Reasons(CaseResult result)
    {
        var failures = result.Failures.ToList();
        if (failures.Count == 0)
            return "";

        var first = Evaluator.Shorten(failures[0].ToString(), MaxReasonChars);
        return failures.Count == 1 ? first : $"{first} (+{failures.Count - 1} more)";
    }

    private static string Percent(double rate) => (rate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "unknown" : string.Join(", ", values);

    private static void WriteTable(TextWriter output, string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

        output.WriteLine(Line(header));
        output.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            output.WriteLine(Line(row));
    }
}
