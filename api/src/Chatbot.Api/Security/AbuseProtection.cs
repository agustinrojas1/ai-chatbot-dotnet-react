using System.Net;
using System.Threading.RateLimiting;
using Chatbot.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Chatbot.Api.Security;

/// <summary>Wiring of SPEC 9: the client IP behind proxies, per-IP rate limits and CORS.</summary>
internal static class AbuseProtection
{
    public const string CorsPolicy = "Frontend";

    /// <summary>Rate limit policy of <c>POST /api/sessions/{id}/messages</c>.</summary>
    public const string MessagesPolicy = "messages";

    /// <summary>Rate limit policy of <c>POST /api/sessions</c>.</summary>
    public const string SessionsPolicy = "sessions";

    /// <summary>Rate limit policy of <c>/api/admin/*</c>.</summary>
    public const string AdminPolicy = "admin";

    /// <summary>
    /// Everything is configured through <see cref="IConfigureOptions{TOptions}"/>, which runs when the options are first
    /// read, so the values come from the final configuration (and a bad value fails at startup, not at the first request).
    /// </summary>
    public static IServiceCollection AddAbuseProtection(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .Validate(RateLimitOptions.IsValid, $"Every {RateLimitOptions.SectionName} value must be greater than zero.")
            .ValidateOnStart();
        services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);

        services.AddCors();
        services.AddRateLimiter(_ => { }); // registers the marker UseRateLimiter looks for; the policies come from ConfigureRateLimiter
        services.AddSingleton<IConfigureOptions<CorsOptions>, ConfigureCors>();
        services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ConfigureForwardedHeaders>();
        services.AddSingleton<IConfigureOptions<RateLimiterOptions>, ConfigureRateLimiter>();
        return services;
    }

    /// <summary>
    /// Order matters: the forwarded headers first (every later step sees the real client IP), then CORS (so a 429 also
    /// carries the CORS headers and the browser can read it), then the rate limiter.
    /// </summary>
    public static WebApplication UseAbuseProtection(this WebApplication app)
    {
        // Without any known proxy the middleware would trust every sender, the opposite of "trust nobody".
        if (TrustedProxies(app.Configuration).Length > 0)
            app.UseForwardedHeaders();
        app.UseCors(CorsPolicy);
        app.UseRateLimiter();

        // Surface a bad Cors:AllowedOrigin at startup instead of at the first request.
        _ = app.Services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsPolicy);
        return app;
    }

    /// <summary>
    /// <c>Cors:AllowedOrigin</c>: one origin or a comma-separated list, exact match, no wildcard. Only the methods and
    /// headers the frontend uses are allowed, and no credentials (the admin key travels in <c>X-Admin-Key</c>, not in a cookie).
    /// </summary>
    private sealed class ConfigureCors(IConfiguration configuration) : IConfigureOptions<CorsOptions>
    {
        public void Configure(CorsOptions options) => options.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(ParseOrigins(configuration["Cors:AllowedOrigin"]))
            .WithMethods(HttpMethods.Get, HttpMethods.Post)
            .WithHeaders("Content-Type", "Accept", AdminEndpoints.KeyHeader)
            .WithExposedHeaders("Retry-After")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
    }

    internal static string[] ParseOrigins(string? setting)
    {
        var origins = (setting ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (origins.Length == 0)
            throw new InvalidOperationException("Cors:AllowedOrigin (CORS_ALLOWED_ORIGIN) must list at least one origin.");

        foreach (var origin in origins)
        {
            var valid = Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
            if (!valid)
                throw new InvalidOperationException($"Cors:AllowedOrigin entry '{origin}' must be an origin like https://app.example.com (no wildcard, no path).");
        }

        return origins;
    }

    /// <summary>Loopback and the private ranges (docker networks, typical VPCs, IPv6 unique-local).</summary>
    public const string DefaultTrustedProxyCidr = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,fc00::/7";

    /// <summary>
    /// Believes <c>X-Forwarded-For</c> only when the connection comes from <c>Security:TrustedProxyCidr</c>: comma-separated
    /// CIDR ranges or single IPs, the reverse proxy in front of the API. A request from any other address is attributed to
    /// the address of the connection itself, so a visitor cannot pick their own IP. Not set means
    /// <see cref="DefaultTrustedProxyCidr"/>; set but blank trusts no proxy (the middleware is not even added).
    /// <c>ForwardLimit = 1</c>: only the entry written by that last proxy is used, never the ones before it, which the
    /// client controls.
    /// </summary>
    private sealed class ConfigureForwardedHeaders(IConfiguration configuration) : IConfigureOptions<ForwardedHeadersOptions>
    {
        public void Configure(ForwardedHeadersOptions options)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // The defaults trust loopback; replace them with exactly what the configuration says.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in TrustedProxies(configuration))
                options.KnownIPNetworks.Add(network);
        }
    }

    internal static IPNetwork[] TrustedProxies(IConfiguration configuration) =>
        (configuration["Security:TrustedProxyCidr"] ?? DefaultTrustedProxyCidr)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseNetwork)
            .ToArray();

    internal static IPNetwork ParseNetwork(string entry)
    {
        if (IPNetwork.TryParse(entry, out var network))
            return network;
        if (IPAddress.TryParse(entry, out var address))
            return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        throw new InvalidOperationException($"Security:TrustedProxyCidr entry '{entry}' is not an IP address or a CIDR range like 10.0.0.0/8.");
    }

    /// <summary>
    /// Sliding windows per client IP (<see cref="ClientIp.PartitionKey"/>): one policy for messages, a looser one for
    /// session creation and one for the admin endpoints. A rejection is a 429 with the JSON body and <c>Retry-After</c>, and is logged with the hashed IP.
    /// </summary>
    private sealed class ConfigureRateLimiter(
        IOptions<RateLimitOptions> limits, IOptions<SecurityOptions> security, ILogger<ConfigureRateLimiter> logger)
        : IConfigureOptions<RateLimiterOptions>
    {
        public void Configure(RateLimiterOptions options)
        {
            var window = TimeSpan.FromMinutes(limits.Value.IpWindowMinutes);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(MessagesPolicy, http => PerIp(http, limits.Value.IpMessages, window));
            options.AddPolicy(SessionsPolicy, http => PerIp(http, limits.Value.IpSessions, window));
            options.AddPolicy(AdminPolicy, http => PerIp(http, limits.Value.AdminRequests, window));
            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : window;

                logger.LogWarning("Rate limit {Policy} hit by client {ClientIpHash}: retry after {RetryAfterSeconds:0}s",
                    policy, ClientIp.Hash(http.Connection.RemoteIpAddress?.ToString(), ClientIp.ResolveSalt(security.Value.IpHashSalt)), retryAfter.TotalSeconds);

                var message = policy switch
                {
                    SessionsPolicy => "Too many new conversations from your network. Please wait a few minutes and try again.",
                    AdminPolicy => "Too many admin requests from your network. Please wait a few minutes and try again.",
                    _ => "Too many messages from your network. Please wait a few minutes and try again.",
                };
                await LimitResponse.TooManyRequests(http, LimitCodes.IpRateLimit, message, retryAfter).ExecuteAsync(http);
            };
        }

        private static RateLimitPartition<string> PerIp(HttpContext http, int permits, TimeSpan window) =>
            RateLimitPartition.GetSlidingWindowLimiter(
                ClientIp.PartitionKey(http.Connection.RemoteIpAddress),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = window,
                    SegmentsPerWindow = 10,
                    QueueLimit = 0,
                });
    }
}
