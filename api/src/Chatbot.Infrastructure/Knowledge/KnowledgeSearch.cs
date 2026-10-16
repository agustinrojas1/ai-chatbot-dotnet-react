using System.Diagnostics;
using Chatbot.Core.Knowledge;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Knowledge;

/// <summary>One candidate chunk with the three scores that explain why it was returned.</summary>
/// <param name="SemanticScore">Cosine similarity, or null when the chunk was not among the semantic top <see cref="KnowledgeSearch.CandidatesPerRanking"/>.</param>
/// <param name="LexicalScore"><c>ts_rank</c>, or null when the chunk was not among the lexical top <see cref="KnowledgeSearch.CandidatesPerRanking"/>.</param>
/// <param name="FinalScore">The Reciprocal Rank Fusion score. It only orders chunks: it is not a probability.</param>
/// <param name="AboveThreshold">This chunk's own semantic similarity or lexical rank reaches its threshold.</param>
public sealed record KnowledgePassage(
    Guid ChunkId, string SourceId, string Title, string Heading, string Text,
    double? SemanticScore, double? LexicalScore, double FinalScore, bool AboveThreshold);

/// <summary>
/// The outcome of a search. <see cref="Passages"/> are the best candidates (at most <see cref="KnowledgeSearch.MaxPassages"/>)
/// whether or not they pass; <see cref="ThresholdPassed"/> says if at least one does, and only those are given to the model.
/// </summary>
public sealed record KnowledgeSearchResult(
    string Query, bool ThresholdPassed, double SemanticThreshold, double LexicalThreshold, IReadOnlyList<KnowledgePassage> Passages,
    long EmbeddingTokens, bool EmbeddingTokensEstimated, string EmbeddingModel, int DurationMs)
{
    /// <summary>The passages the model may use.</summary>
    public IEnumerable<KnowledgePassage> Relevant => Passages.Where(p => p.AboveThreshold);
}

/// <summary>
/// Hybrid retrieval over the knowledge base of ONE business (SPEC 7.6): the query is embedded; the semantic top 20 (cosine
/// distance, HNSW) and the lexical top 20 (full-text search, <c>simple</c> configuration) are fused with Reciprocal Rank
/// Fusion (k = 60) and the best 4 are returned with their scores. Both queries filter by <c>BusinessId</c> in SQL, and the
/// semantic one also by the current embedding model, so vectors of another model are never compared. The two queries run one
/// after the other because they share the request's <see cref="ChatbotDbContext"/>; each takes a few milliseconds.
/// </summary>
public sealed class KnowledgeSearch(
    ChatbotDbContext db, Embedder embedder, IOptions<KnowledgeOptions> options)
{
    public const int CandidatesPerRanking = 20;
    public const int MaxPassages = 4;

    public async Task<KnowledgeSearchResult> SearchAsync(Guid businessId, string query, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var started = Stopwatch.GetTimestamp();

        var embedded = await embedder.EmbedQueryAsync(query, cancellationToken);
        var vector = new Vector(embedded.Vectors[0]);
        var model = embedder.Model;

        var semantic = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.EmbeddingModel == model)
            .Select(c => new { c.Id, Distance = c.Embedding.CosineDistance(vector) })
            .OrderBy(x => x.Distance).ThenBy(x => x.Id)
            .Take(CandidatesPerRanking)
            .ToListAsync(cancellationToken);

        var lexical = new List<(Guid Id, double Rank)>();
        if (LexicalQuery.Build(query) is { } lexicalText)
        {
            var rows = await db.KnowledgeChunks.AsNoTracking()
                .Where(c => c.BusinessId == businessId && c.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", lexicalText)))
                .Select(c => new { c.Id, Rank = c.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", lexicalText)) })
                .OrderByDescending(x => x.Rank).ThenBy(x => x.Id)
                .Take(CandidatesPerRanking)
                .ToListAsync(cancellationToken);
            lexical.AddRange(rows.Select(r => (r.Id, (double)r.Rank)));
        }

        var semanticScores = semantic.ToDictionary(x => x.Id, x => 1 - x.Distance);
        var lexicalScores = lexical.ToDictionary(x => x.Id, x => x.Rank);

        var fused = ReciprocalRankFusion.Fuse<Guid>([[.. semantic.Select(x => x.Id)], [.. lexical.Select(x => x.Id)]])
            .Take(MaxPassages)
            .ToList();

        var ids = fused.Select(f => f.Key).ToList();
        var chunks = ids.Count == 0
            ? []
            : await db.KnowledgeChunks.AsNoTracking()
                .Where(c => c.BusinessId == businessId && ids.Contains(c.Id))
                .Select(c => new { c.Id, c.Ordinal, c.Heading, c.Text, c.Document!.Slug, c.Document.Title })
                .ToDictionaryAsync(c => c.Id, cancellationToken);

        var passages = fused.Select(hit =>
        {
            var chunk = chunks[hit.Key];
            double? sem = semanticScores.TryGetValue(hit.Key, out var s) ? s : null;
            double? lex = lexicalScores.TryGetValue(hit.Key, out var l) ? l : null;
            return new KnowledgePassage(
                hit.Key, KnowledgeChunk.SourceIdOf(chunk.Slug, chunk.Ordinal), chunk.Title, chunk.Heading, chunk.Text,
                sem, lex, hit.Score,
                AboveThreshold: sem >= settings.MinSemanticSimilarity || lex >= settings.MinLexicalRank);
        }).ToList();

        return new KnowledgeSearchResult(
            query, passages.Any(p => p.AboveThreshold), settings.MinSemanticSimilarity, settings.MinLexicalRank, passages,
            embedded.Tokens, embedded.TokensEstimated, model,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
