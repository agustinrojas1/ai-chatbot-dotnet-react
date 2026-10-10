using System.Globalization;
using System.Text;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Retrieval;
using Chatbot.Evals.Running;

namespace Chatbot.Evals.Reporting;

/// <summary>The report saved to <c>evals/results/&lt;yyyy-MM-dd-HHmm&gt;.md</c>.</summary>
internal static class MarkdownReport
{
    private const int MaxPayloadChars = 300;
    private const int MaxReasonsInTable = 2;

    public static string FileName(DateTimeOffset startedAt) => $"{startedAt.ToLocalTime():yyyy-MM-dd-HHmm}.md";

    public static string Render(EvalSummary summary, RunInfo info)
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        md.AppendLine($"# Eval run {info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"- **Model:** {Join(summary.Models)}{(info.Fake ? " (scripted fake, not a real model: the numbers say nothing about quality)" : "")}");
        md.AppendLine($"- **Provider:** {Join(summary.Providers)}");
        md.AppendLine($"- **Runs:** {summary.Results.Count} ({summary.Results.Select(r => r.Transcript.Case.Id).Distinct().Count()} cases, repeat {info.Repeat}): {summary.Runs} judged, {summary.NotRun.Count} not run");
        md.AppendLine($"- **Selection:** {Selection(info)}");
        md.AppendLine($"- **Duration:** {info.Duration.TotalSeconds.ToString("0.#", inv)} s");
        md.AppendLine();

        if (summary.NotRun.Count > 0)
        {
            md.AppendLine($"> **Incomplete run.** {summary.NotRun.Count} run(s) were refused by the provider (quota) after every retry. They are left out of every rate below and listed under \"Not run\"; they say nothing about the agent.");
            md.AppendLine();
        }

        md.AppendLine("## MVP targets");
        md.AppendLine();
        md.AppendLine("| Target | Required | Actual | Result |");
        md.AppendLine("|---|---|---|---|");
        foreach (var target in summary.Targets)
            md.AppendLine($"| {target.Name} | >= {Percent(target.Required)} | {Percent(target.Actual)} ({target.Passed}/{target.Runs}) | {(target.Met ? "MET" : "**NOT MET**")} |");
        md.AppendLine();

        md.AppendLine("## Pass rate by category");
        md.AppendLine();
        md.AppendLine("| Category | Passed | Rate |");
        md.AppendLine("|---|---|---|");
        foreach (var category in summary.Categories)
            md.AppendLine($"| {CaseLoader.Name(category.Category)} | {category.Passed}/{category.Runs} | {Percent(category.Rate)} |");
        md.AppendLine($"| **Overall** | **{summary.Passed}/{summary.Runs}** | **{Percent(summary.PassRate)}** |");
        md.AppendLine();

        md.AppendLine("## Usage");
        md.AppendLine();
        md.AppendLine($"- **Tokens:** {summary.TokensIn:N0} in, {summary.TokensOut:N0} out");
        md.AppendLine($"- **Cost:** ${summary.CostUsd.ToString("0.0000", inv)} (tokens x the `ModelPricing` row of the model{(summary.CostUsd == 0 && summary.TokensIn > 0 ? "; it is 0 because that table has no row for this model" : "")})");
        md.AppendLine();

        md.AppendLine("## Results");
        md.AppendLine();
        md.AppendLine("| Case | Category | Niche | Result | Reasons |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var result in summary.Results)
        {
            var evalCase = result.Transcript.Case;
            var failed = result.Failures.ToList();
            var reasons = string.Join("<br>", failed.Take(MaxReasonsInTable).Select(f => Cell(Evaluator.Shorten(f.ToString(), 220))));
            if (failed.Count > MaxReasonsInTable)
                reasons += $"<br>(+{failed.Count - MaxReasonsInTable} more, see Failures)";
            md.AppendLine($"| {summary.RunName(result)} | {CaseLoader.Name(evalCase.Category)} | {CaseLoader.Name(evalCase.Niche)} | {(result.NotRun ? "NOT RUN" : result.Passed ? "PASS" : "**FAIL**")} | {reasons} |");
        }

        var failures = summary.Failed;
        md.AppendLine();
        md.AppendLine($"## Failures ({failures.Count})");
        foreach (var result in failures)
            AppendFailure(md, summary, result);

        md.AppendLine();
        md.AppendLine($"## Not run ({summary.NotRun.Count})");
        if (summary.NotRun.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Excluded from the pass rates: the provider refused the call (HTTP 429) after every retry.");
            md.AppendLine();
            foreach (var result in summary.NotRun)
                md.AppendLine($"- {summary.RunName(result)}: {result.Transcript.NotRunReason}, {Cell(result.Checks[0].Detail ?? "")}");
        }

        return md.ToString();
    }

    /// <summary>The report of a retrieval run (<c>--retrieval</c>): same header as the others, then the retrieval section.</summary>
    public static string RenderRetrieval(RetrievalSummary summary, RunInfo info, string embeddingModel)
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        md.AppendLine($"# Retrieval eval {info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"- **Embedding model:** {embeddingModel}{(info.Fake ? " (deterministic fake embedder, not a real model: the numbers say nothing about quality)" : "")}");
        md.AppendLine($"- **Questions:** {summary.Results.Count}: {summary.AnswerableCount} answerable, {summary.Unanswerable.Count} unanswerable, {summary.NotRun.Count} not run");
        md.AppendLine($"- **Duration:** {info.Duration.TotalSeconds.ToString("0.#", inv)} s");
        md.AppendLine($"- **Query embedding tokens:** {summary.EmbeddingTokens:N0}");
        md.AppendLine();

        if (summary.NotRun.Count > 0)
        {
            md.AppendLine($"> **Incomplete run.** {summary.NotRun.Count} question(s) could not be searched (embeddings provider) and are left out of every metric: {Cell(summary.NotRun[0].Error ?? "")}");
            md.AppendLine();
        }

        md.AppendLine("## Retrieval targets");
        md.AppendLine();
        md.AppendLine("| Target | Required | Actual | Result |");
        md.AppendLine("|---|---|---|---|");
        foreach (var target in summary.Targets)
            md.AppendLine($"| {target.Name} | >= {ConsoleReport.Fixed(target.Required)} | {ConsoleReport.Fixed(target.Actual)} | {(target.Met ? "MET" : "**NOT MET**")} |");
        md.AppendLine();

        md.AppendLine("## Recall@4 and MRR by language pair");
        md.AppendLine();
        md.AppendLine("The pair is the question language, then the language of the expected document: es->en is a Spanish question over an English document.");
        md.AppendLine();
        md.AppendLine("| Pair | Questions | Recall@4 | MRR |");
        md.AppendLine("|---|---|---|---|");
        foreach (var metrics in summary.ByPair)
            md.AppendLine($"| {metrics.Name} | {metrics.Questions} | {ConsoleReport.Fixed(metrics.RecallAt4)} | {ConsoleReport.Fixed(metrics.Mrr)} |");
        md.AppendLine($"| **Overall** | **{summary.Overall.Questions}** | **{ConsoleReport.Fixed(summary.Overall.RecallAt4)}** | **{ConsoleReport.Fixed(summary.Overall.Mrr)}** |");
        md.AppendLine();

        md.AppendLine("## Relevance threshold (informational)");
        md.AppendLine();
        md.AppendLine($"- Unanswerable questions with `thresholdPassed=false` (the search says \"nothing relevant\"): **{summary.UnanswerableBelowThreshold}/{summary.Unanswerable.Count}**");
        md.AppendLine($"- Answerable questions with `thresholdPassed=true` (the agent is given passages): **{summary.AnswerableAboveThreshold}/{summary.AnswerableCount}**");
        foreach (var leak in summary.Unanswerable.Where(r => r.ThresholdPassed))
            md.AppendLine($"  - passed the threshold without a document: {leak.Case.Id}: {Cell(leak.Case.Question)} (top: {string.Join(", ", leak.DocumentRanking)})");
        md.AppendLine();

        md.AppendLine($"## Misses ({summary.Misses.Count})");
        if (summary.Misses.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("| Question | Niche | Pair | Expected | Top 4 documents (best chunk first) |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var miss in summary.Misses)
                md.AppendLine($"| {miss.Case.Id}: {Cell(miss.Case.Question)} | {CaseLoader.Name(miss.Case.Niche)} | {miss.Pair} | {string.Join(", ", miss.Case.ExpectSources)} | {string.Join(", ", miss.Passages.Select(p => p.SourceId))} |");
        }

        md.AppendLine();
        md.AppendLine("## Questions");
        md.AppendLine();
        md.AppendLine("| Question | Pair | Rank of the expected document | Passed the threshold |");
        md.AppendLine("|---|---|---|---|");
        foreach (var result in summary.Results)
        {
            var rank = result.NotRun ? "not run" : result.Case.Unanswerable ? "unanswerable" : result.Rank is { } r ? r.ToString(inv) : "missing";
            md.AppendLine($"| {result.Case.Id} | {result.Pair ?? "-"} | {rank} | {(result.NotRun ? "-" : result.ThresholdPassed ? "yes" : "no")} |");
        }

        return md.ToString();
    }

    private static void AppendFailure(StringBuilder md, EvalSummary summary, CaseResult result)
    {
        var evalCase = result.Transcript.Case;
        md.AppendLine();
        md.AppendLine($"### {summary.RunName(result)}");
        md.AppendLine();
        md.AppendLine($"{CaseLoader.Name(evalCase.Category)} / {CaseLoader.Name(evalCase.Niche)} / `{evalCase.Source}`");
        md.AppendLine();
        md.AppendLine("Failed checks:");
        md.AppendLine();
        foreach (var failure in result.Failures)
            md.AppendLine($"- [{failure.Dimension}] {Evaluator.Shorten(failure.ToString(), 600)}");

        for (var i = 0; i < result.Transcript.Turns.Count; i++)
        {
            var turn = result.Transcript.Turns[i];
            md.AppendLine();
            md.AppendLine($"**Turn {i + 1}**");
            md.AppendLine();
            md.AppendLine($"- User: {Cell(turn.User)}");
            md.AppendLine($"- Assistant: {(turn.Reply.Length == 0 ? "(no reply)" : Cell(turn.Reply))}");
            if (turn.Error is not null)
                md.AppendLine($"- Error: {Cell(turn.Error)}");
            foreach (var call in turn.ToolCalls)
                md.AppendLine($"- Tool call: `{call.Name}({Payload(call.Arguments)})` returned `{Payload(call.Result)}`");
            md.AppendLine($"- Tokens {turn.TokensIn}/{turn.TokensOut}, bookings: {turn.Bookings.Count}, handoffs: {turn.Handoffs}");
        }
    }

    private static string Selection(RunInfo info)
    {
        var parts = new List<string>();
        if (info.Filter is not null)
            parts.Add($"filter `{info.Filter}`");
        if (info.Category is not null)
            parts.Add($"category `{info.Category}`");
        if (info.Niche is not null)
            parts.Add($"niche `{info.Niche}`");
        return parts.Count == 0 ? "all cases" : string.Join(", ", parts);
    }

    private static string Payload(System.Text.Json.JsonElement? payload) =>
        payload is { } value ? Evaluator.Shorten(JsonText.Show(value), MaxPayloadChars).Replace("`", "'") : "null";

    /// <summary>Text on one line that cannot break a table cell or a list item.</summary>
    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|");

    private static string Percent(double rate) => (rate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "unknown" : string.Join(", ", values);
}
