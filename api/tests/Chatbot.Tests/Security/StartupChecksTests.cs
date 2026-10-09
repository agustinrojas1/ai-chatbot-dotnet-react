using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.Extensions.Logging;
using static Chatbot.Tests.Security.SecurityHost;

namespace Chatbot.Tests.Security;

[Collection(PostgresCollection.Name)]
public class StartupChecksTests(PostgresFixture postgres)
{
    private async Task<List<(LogLevel Level, string Message)>> StartupLogAsync(params (string, string)[] settings)
    {
        var logs = new CapturingLoggerProvider();
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, settings.ToDictionary(s => s.Item1, s => s.Item2), logs: logs);
        factory.CreateClient(); // starts the host
        lock (logs.Entries)
            return [.. logs.Entries];
    }

    [Fact]
    public async Task A_blank_admin_key_outside_development_logs_a_warning()
    {
        var entries = await StartupLogAsync();

        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Admin:Key"));
    }

    [Fact]
    public async Task A_configured_admin_key_and_salt_log_no_warning()
    {
        var entries = await StartupLogAsync(("Admin:Key", "secret"), ("Security:IpHashSalt", "salt"));

        Assert.DoesNotContain(entries, e => e.Level == LogLevel.Warning && (e.Message.Contains("Admin:Key") || e.Message.Contains("IpHashSalt")));
    }

    [Fact]
    public async Task A_blank_ip_hash_salt_outside_development_logs_a_warning()
    {
        var entries = await StartupLogAsync(("Admin:Key", "secret"));

        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Security:IpHashSalt"));
    }
}
