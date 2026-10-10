using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chatbot.Infrastructure.Knowledge;

/// <summary>The two ways the knowledge base gets indexed outside tests: the <c>ingest</c> command and the Development startup.</summary>
public static class KnowledgeStartup
{
    /// <summary>
    /// The <c>ingest</c> command: applies the migrations, runs the idempotent seed (the businesses and the pricing must exist),
    /// then indexes the documents. Throws on any failure, so the caller can exit with a non-zero code.
    /// </summary>
    public static async Task<IngestionReport> RunIngestCommandAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatbotDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>().SeedAsync(cancellationToken);
        return await scope.ServiceProvider.GetRequiredService<KnowledgeIngestor>().IngestAsync(cancellationToken);
    }

    /// <summary>
    /// Automatic ingestion after the seed (Development by default, <c>Knowledge:AutoIngest</c> overrides). Without embeddings
    /// configured it logs a warning and skips, and a failure is logged and does not stop the API from starting: the agent
    /// just finds no passages until <c>ingest</c> succeeds.
    /// </summary>
    public static async Task AutoIngestAsync(IServiceProvider services, bool isDevelopment, ILogger logger, CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<IOptions<KnowledgeOptions>>().Value.ResolveAutoIngest(isDevelopment))
            return;

        if (!services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.IsConfigured)
        {
            logger.LogWarning("Knowledge ingestion skipped: no embeddings API key (set Embeddings:ApiKey, or Llm:Primary:ApiKey which it defaults to). " +
                              "search_knowledge_base will find nothing until the documents are indexed.");
            return;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<KnowledgeIngestor>().IngestAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Knowledge ingestion failed; the API starts without (or with the previous) knowledge base. Run `dotnet run --project api/src/Chatbot.Api -- ingest` to see and retry it.");
        }
    }
}
