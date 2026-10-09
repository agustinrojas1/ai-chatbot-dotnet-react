using System.Globalization;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;

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
