using System.Security.Cryptography;
using System.Text;

namespace Chatbot.Api.Endpoints;

internal static class ClientIp
{
    public const string DefaultSalt = "dev-only-ip-hash-salt";

    /// <summary>
    /// SHA-256 of salt + IP, first 32 hex characters. The raw IP is never stored, and without the salt the hash
    /// cannot be reversed by trying every IPv4 address.
    /// </summary>
    public static string Hash(string? ip, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{ip ?? "unknown"}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }
}
