using System.Net;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using static Chatbot.Tests.Security.SecurityHost;

namespace Chatbot.Tests.Security;

/// <summary>CORS is limited to <c>Cors:AllowedOrigin</c> (a comma-separated list), the frontend's methods and headers.</summary>
[Collection(PostgresCollection.Name)]
public class CorsTests(PostgresFixture postgres)
{
    private const string Allowed = "https://demo.example.com";

    private async Task<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>> StartAsync(string origins = Allowed) =>
        Start(await AgentWorld.CreateAsync(postgres, AlwaysOk()), new Dictionary<string, string> { ["Cors:AllowedOrigin"] = origins });

    private static Task<HttpResponseMessage> PreflightAsync(
        HttpClient client, string origin, string method = "POST", string headers = "content-type,x-admin-key", string path = "/api/sessions")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    [Fact]
    public async Task Preflight_from_the_allowed_origin_lists_only_what_the_frontend_needs()
    {
        using var factory = await StartAsync();

        var response = await PreflightAsync(factory.CreateClient(), Allowed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
        Assert.DoesNotContain("*", Header(response, "Access-Control-Allow-Origin"));
        var methods = Header(response, "Access-Control-Allow-Methods");
        Assert.Contains("POST", methods);
        Assert.Contains("GET", methods);
        Assert.DoesNotContain("DELETE", methods);
        Assert.DoesNotContain("*", methods);
        var headers = Header(response, "Access-Control-Allow-Headers");
        Assert.Contains("x-admin-key", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", headers);
        Assert.Equal("", Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_from_a_foreign_origin_gets_no_cors_headers()
    {
        using var factory = await StartAsync();

        var response = await PreflightAsync(factory.CreateClient(), "https://evil.example.net");

        Assert.Equal("", Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("", Header(response, "Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task A_method_or_header_the_frontend_does_not_use_is_not_allowed()
    {
        using var factory = await StartAsync();
        var client = factory.CreateClient();

        var badMethod = await PreflightAsync(client, Allowed, method: "DELETE");
        var badHeader = await PreflightAsync(client, Allowed, headers: "content-type,x-evil");

        Assert.DoesNotContain("DELETE", Header(badMethod, "Access-Control-Allow-Methods"));
        Assert.DoesNotContain("x-evil", Header(badHeader, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Actual_requests_get_the_origin_header_only_for_allowed_origins()
    {
        using var factory = await StartAsync();
        var client = factory.CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/niches");
        allowed.Headers.Add("Origin", Allowed);
        var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/niches");
        foreign.Headers.Add("Origin", "https://evil.example.net");

        Assert.Equal(Allowed, Header(await client.SendAsync(allowed), "Access-Control-Allow-Origin"));
        Assert.Equal("", Header(await client.SendAsync(foreign), "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_comma_separated_list_allows_each_origin_and_ignores_trailing_slashes()
    {
        using var factory = await StartAsync("https://a.example.com, https://b.example.com/ ,http://localhost:3001");
        var client = factory.CreateClient();

        foreach (var origin in new[] { "https://a.example.com", "https://b.example.com", "http://localhost:3001" })
            Assert.Equal(origin, Header(await PreflightAsync(client, origin), "Access-Control-Allow-Origin"));
        Assert.Equal("", Header(await PreflightAsync(client, "https://c.example.com"), "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://a.example.com, *")]
    [InlineData("https://*.example.com")]
    [InlineData("https://a.example.com/app")]
    [InlineData("a.example.com")]
    public async Task A_wildcard_or_malformed_origin_fails_at_startup(string origins)
    {
        using var factory = await StartAsync(origins);

        AssertFailsToStart(factory, "Cors:AllowedOrigin");
    }
}
