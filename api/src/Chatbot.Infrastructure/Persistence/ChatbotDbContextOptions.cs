using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Persistence;

public static class ChatbotDbContextOptions
{
    /// <summary>
    /// PostgreSQL with the pgvector type mapping the model needs (<c>vector</c> columns). Every <see cref="ChatbotDbContext"/>
    /// must be configured with it, or building the model fails.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseChatbotNpgsql<TContext>(this DbContextOptionsBuilder<TContext> builder, string connectionString)
        where TContext : DbContext
    {
        builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
        return builder;
    }

    public static DbContextOptionsBuilder UseChatbotNpgsql(this DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
        return builder;
    }
}
