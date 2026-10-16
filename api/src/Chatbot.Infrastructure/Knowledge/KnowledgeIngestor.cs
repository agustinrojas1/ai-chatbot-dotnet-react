using System.Security.Cryptography;
using System.Text;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;
using Chatbot.Core.Knowledge;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Chatbot.Infrastructure.Knowledge;

/// <summary>What one ingestion did. <c>Documents</c> = <c>Skipped</c> + <c>Processed</c> (the ones deleted were not in the source).</summary>
public sealed record IngestionReport(
    int Documents, int Skipped, int Processed, int Deleted, int Chunks, long EmbeddingTokens, bool TokensEstimated, decimal CostUsd);

/// <summary>
/// Indexes the knowledge documents into <c>knowledge_documents</c>/<c>knowledge_chunks</c> (SPEC 7.6). Idempotent: a document
/// is skipped when its hash (content, embedding model, dimensions, document prefix and chunker) is unchanged, so a second run calls no API.
/// Changing the model or the dimensions changes every hash, so everything is re-embedded and embeddings of two models never
/// coexist. The chunks of a document are replaced in one transaction, after the embeddings were obtained, so a provider
/// failure leaves the previous version of the document untouched. Documents that disappeared from the source are deleted.
/// </summary>
public sealed class KnowledgeIngestor(
    ChatbotDbContext db,
    IKnowledgeSource source,
    Embedder embedder,
    TimeProvider clock,
    ILogger<KnowledgeIngestor> logger)
{
    private static readonly ChunkerOptions ChunkerSettings = ChunkerOptions.Default;

    public async Task<IngestionReport> IngestAsync(CancellationToken cancellationToken)
    {
        var documents = ParseAll(source.GetFiles());

        var businesses = await db.Businesses.AsNoTracking().ToDictionaryAsync(b => b.Niche, b => b.Id, cancellationToken);
        var missing = documents.Select(d => d.File.Niche).Distinct().Where(n => !businesses.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"No business for {string.Join(", ", missing)}: run the database seeder before ingesting.");

        var pricing = await db.ModelPricings.AsNoTracking().FirstOrDefaultAsync(p => p.Model == embedder.Model, cancellationToken);
        var existing = (await db.KnowledgeDocuments.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(d => (d.BusinessId, d.SourcePath));

        var skipped = 0;
        var processed = 0;
        var chunkTotal = 0;
        long tokens = 0;
        var estimated = false;

        // Documents are short (2-4 chunks), so their chunks are embedded together: one request per BatchSize chunks instead of
        // one per document. A group is written right after it is embedded, so a failure loses at most the group in flight.
        var pending = new List<Pending>();

        async Task FlushAsync()
        {
            if (pending.Count == 0)
                return;

            var embedded = await embedder.EmbedDocumentsAsync(pending.SelectMany(p => p.Chunks.Select(c => c.Text)), cancellationToken);
            var offset = 0;
            foreach (var item in pending)
            {
                var vectors = embedded.Vectors.Skip(offset).Take(item.Chunks.Count).ToList();
                offset += item.Chunks.Count;
                await ReplaceAsync(item.BusinessId, item.Parsed, item.Hash, item.Chunks, vectors, item.DocumentId, cancellationToken);
                logger.LogInformation("Indexed {SourcePath}: {Chunks} chunks.", item.Parsed.File.SourcePath, item.Chunks.Count);
            }

            processed += pending.Count;
            chunkTotal += offset;
            tokens += embedded.Tokens;
            estimated |= embedded.TokensEstimated;
            pending.Clear();
        }

        foreach (var parsed in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var businessId = businesses[parsed.File.Niche];
            var hash = Hash(parsed.NormalizedContent);
            existing.TryGetValue((businessId, parsed.File.SourcePath), out var current);

            if (current?.ContentHash == hash)
            {
                skipped++;
                continue;
            }

            pending.Add(new Pending(businessId, parsed, hash, Chunker.Split(parsed.Document.Title, parsed.Document.Body, ChunkerSettings), current?.Id));
            if (pending.Sum(p => p.Chunks.Count) >= embedder.BatchSize)
                await FlushAsync();
        }

        await FlushAsync();

        var deleted = await DeleteMissingAsync(documents, businesses, cancellationToken);

        var cost = CostCalculator.Compute(pricing, tokens, 0);
        var report = new IngestionReport(documents.Count, skipped, processed, deleted, chunkTotal, tokens, estimated, cost);
        logger.LogInformation(
            "Knowledge ingestion finished: {Documents} documents ({Processed} indexed, {Skipped} unchanged, {Deleted} deleted), {Chunks} chunks, " +
            "{Tokens} embedding tokens{Estimated}, ${Cost:0.000000} at {Model} rates.",
            report.Documents, report.Processed, report.Skipped, report.Deleted, report.Chunks, report.EmbeddingTokens,
            report.TokensEstimated ? " (estimated)" : "", report.CostUsd, embedder.Model);
        return report;
    }

    private sealed record Pending(Guid BusinessId, Parsed Parsed, string Hash, IReadOnlyList<KnowledgeChunkDraft> Chunks, Guid? DocumentId);

    private sealed record Parsed(KnowledgeFile File, string NormalizedContent, ParsedKnowledgeDocument Document);

    /// <summary>Every file is checked before anything is embedded, so a malformed document stops the run early and the message lists all of them.</summary>
    private static List<Parsed> ParseAll(IReadOnlyList<KnowledgeFile> files)
    {
        var parsed = new List<Parsed>(files.Count);
        var errors = new List<string>();
        foreach (var file in files.OrderBy(f => f.SourcePath, StringComparer.Ordinal))
        {
            try
            {
                parsed.Add(new Parsed(file, KnowledgeDocumentParser.Normalize(file.Content).TrimEnd(), KnowledgeDocumentParser.Parse(file.SourcePath, file.Content)));
            }
            catch (KnowledgeFormatException ex)
            {
                errors.Add(ex.Message);
            }
        }

        var duplicates = parsed.GroupBy(p => (p.File.Niche, p.File.Slug)).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Niche}: slug '{g.Key.Slug}' is used by {string.Join(", ", g.Select(p => p.File.SourcePath))}");
        errors.AddRange(duplicates);

        if (errors.Count > 0)
            throw new KnowledgeFormatException("knowledge", $"{errors.Count} problem(s):\n" + string.Join("\n", errors.Select(e => "  - " + e)));
        return parsed;
    }

    private string Hash(string normalizedContent)
    {
        var input = $"{embedder.Model}\n{embedder.Dimensions}\n{embedder.DocumentPrefix}\n{ChunkerSettings.Signature}\n{normalizedContent}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>Deletes the old chunks of a document and inserts the new ones (and the document row) in one transaction.</summary>
    private async Task ReplaceAsync(
        Guid businessId, Parsed parsed, string hash, IReadOnlyList<KnowledgeChunkDraft> chunks, IReadOnlyList<float[]> vectors,
        Guid? documentId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var document = documentId is { } id
            ? await db.KnowledgeDocuments.FirstAsync(d => d.Id == id, ct)
            : db.KnowledgeDocuments.Add(new KnowledgeDocument { BusinessId = businessId, SourcePath = parsed.File.SourcePath }).Entity;

        document.Slug = parsed.File.Slug;
        document.Title = parsed.Document.Title;
        document.Language = parsed.Document.Language;
        document.Tags = [.. parsed.Document.Tags];
        document.ContentHash = hash;
        document.UpdatedAt = parsed.Document.Updated is { } updated
            ? new DateTimeOffset(updated.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : clock.GetUtcNow();

        if (documentId is not null)
            await db.KnowledgeChunks.Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(ct);

        db.KnowledgeChunks.AddRange(chunks.Select((chunk, i) => new KnowledgeChunk
        {
            DocumentId = document.Id,
            BusinessId = businessId,
            Ordinal = chunk.Ordinal,
            Heading = chunk.Heading,
            Text = chunk.Text,
            TokenCount = chunk.TokenCount,
            Embedding = new Vector(vectors[i]),
            EmbeddingModel = embedder.Model,
        }));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<int> DeleteMissingAsync(List<Parsed> documents, Dictionary<Niche, Guid> businesses, CancellationToken ct)
    {
        var present = documents.Select(d => (BusinessId: businesses[d.File.Niche], d.File.SourcePath)).ToHashSet();
        var stored = await db.KnowledgeDocuments.AsNoTracking().Select(d => new { d.Id, d.BusinessId, d.SourcePath }).ToListAsync(ct);
        var stale = stored.Where(d => !present.Contains((d.BusinessId, d.SourcePath))).ToList();
        if (stale.Count == 0)
            return 0;

        var ids = stale.Select(d => d.Id).ToList();
        await db.KnowledgeDocuments.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(ct); // chunks cascade
        foreach (var gone in stale)
            logger.LogInformation("Deleted {SourcePath}: it is no longer in the knowledge folder.", gone.SourcePath);
        return stale.Count;
    }
}
