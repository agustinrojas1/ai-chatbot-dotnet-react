using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only. Reads <c>ConnectionStrings__Postgres</c>; the default never connects for <c>migrations add</c>.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChatbotDbContext>
{
    public ChatbotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=chatbot;Username=chatbot;Password=chatbot";
        var options = new DbContextOptionsBuilder<ChatbotDbContext>().UseChatbotNpgsql(connectionString).Options;
        return new ChatbotDbContext(options);
    }
}
