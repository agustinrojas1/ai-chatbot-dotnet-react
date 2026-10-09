using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using static Chatbot.Tests.Security.SecurityHost;

namespace Chatbot.Tests.Security;

/// <summary>
/// Which IP the limits and <c>ClientIpHash</c> see: the forwarded one only when the connection comes from a trusted
/// proxy. Otherwise any visitor could send <c>X-Forwarded-For</c> and dodge the per-IP limit.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ClientIpTests(PostgresFixture postgres)
{
    private const string Proxy = "10.0.0.5";
    private const string Visitor = "203.0.113.7";

    private async Task<(AgentWorld World, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory)> StartAsync(params (string, string)[] settings)
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        return (world, Start(world, settings.ToDictionary(s => s.Item1, s => s.Item2)));
    }

    private static async Task<string> HashOfNewSessionAsync(AgentWorld world, HttpClient client, string remoteIp, string? forwardedFor)
    {
        var response = await CreateSessionAsync(client, remoteIp: remoteIp, forwardedFor: forwardedFor);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        await using var db = world.NewDb();
        return (await db.ChatSessions.SingleAsync(s => s.Id == id)).ClientIpHash;
    }

    [Fact]
    public async Task Forwarded_for_is_honored_when_the_connection_comes_from_a_trusted_proxy()
    {
        var (world, factory) = await StartAsync(("Security:TrustedProxyCidr", "10.0.0.0/8"), ("RateLimit:IpMessages", "2"));
        using var _ = factory;
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();

        // Two visitors behind the same proxy are counted apart.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: Proxy, forwardedFor: Visitor)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: Proxy, forwardedFor: Visitor)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, session, remoteIp: Proxy, forwardedFor: Visitor)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: Proxy, forwardedFor: "203.0.113.8")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_is_ignored_when_the_connection_is_not_from_a_trusted_proxy()
    {
        var (world, factory) = await StartAsync(("Security:TrustedProxyCidr", "10.0.0.0/8"), ("RateLimit:IpMessages", "2"));
        using var _ = factory;
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();
        const string attacker = "198.51.100.9";

        // The attacker rotates the header on every request; it changes nothing: they are counted by the address they connect from.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: attacker, forwardedFor: "1.1.1.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: attacker, forwardedFor: "2.2.2.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, session, remoteIp: attacker, forwardedFor: "3.3.3.3")).StatusCode);
    }

    [Fact]
    public async Task Client_ip_hash_uses_the_resolved_ip_and_only_the_entry_added_by_the_trusted_proxy()
    {
        var (world, factory) = await StartAsync(("Security:TrustedProxyCidr", "10.0.0.0/8"), ("Security:IpHashSalt", "test-salt"));
        using var _ = factory;
        var client = factory.CreateClient();

        var direct = await HashOfNewSessionAsync(world, client, Visitor, null);
        var viaProxy = await HashOfNewSessionAsync(world, client, Proxy, Visitor);
        // nginx appends the peer it saw to what the client sent, so the client's own entries come first and are not believed.
        var forgedChain = await HashOfNewSessionAsync(world, client, Proxy, $"1.1.1.1, {Visitor}");
        var otherVisitor = await HashOfNewSessionAsync(world, client, Proxy, "203.0.113.9");
        var proxyItself = await HashOfNewSessionAsync(world, client, Proxy, null);
        var spoofedDirect = await HashOfNewSessionAsync(world, client, Visitor, "9.9.9.9");

        Assert.Equal(direct, viaProxy);
        Assert.Equal(direct, forgedChain);
        Assert.Equal(direct, spoofedDirect);
        Assert.NotEqual(direct, otherVisitor);
        Assert.NotEqual(direct, proxyItself);
    }

    [Fact]
    public async Task By_default_private_ranges_are_trusted_and_public_addresses_are_not()
    {
        var (world, factory) = await StartAsync();
        using var _ = factory;
        var client = factory.CreateClient();

        var direct = await HashOfNewSessionAsync(world, client, Visitor, null);

        Assert.Equal(direct, await HashOfNewSessionAsync(world, client, "172.18.0.3", Visitor)); // docker bridge
        Assert.Equal(direct, await HashOfNewSessionAsync(world, client, "127.0.0.1", Visitor));
        Assert.NotEqual(direct, await HashOfNewSessionAsync(world, client, "8.8.8.8", Visitor));
    }

    [Fact]
    public async Task A_blank_trusted_proxy_setting_trusts_nobody()
    {
        var (world, factory) = await StartAsync(("Security:TrustedProxyCidr", ""));
        using var _ = factory;
        var client = factory.CreateClient();

        var proxyItself = await HashOfNewSessionAsync(world, client, Proxy, null);

        Assert.Equal(proxyItself, await HashOfNewSessionAsync(world, client, Proxy, Visitor));
    }

    [Fact]
    public async Task A_malformed_trusted_proxy_entry_fails_at_startup()
    {
        var (_, factory) = await StartAsync(("Security:TrustedProxyCidr", "10.0.0.0/8, not-an-ip"));
        using var _ = factory;

        AssertFailsToStart(factory, "Security:TrustedProxyCidr");
    }

    [Fact]
    public async Task Ipv6_clients_are_counted_by_their_64_bit_network()
    {
        var (world, factory) = await StartAsync(("RateLimit:IpMessages", "2"));
        using var _ = factory;
        var client = factory.CreateClient();
        var session = await world.NewSessionAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: "2001:db8:1:2::1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: "2001:db8:1:2::2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, session, remoteIp: "2001:db8:1:2:abcd::9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, session, remoteIp: "2001:db8:1:3::1")).StatusCode);
    }
}
