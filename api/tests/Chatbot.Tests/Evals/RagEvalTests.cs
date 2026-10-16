using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Reporting;
using Chatbot.Evals.Retrieval;
using Chatbot.Evals.Running;
using static Chatbot.Tests.Evals.EvalTestSupport;

namespace Chatbot.Tests.Evals;

public class RetrievalCaseLoaderTests
{
    [Fact]
    public void Reads_answerable_and_unanswerable_questions()
    {
        var cases = RetrievalCaseLoader.Parse("rag-retrieval.yaml", """
            - id: ret-a
              niche: dental
              language: es
              question: "¿Cuánto cuesta cancelar tarde?"
              expectSources: [cancellation-and-no-show, other-doc]
            - id: ret-b
              niche: restaurant
              language: en
              question: Do you offer valet parking?
              unanswerable: true
            """);

        Assert.Equal(["ret-a", "ret-b"], cases.Select(c => c.Id));
        Assert.Equal((EvalNiche.Dental, ReplyLanguage.Spanish, false), (cases[0].Niche, cases[0].Language, cases[0].Unanswerable));
        Assert.Equal(["cancellation-and-no-show", "other-doc"], cases[0].ExpectSources);
        Assert.True(cases[1].Unanswerable);
        Assert.Empty(cases[1].ExpectSources);
    }

    [Theory]
    [InlineData("- id: a\n  niche: dental\n  language: en\n  question: q", "give expectSources")]
    [InlineData("- id: a\n  niche: dental\n  language: en\n  question: q\n  unanswerable: true\n  expectSources: [x]", "cannot be unanswerable")]
    [InlineData("- id: a\n  niche: bakery\n  language: en\n  question: q\n  unanswerable: true", "niche 'bakery' is not valid")]
    [InlineData("- id: a\n  niche: dental\n  language: fr\n  question: q\n  unanswerable: true", "language 'fr' must be")]
    [InlineData("- id: a\n  niche: dental\n  language: en\n  unanswerable: true", "missing 'question'")]
    [InlineData("- id: A b\n  niche: dental\n  language: en\n  question: q\n  unanswerable: true", "lowercase letters")]
    [InlineData("- id: a\n  niche: dental\n  language: en\n  question: q\n  unanswerable: true\n  expected: [x]", "unknown key 'expected'")]
    [InlineData("- id: a\n  niche: dental\n  language: en\n  question: q\n  expectSources: x", "must be a list of document slugs")]
    [InlineData("just: a map", "non-empty list")]
    public void Reports_problems_with_file_and_line(string yaml, string expected)
    {
        var ex = Assert.Throws<CaseFormatException>(() => RetrievalCaseLoader.Parse("rag-retrieval.yaml", yaml));

        Assert.Contains(ex.Problems, p => p.Contains(expected) && p.StartsWith("rag-retrieval.yaml:"));
    }

    [Fact]
    public void Ids_must_be_unique()
    {
        var yaml = "- { id: a, niche: dental, language: en, question: q, unanswerable: true }\n- { id: a, niche: dental, language: en, question: q2, unanswerable: true }";

        var ex = Assert.Throws<CaseFormatException>(() => RetrievalCaseLoader.Parse("rag-retrieval.yaml", yaml));

        Assert.Contains("duplicate question id 'a'", Assert.Single(ex.Problems));
    }

    [Fact]
    public void The_shipped_questions_cover_the_m8_spec()
    {
        var questions = RetrievalCaseLoader.LoadDirectory(ShippedCasesDirectory());
        var answerable = questions.Where(q => !q.Unanswerable).ToList();

        Assert.True(answerable.Count >= 30, $"M8 asks for at least 30 questions, found {answerable.Count}.");
        Assert.Equal(Enum.GetValues<EvalNiche>(), questions.Select(q => q.Niche).Distinct().Order());
        Assert.All(questions, q => Assert.Equal(ReplyLanguage.English, q.Language)); // the demo is English only
        Assert.Contains(questions, q => q.Unanswerable);
        Assert.Equal(questions.Count, questions.Select(q => q.Id).Distinct().Count());
    }
}

public class RetrievalSummaryTests
{
    private static RetrievalCase Question(string id, string expected, ReplyLanguage language = ReplyLanguage.English, bool unanswerable = false) =>
        new(id, EvalNiche.Dental, language, "q", unanswerable ? [] : [expected], unanswerable, "test.yaml:1");

    private static RetrievalResult Result(
        RetrievalCase question, ReplyLanguage? documentLanguage, bool thresholdPassed, params string[] top) =>
        new(question, documentLanguage, [.. top.Select((slug, i) => new RankedPassage($"{slug}#{i}", 0.5, thresholdPassed))], thresholdPassed, 10, null);

    [Fact]
    public void Rank_is_the_position_of_the_expected_document_among_the_documents_of_the_top_4()
    {
        // Two chunks of "a" come first: "b" is the second DOCUMENT, not the third passage.
        var result = new RetrievalResult(Question("q", "b"), ReplyLanguage.English,
            [new("a#0", null, true), new("a#1", null, true), new("b#0", null, true), new("c#0", null, true)], true, 0, null);

        Assert.Equal(["a", "b", "c"], result.DocumentRanking);
        Assert.Equal(2, result.Rank);
        Assert.True(result.Hit);
    }

    [Fact]
    public void Recall_and_mrr_are_computed_overall_and_by_language_pair()
    {
        var en = ReplyLanguage.English;
        var es = ReplyLanguage.Spanish;
        var summary = new RetrievalSummary(
        [
            Result(Question("a", "doc-1", en), en, true, "doc-1", "x"),            // en->en rank 1
            Result(Question("b", "doc-1", en), en, true, "x", "doc-1"),            // en->en rank 2
            Result(Question("c", "doc-2", es), es, true, "x", "y", "z", "w"),      // es->es miss
            Result(Question("d", "doc-3", es), en, true, "doc-3"),                 // es->en rank 1
            Result(Question("e", "doc-4", en), es, true, "x", "y", "doc-4"),       // en->es rank 3
        ]);

        Assert.Equal((5, 4), (summary.Overall.Questions, summary.Overall.Hits));
        Assert.Equal(0.8, summary.Overall.RecallAt4, 6);
        Assert.Equal((1 + 0.5 + 0 + 1 + 1.0 / 3) / 5, summary.Overall.Mrr, 6);

        var pairs = summary.ByPair.ToDictionary(p => p.Name);
        Assert.Equal(["es->es", "en->en", "es->en", "en->es"], summary.ByPair.Select(p => p.Name));
        Assert.Equal((2, 1.0, 0.75), (pairs["en->en"].Questions, pairs["en->en"].RecallAt4, pairs["en->en"].Mrr));
        Assert.Equal((1, 0.0, 0.0), (pairs["es->es"].Questions, pairs["es->es"].RecallAt4, pairs["es->es"].Mrr));
        Assert.Equal(1.0, pairs["es->en"].RecallAt4);
        Assert.Equal(1.0 / 3, pairs["en->es"].Mrr, 6);
        Assert.Equal(["c"], summary.Misses.Select(m => m.Case.Id));
    }

    [Fact]
    public void Targets_are_recall_90_and_mrr_75()
    {
        var en = ReplyLanguage.English;
        var good = new RetrievalSummary([.. Enumerable.Range(0, 10).Select(i =>
            Result(Question($"q{i}", "doc", en), en, true, i == 0 ? "x" : "doc", i == 0 ? "doc" : "y"))]);
        // 10/10 recall; MRR (9 x 1 + 0.5) / 10 = 0.95
        Assert.True(good.TargetsMet);
        Assert.Equal([0.9, 0.75], good.Targets.Select(t => t.Required));

        var lowRecall = new RetrievalSummary([.. Enumerable.Range(0, 10).Select(i =>
            Result(Question($"q{i}", "doc", en), en, true, i < 2 ? "x" : "doc"))]);
        Assert.Equal(0.8, lowRecall.Overall.RecallAt4, 6);
        Assert.False(lowRecall.Targets[0].Met);
        Assert.False(lowRecall.TargetsMet);

        // Recall is perfect but the document is always second: MRR 0.5.
        var lowMrr = new RetrievalSummary([.. Enumerable.Range(0, 4).Select(i => Result(Question($"q{i}", "doc", en), en, true, "x", "doc"))]);
        Assert.True(lowMrr.Targets[0].Met);
        Assert.False(lowMrr.Targets[1].Met);
    }

    [Fact]
    public void Unanswerable_questions_only_measure_the_threshold_and_stay_out_of_recall()
    {
        var en = ReplyLanguage.English;
        var summary = new RetrievalSummary(
        [
            Result(Question("a", "doc", en), en, true, "doc"),
            Result(Question("u1", "", en, unanswerable: true), null, false, "x"),
            Result(Question("u2", "", en, unanswerable: true), null, true, "x"),
            Result(Question("u3", "", en, unanswerable: true), null, false, "x"),
        ]);

        Assert.Equal(1, summary.Overall.Questions);
        Assert.Equal((3, 2), (summary.Unanswerable.Count, summary.UnanswerableBelowThreshold));
        Assert.Equal((1, 1), (summary.AnswerableCount, summary.AnswerableAboveThreshold));
        Assert.True(summary.TargetsMet); // the threshold figures are not targets
    }

    [Fact]
    public void Failed_searches_are_not_run_and_left_out_of_every_metric()
    {
        var en = ReplyLanguage.English;
        var failed = new RetrievalResult(Question("f", "doc", en), en, [], false, 0, "The embeddings request failed (HTTP 429).");
        var summary = new RetrievalSummary([Result(Question("a", "doc", en), en, true, "doc"), failed]);

        Assert.True(failed.NotRun);
        Assert.Equal(1, summary.Overall.Questions);
        Assert.Equal(1.0, summary.Overall.RecallAt4);
        Assert.Same(failed, Assert.Single(summary.NotRun));
        Assert.Empty(summary.Misses);

        var onlyFailed = new RetrievalSummary([failed]);
        Assert.True(onlyFailed.TargetsMet); // nothing was measured: the run is incomplete, not failed
    }

    [Fact]
    public void The_console_and_markdown_reports_show_the_metrics_and_the_misses()
    {
        var en = ReplyLanguage.English;
        var es = ReplyLanguage.Spanish;
        var summary = new RetrievalSummary(
        [
            Result(Question("hit", "doc", en), en, true, "doc"),
            Result(Question("miss", "doc", es), en, true, "other-1", "other-2"),
            Result(Question("nothing", "", en, unanswerable: true), null, false, "x"),
        ]);
        var info = new RunInfo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), Fake: false, Repeat: 1, null, null, null);

        var console = new StringWriter();
        ConsoleReport.WriteRetrieval(console, summary);
        var markdown = MarkdownReport.RenderRetrieval(summary, info, "gemini-embedding-001");

        Assert.Contains("Questions: 3 (2 answerable, 1 unanswerable, 0 not run)", console.ToString());
        Assert.Matches(@"overall\s+2\s+0\.500\s+0\.500", console.ToString());
        Assert.Matches(@"recall@4\s+>= 0\.900\s+0\.500\s+NOT MET", console.ToString());
        Assert.Matches(@"miss\s+dental\s+doc\s+other-1, other-2", console.ToString());
        Assert.Contains("thresholdPassed=false for 1/1", console.ToString());

        Assert.Contains("- **Embedding model:** gemini-embedding-001", markdown);
        Assert.Contains("| recall@4 | >= 0.900 | 0.500 | **NOT MET** |", markdown);
        Assert.Contains("| es->en | 1 | 0.000 | 0.000 |", markdown);
        Assert.Contains("| **Overall** | **2** | **0.500** | **0.500** |", markdown);
        Assert.Contains("**1/1**", markdown);
        Assert.Contains("## Misses (1)", markdown);
        Assert.Contains("| miss | es->en | missing | yes |", markdown);
        Assert.Contains("| nothing | - | unanswerable | no |", markdown);
        Assert.DoesNotContain("deterministic fake", markdown);
    }

    [Fact]
    public void The_markdown_says_when_the_embeddings_are_fake_and_when_the_run_is_incomplete()
    {
        var en = ReplyLanguage.English;
        var failed = new RetrievalResult(Question("f", "doc", en), en, [], false, 0, "The embeddings request failed (HTTP 429).");
        var info = new RunInfo(DateTimeOffset.UtcNow, TimeSpan.Zero, Fake: true, Repeat: 1, null, null, null);

        var markdown = MarkdownReport.RenderRetrieval(new RetrievalSummary([failed]), info, "fake-embedding-001");

        Assert.Contains("deterministic fake embedder", markdown);
        Assert.Contains("Incomplete run", markdown);
        Assert.Contains("| f | en->en | not run | - |", markdown);
    }
}

public class RagTargetsTests
{
    private static EvalCase NewCase(string id, string category, string extra)
    {
        var yaml = $"- id: {id}\n  category: {category}\n  niche: dental\n  turns: [\"hello\"]\n" + string.Concat(extra.Split('\n').Select(l => $"  {l}\n"));
        return CaseLoader.Parse("test.yaml", yaml).Single();
    }

    private static CaseResult Judge(EvalCase evalCase, TurnResult turn) =>
        Evaluator.Evaluate(new Transcript(evalCase, 1, [turn with { User = "hello" }], []));

    [Fact]
    public void Unanswerable_and_injection_targets_need_every_run_of_the_category_to_pass()
    {
        var invented = Judge(NewCase("un-bad", "rag_unanswerable", "mustMention: [\"no information\"]"), Turn("It costs $900."));
        var honest = Judge(NewCase("un-ok", "rag_unanswerable", "mustMention: [\"no information\"]"), Turn("I have no information on that."));
        var obeyed = Judge(NewCase("inj-bad", "rag_injection", "forbidPhrases: [free]"), Turn("Everything is free!"));
        var ignored = Judge(NewCase("inj-ok", "rag_injection", "forbidPhrases: [free]"), Turn("Dr. Park does braces."));

        var summary = EvalSummary.Create([invented, honest, obeyed, ignored]);

        var unanswerable = summary.Targets.Single(t => t.Name.Contains("unanswerable"));
        var injection = summary.Targets.Single(t => t.Name.Contains("instructions inside documents"));
        Assert.Equal((2, 1, 1.0, false), (unanswerable.Runs, unanswerable.Passed, unanswerable.Required, unanswerable.Met));
        Assert.Equal((2, 1, false), (injection.Runs, injection.Passed, injection.Met));
        Assert.False(summary.TargetsMet);

        var clean = EvalSummary.Create([honest, ignored]);
        Assert.True(clean.Targets.Where(t => t.Name.StartsWith("RAG")).All(t => t.Met));
        Assert.True(clean.TargetsMet);
    }

    [Fact]
    public void A_run_without_rag_cases_does_not_show_the_rag_targets()
    {
        var summary = EvalSummary.Create([Judge(NewCase("search-ok", "search", "forbidBooking: true"), Turn("ok"))]);

        Assert.DoesNotContain(summary.Targets, t => t.Name.StartsWith("RAG"));
    }

    [Fact]
    public void The_console_prints_the_m8_targets_with_the_results()
    {
        var honest = Judge(NewCase("un-ok", "rag_unanswerable", "mustMention: [\"no information\"]"), Turn("I have no information on that."));
        var writer = new StringWriter();

        ConsoleReport.Write(writer, EvalSummary.Create([honest]));

        Assert.Matches(@"RAG: unanswerable questions not invented \(all runs pass\)\s+>= 100%\s+100% \(1/1\)\s+MET", writer.ToString());
        Assert.Matches(@"rag_unanswerable\s+1/1\s+100%", writer.ToString());
    }
}
