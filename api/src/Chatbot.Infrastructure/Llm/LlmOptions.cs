namespace Chatbot.Infrastructure.Llm;

/// <summary>Bound from the <c>Llm</c> configuration section (SPEC 8).</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProviderOptions Primary { get; set; } = new();

    /// <summary>Optional. Without an ApiKey the app runs with no fallback.</summary>
    public LlmProviderOptions? Fallback { get; set; }

    /// <summary>
    /// Seconds the primary has to produce its first streaming update before the fallback takes over (ADR-010). 0 or less
    /// turns the limit off. Ignored without a fallback and never applied to the fallback itself.
    /// </summary>
    public int FirstTokenTimeoutSeconds { get; set; } = 12;
}

/// <summary>Any OpenAI-compatible endpoint (Gemini, Groq, ...).</summary>
public sealed class LlmProviderOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}
