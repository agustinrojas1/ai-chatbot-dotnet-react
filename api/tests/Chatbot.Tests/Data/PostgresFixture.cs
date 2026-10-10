using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chatbot.Tests.Data;

/// <summary>One PostgreSQL 17 + pgvector container shared by every database test; each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates an empty database and returns its connection string (no migrations applied). Pooling is off: every test
    /// has its own database, and Npgsql keeps one pool per connection string, so the idle connections of finished tests
    /// would pile up until PostgreSQL refuses new clients ("too many clients").
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }

    /// <summary>Creates a database and applies all migrations.</summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = CreateContext(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static ChatbotDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ChatbotDbContext>().UseChatbotNpgsql(connectionString).Options);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
