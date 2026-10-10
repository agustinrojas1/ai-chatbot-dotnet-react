using System.Net;
using Chatbot.Core.Domain;
using Chatbot.Core.Knowledge;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Tests.Data;
using Chatbot.Tests.Tools;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Knowledge;

/// <summary>Ingestion against a real PostgreSQL with pgvector, with the deterministic fake embeddings.</summary>
[Collection(PostgresCollection.Name)]
public class KnowledgeIngestorTests(PostgresFixture postgres)
{
    private const string Pets = "## Dogs\n\nDogs up to 25 kg are welcome with a refundable deposit of USD 300.\n\n## Cats\n\nCats are always welcome.";
    private const string Visits = "Visits can be cancelled up to 24 hours before without any fee.";

    private static FakeKnowledgeSource Source() => new FakeKnowledgeSource()
        .Put(Niche.RealEstate, "pets", "Pets policy", Pets)
        .Put(Niche.RealEstate, "visits", "Visit cancellations", Visits)
        .Put(Niche.Restaurant, "allergens", "Allergens", "Our kitchen handles nuts, gluten and dairy.");

    [Fact]
    public async Task Indexes_every_document_with_its_chunks_vectors_and_search_vector()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var db = world.NewDb();

        var report = await Source().IngestAsync(db, world.Embedder);

        Assert.Equal((3, 0, 3, 0), (report.Documents, report.Skipped, report.Processed, report.Deleted));
        Assert.Equal(4, report.Chunks); // pets has two sections
        Assert.True(report.EmbeddingTokens > 0);
        Assert.False(report.TokensEstimated);

        await using var check = world.NewDb();
        var pets = await check.KnowledgeDocuments.Include(d => d.Chunks).SingleAsync(d => d.Slug == "pets");
        Assert.Equal((world.A.Id, "realestate/pets.md", "Pets policy", "en"), (pets.BusinessId, pets.SourcePath, pets.Title, pets.Language));
        Assert.Equal(["test"], pets.Tags);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), pets.UpdatedAt);
        Assert.Equal(64, pets.ContentHash.Length);

        var chunks = pets.Chunks.OrderBy(c => c.Ordinal).ToList();
        Assert.Equal([0, 1], chunks.Select(c => c.Ordinal));
        Assert.Equal(["Dogs", "Cats"], chunks.Select(c => c.Heading));
        Assert.All(chunks, c =>
        {
            Assert.Equal(world.A.Id, c.BusinessId);
            Assert.Equal(TestKnowledge.Model, c.EmbeddingModel);
            Assert.Equal(KnowledgeSchema.EmbeddingDimensions, c.Embedding.ToArray().Length);
            Assert.StartsWith("Pets policy > ", c.Text);
            Assert.True(c.TokenCount > 0);
        });
        Assert.Equal(world.Embeddings.Vectorize(chunks[0].Text), chunks[0].Embedding.ToArray());

        // The generated tsvector column is filled by PostgreSQL from heading + text.
        Assert.Equal(1, await check.KnowledgeChunks.CountAsync(c => c.SearchVector.Matches(EF.Functions.ToTsQuery("simple", "deposit"))));
    }

    [Fact]
    public async Task A_second_run_processes_nothing_and_calls_no_api()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        await using var db = world.NewDb();
        await source.IngestAsync(db, world.Embedder);
        var calls = world.Embeddings.Requests.Count;

        var second = await source.IngestAsync(db, world.Embedder);

        Assert.Equal((3, 3, 0, 0, 0, 0L), (second.Documents, second.Skipped, second.Processed, second.Deleted, second.Chunks, second.EmbeddingTokens));
        Assert.Equal(calls, world.Embeddings.Requests.Count);
        Assert.Equal(4, await db.KnowledgeChunks.CountAsync());
    }

    [Fact]
    public async Task A_changed_document_is_re_embedded_alone_and_its_old_chunks_are_replaced()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        await using var db = world.NewDb();
        await source.IngestAsync(db, world.Embedder);
        var before = (await db.KnowledgeChunks.AsNoTracking().Where(c => c.Document!.Slug == "pets").ToListAsync()).Select(c => c.Id).ToHashSet();
        world.Embeddings.Requests.Clear();

        source.Put(Niche.RealEstate, "pets", "Pets policy", "## Dogs\n\nNo dogs allowed any more.");
        var report = await source.IngestAsync(db, world.Embedder);

        Assert.Equal((3, 2, 1, 1), (report.Documents, report.Skipped, report.Processed, report.Chunks));
        var sent = Assert.Single(world.Embeddings.Requests);
        Assert.Single(sent);
        Assert.Contains("No dogs allowed any more", sent[0]);

        await using var check = world.NewDb();
        var after = await check.KnowledgeChunks.Where(c => c.Document!.Slug == "pets").ToListAsync();
        var chunk = Assert.Single(after);
        Assert.Contains("No dogs allowed", chunk.Text);
        Assert.DoesNotContain(chunk.Id, before);
        Assert.Equal(1, await check.KnowledgeDocuments.CountAsync(d => d.Slug == "pets")); // updated in place, not duplicated
        Assert.Equal(3, await check.KnowledgeDocuments.CountAsync());
    }

    [Fact]
    public async Task Changing_the_embedding_model_re_embeds_everything_and_never_mixes_models()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        await using var db = world.NewDb();
        await source.IngestAsync(db, world.Embedder);
        var newModel = TestKnowledge.NewEmbedder(world.Embeddings, TestKnowledge.EmbeddingSettings("another-embedding-model"));

        var report = await source.IngestAsync(db, newModel);

        Assert.Equal((3, 0, 3), (report.Documents, report.Skipped, report.Processed));
        await using var check = world.NewDb();
        Assert.Equal(["another-embedding-model"], await check.KnowledgeChunks.Select(c => c.EmbeddingModel).Distinct().ToListAsync());
        Assert.Equal(4, await check.KnowledgeChunks.CountAsync());
    }

    [Fact]
    public async Task Documents_that_are_no_longer_in_the_source_are_deleted_with_their_chunks()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        await using var db = world.NewDb();
        await source.IngestAsync(db, world.Embedder);

        source.Remove(Niche.RealEstate, "pets");
        var report = await source.IngestAsync(db, world.Embedder);

        Assert.Equal((2, 2, 0, 1), (report.Documents, report.Skipped, report.Processed, report.Deleted));
        await using var check = world.NewDb();
        Assert.Equal(["allergens", "visits"], (await check.KnowledgeDocuments.Select(d => d.Slug).ToListAsync()).Order());
        Assert.Equal(2, await check.KnowledgeChunks.CountAsync());
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_previous_version_of_the_document()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        await using var db = world.NewDb();
        await source.IngestAsync(db, world.Embedder);

        source.Put(Niche.RealEstate, "visits", "Visit cancellations", "A completely new text.");
        world.Embeddings.Failure = _ => new HttpRequestException("boom", null, HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<EmbeddingException>(() => source.IngestAsync(db, world.Embedder));

        await using var check = world.NewDb();
        var chunk = await check.KnowledgeChunks.SingleAsync(c => c.Document!.Slug == "visits");
        Assert.Contains("cancelled up to 24 hours", chunk.Text);

        // Once the provider is back, the next run picks the change up.
        world.Embeddings.Failure = null;
        var retry = await source.IngestAsync(db, world.Embedder);
        Assert.Equal(1, retry.Processed);
    }

    [Fact]
    public async Task A_malformed_document_stops_the_run_before_anything_is_embedded_or_written()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        source.Files.Add(new KnowledgeFile(Niche.RealEstate, "realestate/broken.md", "no frontmatter at all"));
        source.Files.Add(new KnowledgeFile(Niche.Restaurant, "restaurant/worse.md", "---\ntitle: T\nlanguage: fr\n---\nBody"));
        await using var db = world.NewDb();

        var error = await Assert.ThrowsAsync<KnowledgeFormatException>(() => source.IngestAsync(db, world.Embedder));

        Assert.Contains("2 problem(s)", error.Message);
        Assert.Contains("realestate/broken.md", error.Message);
        Assert.Contains("restaurant/worse.md", error.Message);
        Assert.Empty(world.Embeddings.Requests);
        Assert.Equal(0, await db.KnowledgeDocuments.CountAsync());
    }

    [Fact]
    public async Task Two_files_with_the_same_slug_in_one_business_are_rejected()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var source = Source();
        source.Files.Add(new KnowledgeFile(Niche.RealEstate, "realestate/sub/pets.md", "---\ntitle: T\nlanguage: en\n---\nBody"));
        await using var db = world.NewDb();

        var error = await Assert.ThrowsAsync<KnowledgeFormatException>(() => source.IngestAsync(db, world.Embedder));

        Assert.Contains("slug 'pets'", error.Message);
    }

    [Fact]
    public async Task A_niche_without_a_business_in_the_database_asks_for_the_seed()
    {
        var world = await ToolWorld.CreateAsync(postgres); // has no clinic
        var source = new FakeKnowledgeSource().Put(Niche.Clinic, "x", "X", "Text");
        await using var db = world.NewDb();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.IngestAsync(db, world.Embedder));

        Assert.Contains("seeder", error.Message);
    }

    [Fact]
    public async Task The_cost_of_a_run_uses_the_price_of_the_embedding_model_when_there_is_one()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var db = world.NewDb();
        db.ModelPricings.Add(new ModelPricing { Model = TestKnowledge.Model, InputPerMillionUsd = 100m });
        await db.SaveChangesAsync();

        var report = await Source().IngestAsync(db, world.Embedder);

        Assert.Equal(report.EmbeddingTokens * 100m / 1_000_000m, report.CostUsd);
        Assert.True(report.CostUsd > 0);
    }

    [Fact]
    public void The_embedded_documents_of_the_product_are_readable_when_there_are_any()
    {
        // Whatever documents are compiled in must at least parse and have unique slugs per niche; with none this is vacuous.
        var files = new EmbeddedKnowledgeSource().GetFiles();

        foreach (var file in files)
            KnowledgeDocumentParser.Parse(file.SourcePath, file.Content);
        Assert.Equal(files.Count, files.Select(f => (f.Niche, f.Slug)).Distinct().Count());
    }
}
