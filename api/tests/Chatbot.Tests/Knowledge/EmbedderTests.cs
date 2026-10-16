using System.Net;
using Chatbot.Infrastructure.Knowledge;
using Microsoft.Extensions.AI;

namespace Chatbot.Tests.Knowledge;

public class EmbedderTests
{
    private static HttpRequestException Status(HttpStatusCode code) => new("provider said no", null, code);

    [Fact]
    public async Task Texts_are_sent_in_batches_of_the_configured_size_and_come_back_in_order()
    {
        var generator = new FakeEmbeddingGenerator();
        var embedder = TestKnowledge.NewEmbedder(generator, TestKnowledge.EmbeddingSettings());
        var texts = Enumerable.Range(0, 70).Select(i => $"text number{i}").ToList();

        var batch = await embedder.EmbedAsync(texts, CancellationToken.None);

        Assert.Equal([32, 32, 6], generator.Requests.Select(r => r.Count));
        Assert.Equal(70, batch.Vectors.Count);
        Assert.Equal(generator.Vectorize("text number41"), batch.Vectors[41]); // the 42nd text is the 10th of the second batch
        Assert.Equal(140, batch.Tokens); // the fake reports one token per word
        Assert.False(batch.TokensEstimated);
    }

    [Fact]
    public async Task The_requested_dimensions_and_model_are_passed_to_the_provider()
    {
        var generator = new FakeEmbeddingGenerator();

        await TestKnowledge.NewEmbedder(generator).EmbedAsync(["hello"], CancellationToken.None);

        Assert.Equal(KnowledgeSchema.EmbeddingDimensions, generator.LastOptions?.Dimensions);
        Assert.Equal(TestKnowledge.Model, generator.LastOptions?.ModelId);
    }

    [Fact]
    public async Task Queries_and_documents_get_their_own_task_prefix()
    {
        var generator = new FakeEmbeddingGenerator();
        var options = TestKnowledge.EmbeddingSettings();
        options.QueryPrefix = "task: search result | query: ";
        options.DocumentPrefix = "title: none | text: ";
        var embedder = TestKnowledge.NewEmbedder(generator, options);

        await embedder.EmbedQueryAsync("parking", CancellationToken.None);
        await embedder.EmbedDocumentsAsync(["Parking > Hours", "Parking > Price"], CancellationToken.None);

        Assert.Equal(["task: search result | query: parking"], generator.Requests[0]);
        Assert.Equal(["title: none | text: Parking > Hours", "title: none | text: Parking > Price"], generator.Requests[1]);
    }

    [Fact]
    public async Task Without_usage_from_the_provider_tokens_are_estimated_and_flagged()
    {
        var generator = new FakeEmbeddingGenerator { ReportUsage = false };

        var batch = await TestKnowledge.NewEmbedder(generator).EmbedAsync([new string('a', 40), new string('b', 8)], CancellationToken.None);

        Assert.Equal(12, batch.Tokens); // 4 characters per token
        Assert.True(batch.TokensEstimated);
    }

    [Fact]
    public async Task A_429_is_retried_with_backoff_until_it_succeeds()
    {
        var generator = new FakeEmbeddingGenerator { Failure = call => call < 2 ? Status(HttpStatusCode.TooManyRequests) : null };

        var batch = await TestKnowledge.NewEmbedder(generator).EmbedAsync(["hello"], CancellationToken.None);

        Assert.Single(batch.Vectors);
        Assert.Equal(3, generator.Requests.Count); // two failures and the success
    }

    [Fact]
    public async Task After_the_retries_are_exhausted_the_failure_is_an_embedding_error_without_the_provider_message()
    {
        var generator = new FakeEmbeddingGenerator { Failure = _ => Status(HttpStatusCode.TooManyRequests) };
        var options = TestKnowledge.EmbeddingSettings();
        options.MaxRetries = 2;

        var error = await Assert.ThrowsAsync<EmbeddingException>(() => TestKnowledge.NewEmbedder(generator, options).EmbedAsync(["hello"], CancellationToken.None));

        Assert.Equal(3, generator.Requests.Count);
        Assert.Contains("429", error.Message);
        Assert.DoesNotContain("provider said no", error.Message);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        var generator = new FakeEmbeddingGenerator { Failure = _ => Status(HttpStatusCode.BadRequest) };

        await Assert.ThrowsAsync<EmbeddingException>(() => TestKnowledge.NewEmbedder(generator).EmbedAsync(["hello"], CancellationToken.None));

        Assert.Single(generator.Requests);
    }

    [Fact]
    public async Task Vectors_of_the_wrong_size_are_rejected_with_the_setting_to_fix()
    {
        var generator = new FakeEmbeddingGenerator(dimensions: 3072);

        var error = await Assert.ThrowsAsync<EmbeddingException>(() => TestKnowledge.NewEmbedder(generator).EmbedAsync(["hello"], CancellationToken.None));

        Assert.Contains("3072", error.Message);
        Assert.Contains("Embeddings:Dimensions", error.Message);
    }

    [Fact]
    public async Task Without_an_api_key_the_generator_fails_on_first_use_with_an_actionable_message()
    {
        var generator = EmbeddingClientFactory.Create(new EmbeddingOptions());
        var embedder = new Embedder(generator, Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Embedder>.Instance);

        var error = await Assert.ThrowsAsync<EmbeddingsNotConfiguredException>(() => embedder.EmbedAsync(["hello"], CancellationToken.None));

        Assert.Contains("Embeddings:ApiKey", error.Message);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_or_retried()
    {
        var generator = new FakeEmbeddingGenerator { Failure = _ => new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => TestKnowledge.NewEmbedder(generator).EmbedAsync(["hello"], CancellationToken.None));

        Assert.Single(generator.Requests);
    }
}
