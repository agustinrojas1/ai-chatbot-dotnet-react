using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Chatbot.Api.Endpoints;

/// <summary>
/// Helpers around the client IP. Always pass <c>HttpContext.Connection.RemoteIpAddress</c>: after the forwarded headers
/// middleware it is the real client when the request came through a trusted proxy and the peer address otherwise.
/// </summary>
internal static class ClientIp
{
    public const string DefaultSalt = "dev-only-ip-hash-salt";

    /// <summary>The configured salt, or the public development one when it is blank.</summary>
    public static string ResolveSalt(string? configured) => string.IsNullOrWhiteSpace(configured) ? DefaultSalt : configured;

    /// <summary>
    /// SHA-256 of salt + IP, first 32 hex characters. The raw IP is never stored, and without the salt the hash
    /// cannot be reversed by trying every IPv4 address.
    /// </summary>
    public static string Hash(string? ip, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{ip ?? "unknown"}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    /// <summary>
    /// What a rate limit counts as "one client": the IPv4 address, or the /64 network of an IPv6 address (one home
    /// connection gets a whole /64, so counting single IPv6 addresses would let it rotate past the limit).
    /// </summary>
    public static string PartitionKey(IPAddress? ip)
    {
        if (ip is null)
            return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        return $"{Convert.ToHexStringLower(bytes[..8])}::/64";
    }
}
