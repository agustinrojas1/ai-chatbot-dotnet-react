using Chatbot.Evals.Cases;

namespace Chatbot.Tests.Evals;

public class CaseLoaderTests
{
    private const string Minimal = """
        - id: ok-case
          category: search
          niche: restaurant
          turns:
            - "What is on the menu?"
          expectToolCalls:
            - name: search_catalog
        """;

    [Fact]
    public void Reads_every_kind_of_expectation()
    {
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: full-case
              category: not_in_catalog
              niche: dental
              rubric: free text for a judge
              turns:
                - "How much are braces?"
                - say: "Yes please"
                  expectToolCalls:
                    - name: handoff_to_human
              expectLanguage: en
              expectToolCalls:
                - name: search_catalog
                  args: { query: braces, maxPrice: 100, filters: { glutenFree: true } }
                  count: 1
                - name: handoff_to_human
              expectToolOrder: true
              forbidToolCalls: [create_booking]
              forbidPhrases: ["braces cost $"]
              mustMention:
                - "person"
                - anyOf: ["human", "team"]
                - fromTool: search_catalog
                  path: "results[*].price"
              forbidBooking: true
              expectHandoff: true
            """).Single();

        Assert.Equal(("full-case", EvalCategory.NotInCatalog, EvalNiche.Dental), (evalCase.Id, evalCase.Category, evalCase.Niche));
        Assert.Equal("free text for a judge", evalCase.Rubric);
        Assert.Equal(["How much are braces?", "Yes please"], evalCase.Turns.Select(t => t.Say));
        Assert.Equal("handoff_to_human", Assert.Single(evalCase.Turns[1].Expect.ToolCalls).Name);

        var expect = evalCase.Expect;
        Assert.Equal(ReplyLanguage.English, expect.Language);
        Assert.True(expect.ToolsInOrder);
        Assert.Equal(["create_booking"], expect.ForbidToolCalls);
        Assert.Equal(["braces cost $"], expect.ForbidPhrases);
        Assert.False(expect.Booking);
        Assert.True(expect.Handoff);
        Assert.Equal(1, expect.ToolCalls[0].Count);
        Assert.Equal("""{"query":"braces","maxPrice":100,"filters":{"glutenFree":true}}""", expect.ToolCalls[0].Args!.Value.GetRawText());
        Assert.Equal([["person"], ["human", "team"]], expect.MustMention.OfType<MentionText>().Select(m => m.AnyOf));
        Assert.Equal(new MentionFromTool("search_catalog", "results[*].price"), expect.MustMention.OfType<MentionFromTool>().Single());
    }

    [Fact]
    public void Quoted_numbers_in_args_stay_text_and_plain_ones_are_numbers()
    {
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: args-case
              category: search
              niche: realestate
              turns: ["hi"]
              expectToolCalls:
                - name: search_catalog
                  args: { rooms: 2, neighborhood: "2" }
            """).Single();

        Assert.Equal("""{"rooms":2,"neighborhood":"2"}""", evalCase.Expect.ToolCalls.Single().Args!.Value.GetRawText());
    }

    [Fact]
    public void Accepts_the_spec_spellings_of_category_and_niche()
    {
        var cases = CaseLoader.Parse("test.yaml", """
            - id: a
              category: smallTalk
              niche: realestate
              turns: ["hi"]
              expectHandoff: false
            - id: b
              category: not_in_catalog
              niche: dental
              turns: ["hi"]
              expectHandoff: false
            """);

        Assert.Equal([EvalCategory.SmallTalk, EvalCategory.NotInCatalog], cases.Select(c => c.Category));
    }

    [Fact]
    public void Reports_an_unknown_key_with_its_file_and_line()
    {
        var ex = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("typo.yaml", """
            - id: typo-case
              category: search
              niche: dental
              turns: ["hi"]
              expectToolCall:
                - name: search_catalog
            """));

        var problem = Assert.Single(ex.Problems);
        Assert.StartsWith("typo.yaml:5: unknown key 'expectToolCall'", problem);
        Assert.Contains("expectToolCalls", problem);
    }

    [Fact]
    public void Reports_every_problem_at_once_and_lists_valid_values()
    {
        var ex = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("many.yaml", """
            - id: Bad Id
              category: shopping
              niche: bakery
              turns: []
            - category: search
              niche: dental
              turns: ["hi"]
            """));

        Assert.Contains(ex.Problems, p => p.StartsWith("many.yaml:1:") && p.Contains("lowercase letters"));
        Assert.Contains(ex.Problems, p => p.Contains("category 'shopping' is not valid") && p.Contains("not_in_catalog") && p.Contains("smallTalk"));
        Assert.Contains(ex.Problems, p => p.Contains("niche 'bakery' is not valid") && p.Contains("dental"));
        Assert.Contains(ex.Problems, p => p.Contains("'turns' must be a non-empty list"));
        Assert.Contains(ex.Problems, p => p.StartsWith("many.yaml:5:") && p.Contains("missing 'id'"));
        Assert.Contains("many.yaml:", ex.Message);
    }

    [Theory]
    [InlineData("expectToolCalls:\n    - name: search_catlog", "unknown tool 'search_catlog'")]
    [InlineData("forbidToolCalls: [make_coffee]", "unknown tool 'make_coffee'")]
    [InlineData("expectLanguage: fr", "expectLanguage 'fr' must be 'en' or 'es'")]
    [InlineData("expectHandoff: maybe", "expectHandoff must be true or false")]
    [InlineData("expectBooking: true\n  forbidBooking: true", "cannot both be true")]
    [InlineData("expectBookingTotal: 10", "needs expectBooking: true")]
    [InlineData("expectBooking: true\n  expectBookingTotal: ten", "'ten' is not a number")]
    [InlineData("expectToolCalls:\n    - name: search_catalog\n      count: 0", "count must be a whole number of at least 1")]
    [InlineData("expectToolCalls:\n    - name: search_catalog\n  expectToolOrder: true", "at least two")]
    [InlineData("expectToolCalls:\n    - name: search_catalog\n      args: nope", "args must be a mapping")]
    [InlineData("mustMention:\n    - fromTool: search_catalog", "missing 'path'")]
    [InlineData("mustMention:\n    - fromTool: search_catalog\n      path: results[", "path 'results[' is not valid")]
    [InlineData("mustMention:\n    - anyOf: []", "anyOf needs at least one text")]
    [InlineData("mustMention:\n    - anyOf: [a]\n      fromTool: search_catalog", "cannot be combined")]
    [InlineData("forbidPhrases:\n    - [nested]", "must be a text")]
    public void Rejects_invalid_expectations(string expectation, string expectedMessage)
    {
        var yaml = "- id: bad-case\n  category: search\n  niche: dental\n  turns: [\"hi\"]\n  " + expectation + "\n";

        var ex = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("bad.yaml", yaml));

        Assert.Contains(ex.Problems, p => p.Contains(expectedMessage));
    }

    [Fact]
    public void Rejects_a_case_that_could_never_fail()
    {
        var ex = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("empty.yaml", """
            - id: no-expectations
              category: search
              niche: dental
              turns: ["hi"]
            """));

        Assert.Contains("has no expectations", Assert.Single(ex.Problems));
    }

    [Fact]
    public void Rejects_broken_yaml_and_files_that_are_not_a_list()
    {
        var broken = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("broken.yaml", "- id: [unclosed\n  category: search"));
        Assert.StartsWith("broken.yaml", Assert.Single(broken.Problems));
        Assert.Contains("invalid YAML", broken.Problems[0]);

        var badIndent = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("indent.yaml", "- id: a\n  category: search\n turns: [x]"));
        Assert.StartsWith("indent.yaml:", Assert.Single(badIndent.Problems));

        var notAList = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("map.yaml", "id: single\ncategory: search"));
        Assert.Contains("must be a list of cases", Assert.Single(notAList.Problems));

        var empty = Assert.Throws<CaseFormatException>(() => CaseLoader.Parse("none.yaml", "[]"));
        Assert.Contains("no cases", Assert.Single(empty.Problems));
    }

    [Fact]
    public void Ids_must_be_unique_across_files()
    {
        var ex = Assert.Throws<CaseFormatException>(() =>
            CaseLoader.ParseAll([("one.yaml", Minimal), ("two.yaml", Minimal)]));

        var problem = Assert.Single(ex.Problems);
        Assert.Contains("duplicate case id 'ok-case'", problem);
        Assert.Contains("two.yaml", problem);
        Assert.Contains("one.yaml", problem);
    }

    [Fact]
    public void The_shipped_cases_are_valid_and_cover_the_spec()
    {
        var cases = CaseLoader.LoadDirectory(EvalTestSupport.ShippedCasesDirectory());

        Assert.True(cases.Count >= 25, $"SPEC 12 asks for at least 25 cases, found {cases.Count}.");
        Assert.Equal(Enum.GetValues<EvalCategory>(), cases.Select(c => c.Category).Distinct().Order());
        Assert.Equal(Enum.GetValues<EvalNiche>(), cases.Select(c => c.Niche).Distinct().Order());
        Assert.Contains(cases, c => c.Turns.Any(t => t.Expect.Language == ReplyLanguage.Spanish) && c.Turns.Any(t => t.Expect.Language == ReplyLanguage.English));
        Assert.Contains(cases, c => c.Expect.Booking == true);
        Assert.Contains(cases, c => c.Expect.BookingTotal is not null);
        Assert.Contains(cases, c => c.Expect.Handoff == true);
        Assert.Contains(cases, c => c.Expect.MustMention.OfType<MentionFromTool>().Any());
    }

    [Fact]
    public void Reads_the_knowledge_base_categories_and_the_expected_retrieval_sources()
    {
        var cases = CaseLoader.Parse("test.yaml", """
            - id: rag-case
              category: rag_unanswerable
              niche: dental
              turns:
                - say: "Do you do root canals?"
                  expectRetrievalSources: [cancelacion-y-no-show]
              expectRetrievalSources: [a-doc, other-doc]
              expectToolCalls: [{ name: search_knowledge_base }]
            - id: rag-case-2
              category: ragMixed
              niche: restaurant
              turns: ["hi"]
              forbidToolCalls: [search_knowledge_base]
            """);

        Assert.Equal([EvalCategory.RagUnanswerable, EvalCategory.RagMixed], cases.Select(c => c.Category));
        Assert.Equal(["a-doc", "other-doc"], cases[0].Expect.RetrievalSources);
        Assert.Equal(["cancelacion-y-no-show"], cases[0].Turns[0].Expect.RetrievalSources);
        Assert.Equal("rag_unanswerable", CaseLoader.Name(EvalCategory.RagUnanswerable));
        Assert.Equal(EvalCategory.RagInjection, CaseLoader.ParseCategory("rag_injection"));
    }

    [Fact]
    public void The_shipped_knowledge_base_cases_cover_the_m8_spec()
    {
        var cases = CaseLoader.LoadDirectory(EvalTestSupport.ShippedCasesDirectory());
        var rag = cases.Where(c => c.Category is EvalCategory.RagAnswer or EvalCategory.RagUnanswerable or EvalCategory.RagMixed or EvalCategory.RagInjection).ToList();

        Assert.True(rag.Count >= 18, $"M8 asks for at least 15 end-to-end cases, the task for 18; found {rag.Count}.");
        Assert.True(cases.Count(c => c.Category == EvalCategory.RagUnanswerable) >= 4);
        Assert.Equal(3, cases.Count(c => c.Category == EvalCategory.RagInjection));
        Assert.Equal(Enum.GetValues<EvalNiche>(), rag.Where(c => c.Category == EvalCategory.RagInjection).Select(c => c.Niche).Order());
        Assert.Equal(Enum.GetValues<EvalNiche>(), rag.Select(c => c.Niche).Distinct().Order());
        Assert.All(rag.Where(c => c.Category is EvalCategory.RagAnswer or EvalCategory.RagMixed or EvalCategory.RagInjection),
            c => Assert.Contains(c.Expect.ToolCalls, t => t.Name == "search_knowledge_base"));
        Assert.All(rag.Where(c => c.Category == EvalCategory.RagMixed),
            c => Assert.Contains(c.Expect.ToolCalls, t => t.Name == "search_catalog"));
        Assert.All(rag.Where(c => c.Category is EvalCategory.RagAnswer or EvalCategory.RagInjection or EvalCategory.RagMixed),
            c => Assert.NotEmpty(c.Expect.RetrievalSources));
        Assert.All(rag.Where(c => c.Category == EvalCategory.RagUnanswerable),
            c => Assert.True(c.Expect.MustMention.Count + c.Turns.Sum(t => t.Expect.MustMention.Count) > 0, c.Id));
        Assert.All(rag.Where(c => c.Category == EvalCategory.RagInjection), c => Assert.NotEmpty(c.Expect.ForbidPhrases));
        Assert.Contains(rag, c => c.Expect.Language == ReplyLanguage.Spanish);
        Assert.Contains(rag, c => c.Expect.Language == ReplyLanguage.English);
        Assert.DoesNotContain(cases, c => c.Id.StartsWith("ret-"));
    }
}
