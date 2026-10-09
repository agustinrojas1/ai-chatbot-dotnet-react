namespace Chatbot.Infrastructure.Llm;

/// <summary>Bound from the <c>Llm</c> configuration section (SPEC 8).</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProviderOptions Primary { get; set; } = new();

    /// <summary>Optional. Without an ApiKey the app runs with no fallback.</summary>
    public LlmProviderOptions? Fallback { get; set; }
}

/// <summary>Any OpenAI-compatible endpoint (Gemini, Groq, ...).</summary>
public sealed class LlmProviderOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}
