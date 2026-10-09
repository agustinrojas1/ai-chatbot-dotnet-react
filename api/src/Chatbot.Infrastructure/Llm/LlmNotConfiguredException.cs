namespace Chatbot.Infrastructure.Llm;

/// <summary>
/// The server has no primary provider configured. It is a deployment problem, not something the visitor can fix by
/// retrying: the agent answers it with its own message and a non-retryable error.
/// </summary>
public sealed class LlmNotConfiguredException()
    : InvalidOperationException("LLM is not configured: set Llm:Primary:ApiKey, Llm:Primary:BaseUrl and Llm:Primary:Model.");
