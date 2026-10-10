using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Infrastructure.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chatbot.Tests.Knowledge;

/// <summary>
/// Deterministic stand-in for the embeddings provider: every word is hashed (SHA-256, so it is stable across runs) to one of
/// the vector dimensions and the vector is normalized. Texts that share words are similar, texts that share none are orthogonal.
/// It records what it was asked, and can fail on demand.
/// </summary>
internal sealed partial class FakeEmbeddingGenerator(int dimensions = KnowledgeSchema.EmbeddingDimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The texts of every request, in order.</summary>
    public List<IReadOnlyList<string>> Requests { get; } = [];

    public int TextsEmbedded => Requests.Sum(r => r.Count);

    /// <summary>Given the zero-based request number, an exception to throw instead of answering.</summary>
    public Func<int, Exception?>? Failure { get; set; }

    /// <summary>False: the response carries no usage, so the embedder has to estimate.</summary>
    public bool ReportUsage { get; set; } = true;

    public EmbeddingGenerationOptions? LastOptions { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var call = Requests.Count;
        Requests.Add(texts);
        LastOptions = options;
        if (Failure?.Invoke(call) is { } failure)
            throw failure;

        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Select(t => new Embedding<float>(Vectorize(t))));
        if (ReportUsage)
        {
            var words = texts.Sum(t => Word().Matches(t).Count);
            result.Usage = new UsageDetails { InputTokenCount = words, TotalTokenCount = words };
        }

        return Task.FromResult(result);
    }

    public float[] Vectorize(string text)
    {
        var vector = new float[dimensions];
        foreach (Match word in Word().Matches(text.ToLowerInvariant()))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word.Value));
            vector[(int)(BitConverter.ToUInt32(hash, 0) % (uint)dimensions)] += 1f;
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

/// <summary>An in-memory knowledge folder, so a test controls exactly which documents exist.</summary>
internal sealed class FakeKnowledgeSource : IKnowledgeSource
{
    public List<KnowledgeFile> Files { get; } = [];

    public IReadOnlyList<KnowledgeFile> GetFiles() => [.. Files];

    public FakeKnowledgeSource Put(Niche niche, string name, string title, string body, string language = "en")
    {
        Files.RemoveAll(f => f.Niche == niche && f.SourcePath.EndsWith("/" + name + ".md", StringComparison.Ordinal));
        var folder = EmbeddedKnowledgeSource.Folders.First(f => f.Value == niche).Key;
        Files.Add(new KnowledgeFile(niche, $"{folder}/{name}.md", $"---\ntitle: {title}\nlanguage: {language}\ntags: [test]\nupdated: 2026-09-01\n---\n\n{body}\n"));
        return this;
    }

    public FakeKnowledgeSource Remove(Niche niche, string name)
    {
        Files.RemoveAll(f => f.Niche == niche && f.SourcePath.EndsWith("/" + name + ".md", StringComparison.Ordinal));
        return this;
    }
}

internal static class TestKnowledge
{
    public const string Model = "fake-embedding-001";

    public static EmbeddingOptions EmbeddingSettings(string model = Model) => new()
    {
        ApiKey = "test", BaseUrl = "https://example.invalid/", Model = model, RetryBaseDelaySeconds = 0, MaxRetryDelaySeconds = 0,
    };

    public static Embedder NewEmbedder(FakeEmbeddingGenerator generator, EmbeddingOptions? options = null) =>
        new(generator, Options.Create(options ?? EmbeddingSettings()), NullLogger<Embedder>.Instance);

    /// <summary>Thresholds suited to the bag-of-words fake: its cosines are far lower than those of a real model.</summary>
    public static KnowledgeOptions SearchSettings(double minSemantic = 0.3, double minLexical = 0.05) =>
        new() { MinSemanticSimilarity = minSemantic, MinLexicalRank = minLexical };

    public static KnowledgeSearch NewSearch(ChatbotDbContext db, Embedder embedder, KnowledgeOptions? options = null) =>
        new(db, embedder, Options.Create(options ?? SearchSettings()));

    public static KnowledgeIngestor NewIngestor(ChatbotDbContext db, IKnowledgeSource source, Embedder embedder) =>
        new(db, source, embedder, TimeProvider.System, NullLogger<KnowledgeIngestor>.Instance);
}

internal static class TestKnowledgeExtensions
{
    /// <summary>Indexes <paramref name="source"/> into the database of <paramref name="db"/> with its own context scope.</summary>
    public static async Task<IngestionReport> IngestAsync(this FakeKnowledgeSource source, ChatbotDbContext db, Embedder embedder) =>
        await TestKnowledge.NewIngestor(db, source, embedder).IngestAsync(CancellationToken.None);
}
