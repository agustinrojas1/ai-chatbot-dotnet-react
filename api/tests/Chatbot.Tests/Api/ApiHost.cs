using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Api;

/// <summary>The real HTTP pipeline on a migrated and seeded database, with the model replaced by a scripted fake.</summary>
internal static class ApiHost
{
    public static async Task<(WebApplicationFactory<Program> Factory, AgentWorld World)> StartAsync(
        PostgresFixture postgres, ScriptedChatClient llm, IReadOnlyDictionary<string, string>? settings = null)
    {
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // "Testing": Development would migrate and seed the database at startup.
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", world.ConnectionString);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                builder.UseSetting(key, value);
            builder.ConfigureServices(services => services.AddKeyedSingleton<IChatClient>("llm", llm));
        });
        return (factory, world);
    }
}
