using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Tests.Data;
using Chatbot.Tests.Tools;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Knowledge;

/// <summary>Hybrid retrieval against a real PostgreSQL with pgvector: fusion, thresholds and the scoping by business.</summary>
[Collection(PostgresCollection.Name)]
public class KnowledgeSearchTests(PostgresFixture postgres)
{
    private static string Filler(int words, string prefix) => string.Join(' ', Enumerable.Range(0, words).Select(i => $"{prefix}{i}"));

    /// <summary>Business A (real estate) and B (restaurant) with documents; "saffron" appears in both on purpose.</summary>
    private static FakeKnowledgeSource Documents() => new FakeKnowledgeSource()
        .Put(Niche.RealEstate, "pets", "Pets policy", "## Dogs\n\nDogs up to 25 kg are welcome with a refundable deposit.\n\n## Saffron\n\nThe building garden grows saffron.")
        .Put(Niche.RealEstate, "visits", "Visit cancellations", "Visits can be cancelled up to 24 hours before without any fee.")
        .Put(Niche.RealEstate, "payment-codes", "Payment codes", $"## Codes\n\n{Filler(60, "filler")} Use transfer reference ZX-9981 when paying the reservation. {Filler(60, "more")}")
        .Put(Niche.Restaurant, "allergens", "Allergens", "Our kitchen handles nuts, gluten and dairy. Saffron risotto contains dairy.")
        .Put(Niche.Restaurant, "delivery", "Delivery zones", "Delivery covers the zones Palermo, Recoleta and Belgrano until 23:00.");

    private static async Task<ToolWorld> WorldWithDocumentsAsync(PostgresFixture postgres, ToolWorld? existing = null)
    {
        var world = existing ?? await ToolWorld.CreateAsync(postgres);
        await using var db = world.NewDb();
        await Documents().IngestAsync(db, world.Embedder);
        return world;
    }

    [Fact]
    public async Task A_question_finds_its_document_with_the_three_scores()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "Can the visits be cancelled up to 24 hours before without any fee?", CancellationToken.None);

        Assert.True(result.ThresholdPassed);
        var best = result.Passages[0];
        Assert.Equal("visits#0", best.SourceId);
        Assert.Equal("Visit cancellations", best.Title);
        Assert.StartsWith("Visit cancellations", best.Text);
        Assert.True(best.AboveThreshold);
        Assert.InRange(best.SemanticScore!.Value, 0.3, 1);
        Assert.True(best.LexicalScore > 0);
        Assert.True(best.FinalScore > 0);
        Assert.True(result.Passages.Count <= KnowledgeSearch.MaxPassages);
        Assert.Equal(TestKnowledge.Model, result.EmbeddingModel);
        Assert.True(result.EmbeddingTokens > 0);
        Assert.Equal((0.3, 0.05), (result.SemanticThreshold, result.LexicalThreshold));
    }

    [Fact]
    public async Task A_sentence_that_no_chunk_repeats_word_for_word_has_no_lexical_hits_and_is_found_semantically()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "Can I cancel the visit 24 hours before without a fee?", CancellationToken.None);

        var best = result.Passages[0];
        Assert.Equal("visits#0", best.SourceId);
        Assert.All(result.Passages, p => Assert.Null(p.LexicalScore)); // "cancel" is not in the document, which says "cancelled": the AND query matches nothing
        Assert.InRange(best.SemanticScore!.Value, 0.3, 1);
        Assert.True(result.ThresholdPassed);
    }

    [Fact]
    public async Task An_exact_code_is_found_by_the_lexical_side_when_the_semantic_score_is_below_the_threshold()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "ZX-9981", CancellationToken.None);

        var best = result.Passages[0];
        Assert.Equal("payment-codes#0", best.SourceId);
        Assert.True(best.SemanticScore < 0.3, $"semantic similarity was {best.SemanticScore}: the vector side does not find it");
        Assert.True(best.LexicalScore >= 0.05);
        Assert.True(best.AboveThreshold);
        Assert.True(result.ThresholdPassed);
    }

    [Fact]
    public async Task Without_the_lexical_side_the_same_exact_code_would_not_pass_the_threshold()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        world.KnowledgeSettings = TestKnowledge.SearchSettings(minLexical: 1000); // lexical hits never count
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "ZX-9981", CancellationToken.None);

        Assert.False(result.ThresholdPassed);
        Assert.DoesNotContain(result.Passages, p => p.AboveThreshold);
    }

    [Fact]
    public async Task An_unrelated_question_does_not_pass_the_threshold_but_still_lists_its_candidates()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "quantum chromodynamics lattice simulation", CancellationToken.None);

        Assert.False(result.ThresholdPassed);
        Assert.NotEmpty(result.Passages); // the nearest chunks are still reported, flagged as below the threshold
        Assert.All(result.Passages, p => Assert.False(p.AboveThreshold));
        Assert.Empty(result.Relevant);
    }

    [Fact]
    public async Task A_query_of_common_words_only_runs_no_lexical_search_and_does_not_pass()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "what is the", CancellationToken.None);

        Assert.False(result.ThresholdPassed);
        Assert.All(result.Passages, p => Assert.Null(p.LexicalScore));
    }

    [Fact]
    public async Task A_search_never_returns_chunks_of_another_business()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();
        var knowledge = world.NewKnowledge(db);

        // "saffron" is in a document of each business; the allergens document only exists in B.
        var asA = await knowledge.SearchAsync(world.A.Id, "saffron risotto dairy allergens", CancellationToken.None);
        var asB = await knowledge.SearchAsync(world.B.Id, "saffron risotto dairy allergens", CancellationToken.None);
        var delivery = await knowledge.SearchAsync(world.A.Id, "delivery zones Palermo Recoleta Belgrano", CancellationToken.None);

        await using var check = world.NewDb();
        var owners = await check.KnowledgeChunks.ToDictionaryAsync(c => c.Id, c => c.BusinessId);
        Assert.All(asA.Passages, p => Assert.Equal(world.A.Id, owners[p.ChunkId]));
        Assert.All(asB.Passages, p => Assert.Equal(world.B.Id, owners[p.ChunkId]));
        Assert.Equal("allergens#0", asB.Passages[0].SourceId);
        Assert.DoesNotContain(asA.Passages, p => p.SourceId.StartsWith("allergens", StringComparison.Ordinal));

        // The best answer exists only in B: for A the question is not answerable.
        Assert.DoesNotContain(delivery.Passages, p => p.SourceId.StartsWith("delivery", StringComparison.Ordinal));
        Assert.False(delivery.ThresholdPassed);
    }

    [Fact]
    public async Task Chunks_of_another_embedding_model_are_never_compared_semantically()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();
        var otherModel = TestKnowledge.NewEmbedder(world.Embeddings, TestKnowledge.EmbeddingSettings("another-embedding-model"));

        var result = await TestKnowledge.NewSearch(db, otherModel, world.KnowledgeSettings)
            .SearchAsync(world.A.Id, "Can I cancel the visit 24 hours before without a fee?", CancellationToken.None);

        Assert.All(result.Passages, p => Assert.Null(p.SemanticScore));
    }

    [Fact]
    public async Task A_business_without_documents_gets_an_empty_result()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var db = world.NewDb();

        var result = await world.NewKnowledge(db).SearchAsync(world.A.Id, "anything at all", CancellationToken.None);

        Assert.Empty(result.Passages);
        Assert.False(result.ThresholdPassed);
    }

    [Fact]
    public async Task The_search_is_deterministic()
    {
        var world = await WorldWithDocumentsAsync(postgres);
        await using var db = world.NewDb();
        var knowledge = world.NewKnowledge(db);

        var first = await knowledge.SearchAsync(world.A.Id, "dogs deposit visit", CancellationToken.None);
        var second = await knowledge.SearchAsync(world.A.Id, "dogs deposit visit", CancellationToken.None);

        Assert.Equal(first.Passages.Select(p => (p.SourceId, p.FinalScore)), second.Passages.Select(p => (p.SourceId, p.FinalScore)));
    }
}
