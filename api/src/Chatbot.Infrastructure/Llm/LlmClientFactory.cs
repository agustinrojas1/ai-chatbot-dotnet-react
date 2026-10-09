using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using ChatClient = OpenAI.Chat.ChatClient; // OpenAI.Chat also defines a ChatMessage that clashes with Microsoft.Extensions.AI

namespace Chatbot.Infrastructure.Llm;

public static class LlmClientFactory
{
    /// <summary>Service key of the raw (undecorated) <see cref="IChatClient"/>. Tracing and function invocation wrap this one.</summary>
    public const string ServiceKey = "llm";

    /// <summary>Binds the <c>Llm</c> section and registers the fallback client as a keyed singleton. Nothing is built until first use.</summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName));
        services.AddKeyedSingleton<IChatClient>(ServiceKey, (sp, _) =>
            Create(sp.GetRequiredService<IOptions<LlmOptions>>().Value, sp.GetService<ILoggerFactory>()));
        return services;
    }

    public static FallbackChatClient Create(LlmOptions options, ILoggerFactory? loggerFactory = null)
    {
        var fallback = options.Fallback is { } f && !string.IsNullOrWhiteSpace(f.ApiKey)
            ? CreateEndpoint(f, retries: null)
            : null;

        // A missing key must not stop the app from starting (e.g. /health in CI): it fails on the first chat call instead.
        // With a fallback the SDK's own retries (3, with backoff) are turned off on the primary: we switch provider instead of waiting.
        var primary = string.IsNullOrWhiteSpace(options.Primary.ApiKey)
            ? new LlmEndpoint(new UnconfiguredChatClient(), "unconfigured", options.Primary.Model)
            : CreateEndpoint(options.Primary, retries: fallback is null ? null : 0);

        return new FallbackChatClient(primary, fallback, loggerFactory?.CreateLogger<FallbackChatClient>());
    }

    /// <summary><c>generativelanguage.googleapis.com</c> → <c>gemini</c>, <c>api.groq.com</c> → <c>groq</c>, otherwise the host.</summary>
    public static string ProviderLabel(Uri baseUrl) => baseUrl.Host switch
    {
        "generativelanguage.googleapis.com" => "gemini",
        "api.groq.com" => "groq",
        var host => host,
    };

    private static LlmEndpoint CreateEndpoint(LlmProviderOptions provider, int? retries)
    {
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException($"Llm BaseUrl '{provider.BaseUrl}' is not a valid absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(provider.Model))
        {
            throw new InvalidOperationException($"Llm Model is not configured for {baseUrl.Host}.");
        }

        var clientOptions = new OpenAIClientOptions { Endpoint = baseUrl };
        if (retries is { } maxRetries)
        {
            clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries);
        }

        var chatClient = new ChatClient(provider.Model, new ApiKeyCredential(provider.ApiKey), clientOptions);
        return new LlmEndpoint(chatClient.AsIChatClient(), ProviderLabel(baseUrl), provider.Model);
    }

    /// <summary>Stands in for the primary when no API key is configured; every call fails with an actionable message.</summary>
    private sealed class UnconfiguredChatClient : IChatClient
    {
        private const string Message = "LLM is not configured: set Llm:Primary:ApiKey, Llm:Primary:BaseUrl and Llm:Primary:Model.";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Message);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Message);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
