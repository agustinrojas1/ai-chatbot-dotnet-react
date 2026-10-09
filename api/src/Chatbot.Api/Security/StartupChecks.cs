using Chatbot.Api.Endpoints;

namespace Chatbot.Api.Security;

internal static class StartupChecks
{
    /// <summary>
    /// Outside Development a missing secret is a deployment mistake worth a loud line in the logs, not a crash:
    /// the app still starts safely (the admin endpoints answer 503 with a blank key).
    /// </summary>
    public static void WarnAboutMissingSecrets(ILogger logger, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration[$"{AdminOptions.SectionName}:Key"]))
            logger.LogWarning("Admin:Key (ADMIN_KEY) is blank: /api/admin/* answers 503 until it is set.");

        if (string.IsNullOrWhiteSpace(configuration[$"{SecurityOptions.SectionName}:IpHashSalt"]))
            logger.LogWarning("Security:IpHashSalt (IP_HASH_SALT) is blank: client IP hashes use a public development salt.");
    }
}
