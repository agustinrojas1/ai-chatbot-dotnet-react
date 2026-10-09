using Chatbot.Api.Endpoints;
using Chatbot.Api.Security;
using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAbuseProtection();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOptions<AdminOptions>().Bind(builder.Configuration.GetSection(AdminOptions.SectionName));

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

// Development only: apply migrations and seed at startup. Production applies migrations as an explicit step.
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ChatbotDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>().SeedAsync(CancellationToken.None);
}

if (!app.Environment.IsDevelopment())
    StartupChecks.WarnAboutMissingSecrets(app.Logger, app.Configuration);

app.UseAbuseProtection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHealthChecks("/health/ready", new()
{
    Predicate = c => c.Tags.Contains("ready"),
});

app.MapNicheEndpoints();
app.MapSessionEndpoints();
app.MapSessionHistoryEndpoints();
app.MapAdminEndpoints();

app.Run();

internal sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Unhealthy("ConnectionStrings:Postgres is not configured.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(timeout.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres unreachable.", ex);
        }
    }
}

public partial class Program;
