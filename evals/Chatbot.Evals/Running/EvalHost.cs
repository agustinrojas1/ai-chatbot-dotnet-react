using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Infrastructure.Llm;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Chatbot.Evals.Running;

/// <summary>Configuration of an eval run: evalsettings.json, environment variables and what the command line overrides.</summary>
internal static class EvalConfiguration
{
    /// <summary>The names of <c>.env.example</c>, so a sourced <c>.env</c> works. The <c>Llm__...</c> variables win over them.</summary>
    private static readonly (string Variable, string Key)[] EnvAliases =
    [
        ("LLM_PRIMARY_BASE_URL", "Llm:Primary:BaseUrl"),
        ("LLM_PRIMARY_API_KEY", "Llm:Primary:ApiKey"),
        ("LLM_PRIMARY_MODEL", "Llm:Primary:Model"),
        ("LLM_FALLBACK_BASE_URL", "Llm:Fallback:BaseUrl"),
        ("LLM_FALLBACK_API_KEY", "Llm:Fallback:ApiKey"),
        ("LLM_FALLBACK_MODEL", "Llm:Fallback:Model"),
        ("EMBEDDINGS_BASE_URL", "Embeddings:BaseUrl"),
        ("EMBEDDINGS_API_KEY", "Embeddings:ApiKey"),
        ("EMBEDDINGS_MODEL", "Embeddings:Model"),
    ];

    /// <param name="fakeEmbeddings">Configures the embeddings for <see cref="FakeEmbeddingGenerator"/> (model name, a dummy key, its thresholds).</param>
    public static IConfiguration Build(string connectionString, string? modelOverride = null, bool fakeEmbeddings = false)
    {
        var aliases = EnvAliases
            .Select(a => (a.Key, Value: Environment.GetEnvironmentVariable(a.Variable)))
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .ToDictionary(a => a.Key, a => a.Value);

        var overrides = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString };
        if (modelOverride is not null)
            overrides["Llm:Primary:Model"] = modelOverride;
        if (fakeEmbeddings)
        {
            overrides["Embeddings:ApiKey"] = "fake";
            overrides["Embeddings:BaseUrl"] = "http://fake.invalid/";
            overrides["Embeddings:Model"] = FakeEmbeddingGenerator.Model;
            overrides["Knowledge:MinSemanticSimilarity"] = FakeEmbeddingGenerator.MinSemanticSimilarity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "evalsettings.json"), optional: true)
            .AddInMemoryCollection(aliases)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(overrides)
            .Build();
    }
}

/// <summary>The services the harness shares with the API: the same <c>AddInfrastructure</c> registration and the same <c>AgentService</c>.</summary>
internal static class EvalHost
{
    /// <param name="llm">Replaces the configured model client (the <c>--fake</c> run and the tests); null uses <c>Llm</c> from the configuration.</param>
    /// <param name="policy">Rate-limit handling around the model client (see <see cref="QuotaGuardChatClient"/>); null leaves the client as it is.</param>
    /// <param name="embeddings">Replaces the embeddings client (the <c>--fake</c> run and the tests); null uses <c>Embeddings</c> from the configuration.</param>
    public static ServiceProvider Build(
        IConfiguration configuration, IChatClient? llm = null, ModelCallPolicy? policy = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddInfrastructure(configuration);
        services.AddSingleton<QuotaMonitor>();
        if (embeddings is not null)
            services.AddKeyedSingleton(EmbeddingClientFactory.ServiceKey, embeddings);

        // The last registration of a key wins: this replaces the client AddInfrastructure registered, without touching it.
        services.AddKeyedSingleton<IChatClient>(LlmClientFactory.ServiceKey, (sp, _) =>
        {
            var inner = llm ?? LlmClientFactory.Create(sp.GetRequiredService<IOptions<LlmOptions>>().Value, sp.GetService<ILoggerFactory>());
            return policy is null ? inner : new QuotaGuardChatClient(inner, policy, sp.GetRequiredService<QuotaMonitor>());
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Indexes the knowledge documents (chunks and embeddings) the way the API does after the seed. Idempotent: a document whose
    /// hash did not change is skipped, so a persistent database is not re-embedded.
    /// </summary>
    public static async Task<IngestionReport> IngestKnowledgeAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<KnowledgeIngestor>().IngestAsync(ct);
    }

    /// <summary>Applies the migrations and the idempotent seed, as the API does at startup in Development.</summary>
    public static async Task PrepareDatabaseAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatbotDbContext>().Database.MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>().SeedAsync(ct);
    }
}

/// <summary>The database of a run: the one in <c>ConnectionStrings__Postgres</c>, or a throwaway PostgreSQL container.</summary>
internal sealed class EvalDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;

    private EvalDatabase(string connectionString, PostgreSqlContainer? container)
    {
        ConnectionString = connectionString;
        _container = container;
    }

    public string ConnectionString { get; }

    public bool IsContainer => _container is not null;

    public static async Task<EvalDatabase> StartAsync(string? configuredConnectionString, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
            return new EvalDatabase(configuredConnectionString, null);

        var container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
        await container.StartAsync(ct);
        return new EvalDatabase(container.GetConnectionString(), container);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
