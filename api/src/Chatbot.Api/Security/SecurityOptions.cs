namespace Chatbot.Api.Security;

/// <summary>Bound from the <c>Security</c> configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Salt of the stored client IP hash. Blank falls back to a public development value.</summary>
    public string? IpHashSalt { get; set; }
}
