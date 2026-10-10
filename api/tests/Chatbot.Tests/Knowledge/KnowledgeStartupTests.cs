using Chatbot.Core.Domain;
using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chatbot.Tests.Knowledge;

/// <summary>The configuration of the embeddings, the <c>ingest</c> command and the automatic ingestion at startup.</summary>
[Collection(PostgresCollection.Name)]
public class KnowledgeStartupTests(PostgresFixture postgres)
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private ServiceProvider Services(string connectionString, FakeEmbeddingGenerator generator, FakeKnowledgeSource source, params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(Config([("ConnectionStrings:Postgres", connectionString), ("Embeddings:Model", TestKnowledge.Model), .. settings]));
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>("embeddings", generator);
        services.AddSingleton<IKnowledgeSource>(source);
        return services.BuildServiceProvider();
    }

    private static FakeKnowledgeSource AllNiches() => new FakeKnowledgeSource()
        .Put(Niche.RealEstate, "pets", "Pets policy", "Dogs are welcome.")
        .Put(Niche.Clinic, "insurance", "Insurance", "We accept OSDE 210.")
        .Put(Niche.Restaurant, "allergens", "Allergens", "We handle nuts.");

    [Fact]
    public void Embeddings_default_to_the_primary_llm_endpoint_key_and_the_gemini_model()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Config(
            ("ConnectionStrings:Postgres", "Host=x"), ("Llm:Primary:BaseUrl", "https://llm.example/v1/"), ("Llm:Primary:ApiKey", "primary-key")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

        Assert.Equal(("https://llm.example/v1/", "primary-key", "gemini-embedding-001", 768), (options.BaseUrl, options.ApiKey, options.Model, options.Dimensions));
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void Embeddings_settings_override_the_llm_defaults_and_blank_values_fall_back_to_them()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Config(
            ("ConnectionStrings:Postgres", "Host=x"), ("Llm:Primary:BaseUrl", "https://llm.example/v1/"), ("Llm:Primary:ApiKey", "primary-key"),
            ("Embeddings:ApiKey", "embeddings-key"), ("Embeddings:Model", ""), ("Embeddings:BaseUrl", "")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

        Assert.Equal(("https://llm.example/v1/", "embeddings-key", "gemini-embedding-001"), (options.BaseUrl, options.ApiKey, options.Model));
    }

    [Fact]
    public void Dimensions_that_do_not_match_the_column_are_refused_with_the_way_to_change_them()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Config(("ConnectionStrings:Postgres", "Host=x"), ("Embeddings:Dimensions", "1024")));
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value);

        Assert.Contains("768", error.Message);
        Assert.Contains("migration", error.Message);
    }

    [Fact]
    public async Task The_ingest_command_migrates_seeds_and_indexes_and_a_second_run_does_nothing()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync(); // not migrated: the command does it
        var generator = new FakeEmbeddingGenerator();
        await using var provider = Services(connectionString, generator, AllNiches());

        var first = await KnowledgeStartup.RunIngestCommandAsync(provider, CancellationToken.None);
        var second = await KnowledgeStartup.RunIngestCommandAsync(provider, CancellationToken.None);

        Assert.Equal((3, 3, 0), (first.Documents, first.Processed, second.Processed));
        Assert.Equal(3, second.Skipped);
        await using var db = PostgresFixture.CreateContext(connectionString);
        Assert.Equal(3, await db.Businesses.CountAsync());
        Assert.Equal(3, await db.KnowledgeChunks.CountAsync());
    }

    [Fact]
    public async Task The_ingest_command_throws_when_the_provider_fails_so_the_process_can_exit_with_an_error()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        var generator = new FakeEmbeddingGenerator { Failure = _ => new HttpRequestException("boom", null, System.Net.HttpStatusCode.Unauthorized) };
        await using var provider = Services(connectionString, generator, AllNiches());

        await Assert.ThrowsAsync<EmbeddingException>(() => KnowledgeStartup.RunIngestCommandAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Auto_ingest_indexes_in_development_and_does_nothing_elsewhere_by_default()
    {
        var generator = new FakeEmbeddingGenerator();
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var provider = Services(connectionString, generator, AllNiches(), ("Embeddings:ApiKey", "key"), ("Embeddings:BaseUrl", "https://x.invalid/"));
        await KnowledgeStartup.RunIngestCommandAsync(provider, CancellationToken.None); // businesses must exist
        await using (var db = PostgresFixture.CreateContext(connectionString))
            await db.KnowledgeDocuments.ExecuteDeleteAsync();
        var logger = new CapturingLogger();

        await KnowledgeStartup.AutoIngestAsync(provider, isDevelopment: false, logger, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(connectionString))
            Assert.Equal(0, await db.KnowledgeDocuments.CountAsync());

        await KnowledgeStartup.AutoIngestAsync(provider, isDevelopment: true, logger, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(connectionString))
            Assert.Equal(3, await db.KnowledgeDocuments.CountAsync());
    }

    [Fact]
    public async Task Auto_ingest_can_be_switched_on_outside_development_and_off_inside_it()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var on = Services(connectionString, new FakeEmbeddingGenerator(), AllNiches(), ("Embeddings:ApiKey", "key"), ("Embeddings:BaseUrl", "https://x.invalid/"), ("Knowledge:AutoIngest", "true"));
        var off = Services(connectionString, new FakeEmbeddingGenerator(), AllNiches(), ("Embeddings:ApiKey", "key"), ("Embeddings:BaseUrl", "https://x.invalid/"), ("Knowledge:AutoIngest", "false"));
        await KnowledgeStartup.RunIngestCommandAsync(on, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(connectionString))
            await db.KnowledgeDocuments.ExecuteDeleteAsync();

        await KnowledgeStartup.AutoIngestAsync(off, isDevelopment: true, new CapturingLogger(), CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(connectionString))
            Assert.Equal(0, await db.KnowledgeDocuments.CountAsync());

        await KnowledgeStartup.AutoIngestAsync(on, isDevelopment: false, new CapturingLogger(), CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(connectionString))
            Assert.Equal(3, await db.KnowledgeDocuments.CountAsync());
    }

    [Fact]
    public async Task Auto_ingest_without_an_api_key_warns_and_skips_without_failing_the_startup()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var generator = new FakeEmbeddingGenerator();
        await using var provider = Services(connectionString, generator, AllNiches()); // no Embeddings:ApiKey and no Llm:Primary:ApiKey
        var logger = new CapturingLogger();

        await KnowledgeStartup.AutoIngestAsync(provider, isDevelopment: true, logger, CancellationToken.None);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Embeddings:ApiKey", warning.Message);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task A_failing_auto_ingest_is_logged_and_never_thrown()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var generator = new FakeEmbeddingGenerator { Failure = _ => new HttpRequestException("boom", null, System.Net.HttpStatusCode.Unauthorized) };
        await using var provider = Services(connectionString, generator, AllNiches(), ("Embeddings:ApiKey", "key"), ("Embeddings:BaseUrl", "https://x.invalid/"));
        var logger = new CapturingLogger();

        await KnowledgeStartup.AutoIngestAsync(provider, isDevelopment: true, logger, CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Knowledge ingestion failed"));
    }

    [Fact]
    public async Task The_migration_enables_the_vector_extension_and_the_indexes_exist()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);

        var extensions = await db.Database.SqlQueryRaw<string>("SELECT extname AS \"Value\" FROM pg_extension").ToListAsync();
        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'knowledge_chunks'").ToListAsync();

        Assert.Contains("vector", extensions);
        Assert.Contains(indexes, i => i.Contains("USING hnsw") && i.Contains("vector_cosine_ops"));
        Assert.Contains(indexes, i => i.Contains("USING gin") && i.Contains("SearchVector"));
        Assert.Contains(indexes, i => i.Contains("(\"BusinessId\")"));
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("\"DocumentId\", \"Ordinal\""));
    }
}
