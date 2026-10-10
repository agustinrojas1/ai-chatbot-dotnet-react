namespace Chatbot.Infrastructure.Knowledge;

/// <summary>
/// The embeddings provider, bound from the <c>Embeddings</c> section. <c>BaseUrl</c> and <c>ApiKey</c> default to
/// <c>Llm:Primary</c> (Gemini serves both on one key), <c>Model</c> to <see cref="DefaultModel"/>.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";
    public const string DefaultModel = "gemini-embedding-001";

    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// Size of the vectors requested from the provider (Matryoshka truncation: <c>gemini-embedding-001</c> natively returns 3072).
    /// It must equal <see cref="KnowledgeSchema.EmbeddingDimensions"/>, the size of the database column: changing it needs a
    /// migration and a full re-index (ADR-017). pgvector indexes (HNSW) accept at most 2000 dimensions.
    /// </summary>
    public int Dimensions { get; set; } = KnowledgeSchema.EmbeddingDimensions;

    /// <summary>Texts per embeddings request when indexing.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Retries of a request that answers 429 or 5xx, with exponential backoff (free tiers rate limit by minute).</summary>
    public int MaxRetries { get; set; } = 6;

    /// <summary>First wait before a retry; it doubles each time and never exceeds <see cref="MaxRetryDelaySeconds"/>.</summary>
    public double RetryBaseDelaySeconds { get; set; } = 2;

    public double MaxRetryDelaySeconds { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);

    public static bool IsValid(EmbeddingOptions o) =>
        o.Dimensions == KnowledgeSchema.EmbeddingDimensions && o.BatchSize > 0 && o.MaxRetries >= 0
        && o.RetryBaseDelaySeconds >= 0 && o.MaxRetryDelaySeconds >= 0 && !string.IsNullOrWhiteSpace(o.Model);
}

/// <summary>Bound from the <c>Knowledge</c> section: ingestion switch and retrieval thresholds (SPEC 7.6).</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>Index the knowledge documents when the API starts. Null means "only in Development".</summary>
    public bool? AutoIngest { get; set; }

    /// <summary>
    /// Cosine similarity (1 = same direction) the best semantic hit must reach. Below it, and with no strong lexical hit either,
    /// the search answers "nothing relevant". 0.60 is the value measured on the real model (<c>gemini-embedding-001</c>, 768 d): the
    /// best hit of every answerable probe question scored 0.62 or more, and most unanswerable ones 0.45 to 0.58 (ADR-016). Retune
    /// it with the retrieval evals whenever the model, the dimensions or the documents change.
    /// </summary>
    public double MinSemanticSimilarity { get; set; } = 0.60;

    /// <summary>
    /// <c>ts_rank</c> the best lexical hit must reach to count as relevant without a good semantic hit (an exact amount, a phone
    /// number or an acronym). A lexical hit already contains EVERY content word of the question, so the bar is low: one matching
    /// word scores about 0.06 and each extra word raises it.
    /// </summary>
    public double MinLexicalRank { get; set; } = 0.05;

    public bool ResolveAutoIngest(bool isDevelopment) => AutoIngest ?? isDevelopment;

    public static bool IsValid(KnowledgeOptions o) =>
        o.MinSemanticSimilarity is >= -1 and <= 1 && o.MinLexicalRank >= 0;
}

/// <summary>Constants of the stored schema that a configuration value cannot change without a migration.</summary>
public static class KnowledgeSchema
{
    /// <summary>Size of <c>knowledge_chunks.Embedding</c> (<c>vector(768)</c>). See <see cref="EmbeddingOptions.Dimensions"/>.</summary>
    public const int EmbeddingDimensions = 768;
}
