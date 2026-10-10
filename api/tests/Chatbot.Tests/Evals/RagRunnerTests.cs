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
        Assert.True(first.Documents >= 45, $"expected the 54 seeded documents, found {first.Documents}");
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

        // The fake finds shared words: same-language questions are found, which shows the metrics are wired. Cross-language ones
        // (es->en, en->es) are what the real embeddings are measured on.
        var summary = new RetrievalSummary(results);
        var pairs = summary.ByPair.ToDictionary(p => p.Name);
        Assert.Equal(["es->es", "en->en", "es->en", "en->es"], summary.ByPair.Select(p => p.Name));
        Assert.True(pairs["es->es"].RecallAt4 >= 0.8, $"es->es recall {pairs["es->es"].RecallAt4}");
        Assert.True(pairs["en->en"].RecallAt4 >= 0.8, $"en->en recall {pairs["en->en"].RecallAt4}");
        Assert.True(pairs["es->en"].RecallAt4 < 0.5);
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
        var questions = RetrievalCaseLoader.Parse("t.yaml", "- { id: wrong-niche, niche: restaurant, language: es, question: q, expectSources: [cancelacion-y-no-show] }");

        await Assert.ThrowsAsync<CaseFormatException>(() => RetrievalRunner.RunAsync(services, questions, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_scripted_agent_that_searches_the_knowledge_base_passes_an_answer_case()
    {
        var model = new ScriptedChatClient((_, messages) =>
            messages.Last(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any()
                ? Turn.Reply("El cargo por cancelación tardía es de $45 si cancela con menos de 24 horas de anticipación.")
                : Turn.Call(AgentToolFactory.SearchKnowledgeBase, new()
                {
                    ["query"] = "Si cancela con menos de 24 horas de anticipación, se aplica un cargo por cancelación tardía de $45.",
                }));
        await using var services = await HostAsync(model);
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: rag-answer-test
              category: rag_answer
              niche: dental
              turns: ["¿Cuánto cuesta cancelar un turno con poca anticipación?"]
              expectLanguage: es
              expectToolCalls: [{ name: search_knowledge_base }]
              expectRetrievalSources: [cancelacion-y-no-show]
              mustMention: ["$45"]
              forbidToolCalls: [create_booking]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        var turn = result.Transcript.Turns[0];
        Assert.Equal(["search_knowledge_base"], turn.ToolCalls.Select(c => c.Name)); // the Retrieval event counts as the tool call
        var retrieval = Assert.Single(turn.Retrievals);
        Assert.True(retrieval.ThresholdPassed);
        Assert.Contains(retrieval.Passages, p => p.Relevant && p.Slug == "cancelacion-y-no-show");
        Assert.Equal("search_knowledge_base", turn.ToolCalls[0].Name);
        Assert.True(turn.ToolCalls[0].Result!.Value.TryGetProperty("passages", out _)); // the Retrieval output, with its passages
    }

    [Fact]
    public async Task A_scripted_agent_that_admits_it_has_no_information_passes_an_unanswerable_case()
    {
        var model = new ScriptedChatClient((_, messages) =>
            messages.Last(m => m.Role != Microsoft.Extensions.AI.ChatRole.System).Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any()
                ? Turn.Reply("I don't have that information. I can connect you with a person from the team.")
                : Turn.Call(AgentToolFactory.SearchKnowledgeBase, new() { ["query"] = "valet parking zzz" }));
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
        await using var services = await HostAsync(new ScriptedChatClient((_, _) => Turn.Reply("El cargo es de $45 si cancela tarde.")));
        await EvalHost.IngestKnowledgeAsync(services, CancellationToken.None);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: rag-answer-memory
              category: rag_answer
              niche: dental
              turns: ["¿Cuánto cuesta cancelar un turno tarde?"]
              expectLanguage: es
              expectToolCalls: [{ name: search_knowledge_base }]
              expectRetrievalSources: [cancelacion-y-no-show]
              mustMention: ["$45"]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Failed);
        Assert.Equal(["calls search_knowledge_base", "retrieval returned cancelacion-y-no-show"], result.Failures.Select(f => f.Name));
    }
}
