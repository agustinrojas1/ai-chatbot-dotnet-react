using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Llm;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chatbot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // The connection string is read when the DbContext is first resolved, so hosts that never touch the
        // database (e.g. the /health liveness probe) start without it.
        services.AddDbContext<ChatbotDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.")));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();
        services.AddLlm(configuration);
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName));
        services.AddScoped<AgentService>();

        return services;
    }
}
