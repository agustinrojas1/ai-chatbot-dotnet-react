using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Chatbot.Infrastructure.Knowledge;
using Microsoft.Extensions.AI;

namespace Chatbot.Evals.Running;

/// <summary>
/// The embeddings of <c>--fake</c>: deterministic and offline. Every word is hashed (SHA-256, stable across runs) to one of
/// the vector dimensions and the vector is normalized, so texts that share words are close and texts that share none are
/// orthogonal. It finds exact words, not meaning: a question in one language over a document in the other scores near zero.
/// That is what the real model is for; this one proves the harness runs.
/// </summary>
internal sealed partial class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string Model = "fake-embedding-001";

    /// <summary>The thresholds of <c>--fake</c>: a bag-of-words cosine is far lower than that of a real model.</summary>
    public const double MinSemanticSimilarity = 0.3;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Select(t => new Embedding<float>(Vectorize(t))));
        var words = texts.Sum(t => Word().Matches(t).Count);
        result.Usage = new UsageDetails { InputTokenCount = words, TotalTokenCount = words };
        return Task.FromResult(result);
    }

    private static float[] Vectorize(string text)
    {
        var vector = new float[KnowledgeSchema.EmbeddingDimensions];
        foreach (Match word in Word().Matches(text.ToLowerInvariant()))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word.Value));
            vector[(int)(BitConverter.ToUInt32(hash, 0) % (uint)vector.Length)] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm == 0)
            vector[0] = 1f;
        else
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
