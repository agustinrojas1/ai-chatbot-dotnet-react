using Chatbot.Evals;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Reporting;
using Chatbot.Evals.Running;
using static Chatbot.Tests.Evals.EvalTestSupport;

namespace Chatbot.Tests.Evals;

public class ReportTests
{
    private static EvalCase NewCase(string id, string category, string extra)
    {
        var yaml = $"- id: {id}\n  category: {category}\n  niche: dental\n  turns: [\"hello\"]\n" + string.Concat(extra.Split('\n').Select(l => $"  {l}\n"));
        return CaseLoader.Parse("test.yaml", yaml).Single();
    }

    private static CaseResult Result(EvalCase evalCase, TurnResult turn, int iteration = 1) =>
        Evaluator.Evaluate(new Transcript(evalCase, iteration, [turn with { User = "hello" }], []));

    /// <summary>Three runs: a passing search, a search that lacks its tool call, and a language case answered in the wrong language.</summary>
    private static EvalSummary Sample()
    {
        var search = NewCase("search-ok", "search", "expectToolCalls: [{ name: search_catalog }]");
        var missingTool = NewCase("search-no-tool", "search", "expectToolCalls: [{ name: search_catalog }]");
        var language = NewCase("lang-es", "language", "expectLanguage: es");

        return EvalSummary.Create(
        [
            Result(search, Turn("Hola, cuesta $80.", Call("search_catalog")) with { CostUsd = 0.0005m }),
            Result(missingTool, Turn("Cuesta $80 | dicen") with { CostUsd = 0.0005m }),
            Result(language, Turn("Hello, the cleaning costs $80. Would you like to book?") with { CostUsd = 0.001m }),
        ]);
    }

    [Fact]
    public void Aggregates_pass_rate_by_category_and_overall()
    {
        var summary = Sample();

        Assert.Equal(
            [(EvalCategory.Language, 1, 0), (EvalCategory.Search, 2, 1)],
            summary.Categories.Select(c => (c.Category, c.Runs, c.Passed)));
        Assert.Equal(0.5, summary.Categories.Single(c => c.Category == EvalCategory.Search).Rate);
        Assert.Equal((3, 1), (summary.Runs, summary.Passed));
        Assert.Equal(1.0 / 3, summary.PassRate, 6);
    }

    [Fact]
    public void Adds_up_tokens_cost_models_and_providers()
    {
        var summary = Sample();

        Assert.Equal((300, 60), (summary.TokensIn, summary.TokensOut));
        Assert.Equal(0.002m, summary.CostUsd);
        Assert.Equal(["test-model"], summary.Models);
        Assert.Equal(["test"], summary.Providers);
    }

    [Fact]
    public void Measures_the_mvp_targets_per_dimension()
    {
        var targets = Sample().Targets.ToDictionary(t => t.Name.Split(' ')[0]);

        // One run has language checks and fails them; two runs have tool checks and one fails; all three have phrase checks and pass.
        Assert.Equal((1, 0, false, 1.0), (targets["Language"].Runs, targets["Language"].Passed, targets["Language"].Met, targets["Language"].Required));
        Assert.Equal((3, 3, true), (targets["Forbidden"].Runs, targets["Forbidden"].Passed, targets["Forbidden"].Met));
        Assert.Equal((2, 1, false, 0.9), (targets["Correct"].Runs, targets["Correct"].Passed, targets["Correct"].Met, targets["Correct"].Required));
        Assert.False(Sample().TargetsMet);
    }

    [Fact]
    public void Targets_are_met_when_nothing_fails_and_when_nothing_is_measured()
    {
        var search = NewCase("search-ok", "search", "expectToolCalls: [{ name: search_catalog }]");
        var passing = EvalSummary.Create([Result(search, Turn("It costs $80.", Call("search_catalog")))]);

        Assert.True(passing.TargetsMet);
        // No case has language checks: the language target has no runs and counts as met.
        var language = passing.Targets.Single(t => t.Name.StartsWith("Language"));
        Assert.Equal(0, language.Runs);
        Assert.True(language.Met);
    }

    [Fact]
    public void A_ninety_percent_tool_target_tolerates_one_failure_in_ten()
    {
        var search = NewCase("search", "search", "expectToolCalls: [{ name: search_catalog }]");
        var results = Enumerable.Range(0, 10)
            .Select(i => Result(search, i == 0 ? Turn("no tool") : Turn("ok", Call("search_catalog")), i + 1))
            .ToList();

        var tools = EvalSummary.Create(results).Targets.Single(t => t.Name.StartsWith("Correct"));

        Assert.Equal(0.9, tools.Actual);
        Assert.True(tools.Met);
    }

    [Fact]
    public void Console_table_lists_every_run_with_its_reasons_and_the_targets()
    {
        var writer = new StringWriter();

        ConsoleReport.Write(writer, Sample());
        var text = writer.ToString();

        Assert.Contains("Case", text);
        Assert.Matches(@"search-ok\s+search\s+PASS", text);
        Assert.Matches(@"search-no-tool\s+search\s+FAIL\s+calls search_catalog", text);
        Assert.Matches(@"lang-es\s+language\s+FAIL\s+turn 1: reply is in Spanish", text);
        Assert.Matches(@"OVERALL\s+1/3\s+33\.3%", text);
        Assert.Contains("Tokens: 300 in, 60 out. Cost: $0.0020", text);
        Assert.Matches(@"Language .*>= 100%\s+0% \(0/1\)\s+NOT MET", text);
        Assert.Matches(@"Correct tool use\s+>= 90%\s+50% \(1/2\)\s+NOT MET", text);
    }

    [Fact]
    public void Repeated_runs_get_a_run_number()
    {
        var search = NewCase("search-ok", "search", "expectToolCalls: [{ name: search_catalog }]");
        var summary = EvalSummary.Create(
        [
            Result(search, Turn("ok", Call("search_catalog")), 1),
            Result(search, Turn("ok"), 2),
        ]);
        var writer = new StringWriter();

        ConsoleReport.Write(writer, summary);

        Assert.Matches(@"search-ok#1\s+search\s+PASS", writer.ToString());
        Assert.Matches(@"search-ok#2\s+search\s+FAIL", writer.ToString());
    }

    [Fact]
    public void Markdown_report_has_header_targets_categories_usage_and_the_failures_with_transcripts()
    {
        var info = new RunInfo(new DateTimeOffset(2026, 10, 9, 16, 40, 0, TimeSpan.Zero), TimeSpan.FromSeconds(12.3), Fake: false, Repeat: 1, null, null, null);

        var markdown = MarkdownReport.Render(Sample(), info);

        Assert.Contains("- **Model:** test-model", markdown);
        Assert.Contains("- **Provider:** test", markdown);
        Assert.Contains("- **Runs:** 3 (3 cases, repeat 1)", markdown);
        Assert.Contains("- **Selection:** all cases", markdown);
        Assert.Contains("| Correct tool use | >= 90% | 50% (1/2) | **NOT MET** |", markdown);
        Assert.Contains("| search | 1/2 | 50% |", markdown);
        Assert.Contains("| language | 0/1 | 0% |", markdown);
        Assert.Contains("| **Overall** | **1/3** | **33.3%** |", markdown);
        Assert.Contains("- **Tokens:** 300 in, 60 out", markdown);
        Assert.Contains("- **Cost:** $0.0020", markdown);
        Assert.Contains("| search-ok | search | dental | PASS |", markdown);
        Assert.Contains("## Failures (2)", markdown);
        Assert.Contains("### search-no-tool", markdown);
        Assert.Contains("- [ToolUse] calls search_catalog: expected at least once, matched 0.", markdown);
        Assert.Contains("- User: hello", markdown);
        Assert.Contains("- Assistant: Cuesta $80 \\| dicen", markdown); // a pipe cannot break the markdown
        Assert.DoesNotContain("### search-ok", markdown);
    }

    [Fact]
    public void Markdown_report_shows_the_tool_calls_of_a_failed_run()
    {
        var evalCase = NewCase("order", "order", "forbidToolCalls: [create_booking]");
        var result = Result(evalCase, Turn("Done", Call("create_booking", """{"customerName":"Luca"}""", """{"status":"PendingConfirmation"}""")));
        var info = new RunInfo(DateTimeOffset.UtcNow, TimeSpan.Zero, Fake: true, Repeat: 1, "ord", "order", "dental");

        var markdown = MarkdownReport.Render(EvalSummary.Create([result]), info);

        Assert.Contains("""- Tool call: `create_booking({"customerName":"Luca"})` returned `{"status":"PendingConfirmation"}`""", markdown);
        Assert.Contains("scripted fake", markdown);
        Assert.Contains("- **Selection:** filter `ord`, category `order`, niche `dental`", markdown);
    }

    [Fact]
    public void Cost_note_explains_a_zero_cost_with_tokens()
    {
        var evalCase = NewCase("a", "search", "forbidBooking: true");
        var info = new RunInfo(DateTimeOffset.UtcNow, TimeSpan.Zero, false, 1, null, null, null);

        var markdown = MarkdownReport.Render(EvalSummary.Create([Result(evalCase, Turn("ok"))]), info);

        Assert.Contains("no row for this model", markdown);
    }

    [Fact]
    public void Report_file_name_is_the_start_time_to_the_minute()
    {
        var name = MarkdownReport.FileName(new DateTimeOffset(2026, 10, 9, 16, 40, 59, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 9))));

        Assert.Equal("2026-10-09-1640.md", name);
    }
}

public class EvalOptionsTests
{
    [Fact]
    public void Parses_every_option()
    {
        var options = EvalOptions.Parse(["--filter", "lang", "--category", "not_in_catalog", "--niche", "dental", "--model", "m1", "--repeat", "3", "--fake", "--cases", "c", "--results", "r"]);

        Assert.Equal(("lang", EvalCategory.NotInCatalog, EvalNiche.Dental, "m1", 3, true), (options.Filter, options.Category, options.Niche, options.Model, options.Repeat, options.Fake));
        Assert.Equal(("c", "r"), (options.CasesDirectory, options.ResultsDirectory));
    }

    [Fact]
    public void Defaults_run_everything_once_against_a_real_model()
    {
        var options = EvalOptions.Parse([]);

        Assert.Equal((1, false), (options.Repeat, options.Fake));
        Assert.Null(options.Filter);
    }

    [Theory]
    [InlineData("--repeat", "0", "--repeat must be")]
    [InlineData("--repeat", "x", "--repeat must be")]
    [InlineData("--category", "cooking", "Unknown category 'cooking'")]
    [InlineData("--niche", "bakery", "Unknown niche 'bakery'")]
    [InlineData("--model", null, "--model needs a value")]
    [InlineData("--bogus", null, "Unknown option '--bogus'")]
    public void Rejects_bad_arguments_with_a_clear_message(string name, string? value, string expected)
    {
        string[] args = value is null ? [name] : [name, value];

        var ex = Assert.Throws<UsageException>(() => EvalOptions.Parse(args));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Filters_combine()
    {
        var cases = CaseLoader.Parse("t.yaml", """
            - { id: lang-a, category: language, niche: dental, turns: [hi], expectHandoff: false }
            - { id: lang-b, category: language, niche: restaurant, turns: [hi], expectHandoff: false }
            - { id: book-a, category: booking, niche: dental, turns: [hi], expectHandoff: false }
            """);

        Assert.Equal(["lang-a", "lang-b"], EvalOptions.Parse(["--filter", "LANG"]).Select(cases).Select(c => c.Id));
        Assert.Equal(["lang-a"], EvalOptions.Parse(["--filter", "lang", "--niche", "dental"]).Select(cases).Select(c => c.Id));
        Assert.Equal(["book-a"], EvalOptions.Parse(["--category", "booking"]).Select(cases).Select(c => c.Id));
        Assert.Empty(EvalOptions.Parse(["--filter", "nothing"]).Select(cases));
    }
}
