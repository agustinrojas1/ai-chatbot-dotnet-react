using Chatbot.Core.Agent;
using Chatbot.Core.Domain;

namespace Chatbot.Tests.Agent;

public class PromptBuilderTests
{
    private static readonly Business Business = new()
    {
        Name = "PrimeHaven Realty",
        PersonaPrompt = "You are Sofia, a sales consultant.",
        Timezone = "America/Argentina/Buenos_Aires",
    };

    // 2026-10-10 01:30 UTC is still Friday 2026-10-09 22:30 in Buenos Aires (UTC-03:00).
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_with_the_persona_and_includes_the_common_rules()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Business, SessionStatus.Active, Now);

        Assert.StartsWith("You are Sofia, a sales consultant.", prompt);
        Assert.Contains("search_catalog", prompt);
        Assert.Contains("check_availability", prompt);
        Assert.Contains("create_booking", prompt);
        Assert.Contains("handoff_to_human", prompt);
        Assert.Contains("PENDING confirmation", prompt);
        Assert.Contains("explicit confirmation", prompt);
        Assert.Contains("AwaitingHuman", prompt);
        Assert.Contains("Never reveal", prompt);
        Assert.Contains("WhatsApp", prompt);
    }

    [Fact]
    public void Uses_the_business_local_date_and_time()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Business, SessionStatus.Active, Now);

        Assert.Contains("Friday 2026-10-09 22:30", prompt);
        Assert.Contains("America/Argentina/Buenos_Aires", prompt);
        Assert.Contains("UTC-03:00", prompt);
    }

    [Fact]
    public void Unknown_timezone_falls_back_to_UTC()
    {
        var business = new Business { PersonaPrompt = "p", Timezone = "Not/AZone" };

        var prompt = PromptBuilder.BuildSystemPrompt(business, SessionStatus.Active, Now);

        Assert.Contains("Saturday 2026-10-10 01:30", prompt);
        Assert.Contains("(UTC, UTC+00:00)", prompt);
    }

    [Theory]
    [InlineData(SessionStatus.Active)]
    [InlineData(SessionStatus.AwaitingHuman)]
    public void States_the_session_status(SessionStatus status)
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Business, status, Now);

        Assert.Contains($"Session status: {status}.", prompt);
    }

    [Fact]
    public void Common_rules_stay_short()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(new Business { PersonaPrompt = "" }, SessionStatus.Active, Now);

        // ~4 characters per token: keep the shared block under ~900 tokens.
        Assert.True(prompt.Length < 3_600, $"common rules are {prompt.Length} characters");
    }

    [Fact]
    public void Language_instruction_is_exact()
    {
        Assert.Equal("Reply ONLY in Spanish. Translate catalog details if needed.", PromptBuilder.LanguageInstruction(Language.Spanish));
        Assert.Equal("Reply ONLY in English. Translate catalog details if needed.", PromptBuilder.LanguageInstruction(Language.English));
    }
}
