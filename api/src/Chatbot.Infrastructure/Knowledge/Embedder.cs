using Chatbot.Core.Knowledge;
using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chatbot.Infrastructure.Knowledge;

/// <summary>Embeddings could not be produced. The message is safe to log; it never carries request text.</summary>
public class EmbeddingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>No API key for the embeddings provider. A deployment problem: it fails on first use, not at startup.</summary>
public sealed class EmbeddingsNotConfiguredException()
    : EmbeddingException("Embeddings are not configured: set Embeddings:ApiKey and Embeddings:BaseUrl (they default to Llm:Primary).");

/// <summary>Vectors for a list of texts, in order, plus the tokens the provider billed for them.</summary>
/// <param name="TokensEstimated">True when the provider reported no usage and <paramref name="Tokens"/> is an estimate (4 characters per token).</param>
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, long Tokens, bool TokensEstimated);

/// <summary>
/// Turns texts into vectors with the keyed <c>embeddings</c> <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>: requests of at
/// most <see cref="EmbeddingOptions.BatchSize"/> texts, a retry with exponential backoff on 429 and 5xx (the free tier limits
/// requests and tokens per minute, so a long ingestion waits instead of failing), and a check that every vector has the
/// size the database column expects. One instance for the whole app: it keeps no per-request state.
/// </summary>
public sealed class Embedder(
    [FromKeyedServices(EmbeddingClientFactory.ServiceKey)] IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<EmbeddingOptions> options,
    ILogger<Embedder> logger)
{
    public string Model => options.Value.Model;
    public int Dimensions => options.Value.Dimensions;
    public int BatchSize => options.Value.BatchSize;
    public bool IsConfigured => options.Value.IsConfigured;
    public string DocumentPrefix => options.Value.DocumentPrefix;

    /// <summary>Embeds a search query, with <see cref="EmbeddingOptions.QueryPrefix"/>.</summary>
    public Task<EmbeddingBatch> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
        EmbedAsync([options.Value.QueryPrefix + query], cancellationToken);

    /// <summary>Embeds chunks to index, each with <see cref="EmbeddingOptions.DocumentPrefix"/>.</summary>
    public Task<EmbeddingBatch> EmbedDocumentsAsync(IEnumerable<string> texts, CancellationToken cancellationToken) =>
        EmbedAsync([.. texts.Select(t => options.Value.DocumentPrefix + t)], cancellationToken);

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var vectors = new List<float[]>(texts.Count);
        long tokens = 0;
        var anyEstimated = false;

        for (var start = 0; start < texts.Count; start += settings.BatchSize)
        {
            var batch = texts.Skip(start).Take(settings.BatchSize).ToList();
            var generated = await GenerateWithRetryAsync(batch, settings, cancellationToken);

            if (generated.Count != batch.Count)
                throw new EmbeddingException($"The provider returned {generated.Count} embeddings for {batch.Count} texts.");

            foreach (var embedding in generated)
            {
                if (embedding.Vector.Length != settings.Dimensions)
                {
                    throw new EmbeddingException(
                        $"The provider returned vectors of {embedding.Vector.Length} dimensions but Embeddings:Dimensions is {settings.Dimensions}. " +
                        "Check that the model supports the 'dimensions' parameter.");
                }

                vectors.Add(embedding.Vector.ToArray());
            }

            if (generated.Usage is { } usage && (usage.InputTokenCount ?? usage.TotalTokenCount) is { } reported)
            {
                tokens += reported;
            }
            else
            {
                tokens += batch.Sum(Chunker.EstimateTokens);
                anyEstimated = true;
            }
        }

        return new EmbeddingBatch(vectors, tokens, anyEstimated);
    }

    private async Task<GeneratedEmbeddings<Embedding<float>>> GenerateWithRetryAsync(
        List<string> batch, EmbeddingOptions settings, CancellationToken cancellationToken)
    {
        var request = new EmbeddingGenerationOptions { Dimensions = settings.Dimensions, ModelId = settings.Model };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await generator.GenerateAsync(batch, request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EmbeddingException)
            {
                throw;
            }
            catch (Exception ex) when (FallbackChatClient.IsRetryable(ex) && attempt < settings.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(settings.MaxRetryDelaySeconds, settings.RetryBaseDelaySeconds * Math.Pow(2, attempt)));
                logger.LogWarning("Embeddings provider rate limited or failed (attempt {Attempt} of {Max}); retrying in {Delay}.",
                    attempt + 1, settings.MaxRetries + 1, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                // The message of a provider error can echo the request; callers only see the type and status.
                throw new EmbeddingException($"The embeddings request failed ({DescribeFailure(ex)}).", ex);
            }
        }
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        System.ClientModel.ClientResultException e => $"{e.GetType().Name}, HTTP {e.Status}",
        HttpRequestException e => $"{e.GetType().Name}, HTTP {(int?)e.StatusCode}",
        _ => ex.GetType().Name,
    };
}
