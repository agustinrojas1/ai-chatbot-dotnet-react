using Chatbot.Evals;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Retrieval;
using Chatbot.Evals.Running;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Evals;

/// <summary>The RAG part of the harness against a real pgvector database, the real documents and the deterministic fake embeddings.</summary>
[Collection(PostgresCollection.Name)]
public class RagRunnerTests(PostgresFixture postgres)
{
    private async Task<ServiceProvider> HostAsync(Microsoft.Extensions.AI.IChatClient? model = null)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var services = EvalHost.Build(
            EvalConfiguration.Build(connectionString, fakeEmbeddings: true), model ?? new FakeChatClient(), policy: null, embeddings: new FakeEmbeddingGenerator());
        await EvalHost.PrepareDatabaseAsync(services, CancellationToken.None);
        return services;
    }

    [Fact]
    public async Task Ingestion_indexes_every_document_of_the_three_businesses_and_a_second_run_changes_nothing()
    {
        await using var services = await HostAsync();

        var first = await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var second = await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);

        Assert.Equal(first.Documents, first.Processed);
        Assert.True(first.Documents >= 45, $"expected the 120 seeded documents, found {first.Documents}");
        Assert.True(first.Chunks >= first.Documents);
        Assert.Equal((first.Documents, 0), (second.Skipped, second.Processed));
    }

    [Fact]
    public async Task The_shipped_retrieval_questions_run_against_the_real_documents()
    {
        await using var services = await HostAsync();
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var questions = RetrievalCaseLoader.LoadDirectory(EvalTestSupport.ShippedCasesDirectory());

        var results = await RetrievalRunner.RunAsync(services, questions, onResult: null, CancellationToken.None);

        // Every expected slug exists (the runner stops on an unknown one) and a question is searched once.
        Assert.Equal(questions.Count, results.Count);
        Assert.All(results, r => Assert.False(r.NotRun));
        Assert.All(results.Where(r => !r.Case.Unanswerable), r => Assert.NotNull(r.Pair));
        Assert.All(results.Where(r => r.Case.Unanswerable), r => Assert.Null(r.Pair));

        // The fake finds shared words, so most questions are found, which shows the metrics are wired. The real embeddings
        // are measured by the eval harness, not here.
        var summary = new RetrievalSummary(results);
        var pair = Assert.Single(summary.ByPair);
        Assert.Equal("en->en", pair.Name);
        Assert.True(pair.RecallAt4 >= 0.7, $"en->en recall {pair.RecallAt4}");
        Assert.True(summary.Results.Sum(r => r.EmbeddingTokens) > 0);
    }

    [Fact]
    public async Task A_question_that_expects_an_unknown_document_stops_the_run_before_searching()
    {
        await using var services = await HostAsync();
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var questions = RetrievalCaseLoader.Parse("t.yaml", "- { id: typo, niche: dental, language: en, question: q, expectSources: [cancelation-y-no-show] }");

        var ex = await Assert.ThrowsAsync<CaseFormatException>(() => RetrievalRunner.RunAsync(services, questions, null, CancellationToken.None));

        Assert.Contains("expects document 'cancelation-y-no-show', which is not in the dental knowledge base", Assert.Single(ex.Problems));
    }

    [Fact]
    public async Task Questions_are_scoped_to_the_business_of_their_niche()
    {
        await using var services = await HostAsync();
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        // A dental document slug asked in the restaurant business: it is not there, so the harness refuses it as unknown.
        var questions = RetrievalCaseLoader.Parse("t.yaml", "- { id: wrong-niche, niche: restaurant, language: es, question: q, expectSources: [cancellation-and-no-show] }");

        await Assert.ThrowsAsync<CaseFormatException>(() => RetrievalRunner.RunAsync(services, questions, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_scripted_agent_that_searches_the_knowledge_base_passes_an_answer_case()
    {
        var model = new ScriptedChatClient((_, messages) =>
            messages.Last(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any()
                ? Turn.Reply("The late-cancellation charge is $45 if you cancel with less than 24 hours' notice.")
                : Turn.Call(AgentToolFactory.SearchKnowledgeBase, new()
                {
                    ["query"] = "If you cancel with less than 24 hours notice, a late-cancellation charge of $45 applies.",
                }));
        await using var services = await HostAsync(model);
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: rag-answer-test
              category: rag_answer
              niche: dental
              turns: ["How much does it cost to cancel an appointment at short notice?"]
              expectLanguage: en
              expectToolCalls: [{ name: search_knowledge_base }]
              expectRetrievalSources: [cancellation-and-no-show]
              mustMention: ["$45"]
              forbidToolCalls: [create_booking]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        var turn = result.Transcript.Turns[0];
        Assert.Equal(["search_knowledge_base"], turn.ToolCalls.Select(c => c.Name)); // the Retrieval event counts as the tool call
        var retrieval = Assert.Single(turn.Retrievals);
        Assert.True(retrieval.ThresholdPassed);
        Assert.Contains(retrieval.Passages, p => p.Relevant && p.Slug == "cancellation-and-no-show");
        Assert.Equal("search_knowledge_base", turn.ToolCalls[0].Name);
        Assert.True(turn.ToolCalls[0].Result!.Value.TryGetProperty("passages", out _)); // the Retrieval output, with its passages
    }

    [Fact]
    public async Task A_scripted_agent_that_admits_it_has_no_information_passes_an_unanswerable_case()
    {
        var model = new ScriptedChatClient((_, messages) =>
            messages.Last(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any()
                ? Turn.Reply("I don't have that information. I can connect you with a person from the team.")
                : Turn.Call(AgentToolFactory.SearchKnowledgeBase, new() { ["query"] = "valet zzz qqq" }));
        await using var services = await HostAsync(model);
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: rag-unanswerable-test
              category: rag_unanswerable
              niche: restaurant
              turns: ["Do you offer valet parking?"]
              expectLanguage: en
              forbidToolCalls: [create_booking]
              mustMention:
                - anyOf: ["don't have", "no information"]
                - anyOf: ["person", "team"]
              forbidPhrases: ["we offer valet"]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        Assert.False(Assert.Single(result.Transcript.Turns[0].Retrievals).ThresholdPassed); // found: false
    }

    [Fact]
    public async Task An_answer_from_memory_without_searching_fails_the_retrieval_expectations()
    {
        await using var services = await HostAsync(new ScriptedChatClient((_, _) => Turn.Reply("The charge is $45 if you cancel late.")));
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: rag-answer-memory
              category: rag_answer
              niche: dental
              turns: ["How much does a late cancellation cost?"]
              expectLanguage: en
              expectToolCalls: [{ name: search_knowledge_base }]
              expectRetrievalSources: [cancellation-and-no-show]
              mustMention: ["$45"]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Failed);
        Assert.Equal(["calls search_knowledge_base", "retrieval returned cancellation-and-no-show"], result.Failures.Select(f => f.Name));
    }
}
