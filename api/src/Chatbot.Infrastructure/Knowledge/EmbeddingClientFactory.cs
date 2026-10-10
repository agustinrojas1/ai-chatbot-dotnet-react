using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using EmbeddingClient = OpenAI.Embeddings.EmbeddingClient; // OpenAI.Embeddings also defines EmbeddingGenerationOptions, which clashes with Microsoft.Extensions.AI
using Chatbot.Infrastructure.Llm;

namespace Chatbot.Infrastructure.Knowledge;

public static class EmbeddingClientFactory
{
    /// <summary>Service key of the <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> the <see cref="Embedder"/> uses.</summary>
    public const string ServiceKey = "embeddings";

    /// <summary>
    /// Binds <c>Embeddings</c> and <c>Knowledge</c>, and registers the generator, the <see cref="Embedder"/>, the search and the
    /// ingestion. Nothing is built until first use, so a missing key never stops the app from starting.
    /// </summary>
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .PostConfigure<IOptions<LlmOptions>>(UseLlmDefaults)
            .Validate(EmbeddingOptions.IsValid,
                $"Embeddings:Dimensions must be {KnowledgeSchema.EmbeddingDimensions} (the size of the knowledge_chunks.Embedding column; changing it needs a migration and a re-index), " +
                "and BatchSize, MaxRetries, the delays and Model must be valid.")
            .ValidateOnStart();
        services.AddOptions<KnowledgeOptions>()
            .Bind(configuration.GetSection(KnowledgeOptions.SectionName))
            .Validate(KnowledgeOptions.IsValid, "Knowledge:MinSemanticSimilarity must be between -1 and 1 and Knowledge:MinLexicalRank must not be negative.")
            .ValidateOnStart();

        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(ServiceKey, (sp, _) =>
            Create(sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value));
        services.AddSingleton<Embedder>();
        services.AddSingleton<IKnowledgeSource, EmbeddedKnowledgeSource>();
        services.AddScoped<KnowledgeSearch>();
        services.AddScoped<KnowledgeIngestor>();
        return services;
    }

    public static IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options)
    {
        // A missing key must not stop the app from starting (e.g. /health in CI): it fails on the first use instead.
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return new UnconfiguredEmbeddingGenerator();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl))
            throw new InvalidOperationException($"Embeddings BaseUrl '{options.BaseUrl}' is not a valid absolute URL.");

        // The Embedder retries with its own backoff, so the SDK's retries (which would multiply with ours) are off.
        var clientOptions = new OpenAIClientOptions { Endpoint = baseUrl, RetryPolicy = new ClientRetryPolicy(0) };
        return new EmbeddingClient(options.Model, new ApiKeyCredential(options.ApiKey), clientOptions).AsIEmbeddingGenerator();
    }

    private static void UseLlmDefaults(EmbeddingOptions options, IOptions<LlmOptions> llm)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            options.BaseUrl = llm.Value.Primary.BaseUrl;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            options.ApiKey = llm.Value.Primary.ApiKey;
        if (string.IsNullOrWhiteSpace(options.Model))
            options.Model = EmbeddingOptions.DefaultModel;
    }

    /// <summary>Stands in when there is no API key; every call fails with an actionable message.</summary>
    private sealed class UnconfiguredEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new EmbeddingsNotConfiguredException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
