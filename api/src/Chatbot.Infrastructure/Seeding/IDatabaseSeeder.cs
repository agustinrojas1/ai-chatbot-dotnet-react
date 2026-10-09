namespace Chatbot.Infrastructure.Seeding;

/// <summary>Populates reference data (businesses, catalog, slots). Runs at startup in Development after migrations.</summary>
public interface IDatabaseSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}
