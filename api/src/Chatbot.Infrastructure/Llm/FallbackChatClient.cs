using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Llm;

/// <summary>One LLM provider: the client plus the labels reported in the trace.</summary>
public sealed record LlmEndpoint(IChatClient Client, string Provider, string Model);

/// <summary>
/// Calls the primary provider and, if it answers 429 or 5xx before producing anything, retries once with the fallback
/// (SPEC 8). The provider that answered is reported in <c>AdditionalProperties["llm.provider"]</c> and <c>["llm.model"]</c>.
/// </summary>
public sealed class FallbackChatClient(LlmEndpoint primary, LlmEndpoint? fallback, ILogger<FallbackChatClient>? logger = null) : IChatClient
{
    public const string ProviderKey = "llm.provider";
    public const string ModelKey = "llm.model";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var used = primary;
        ChatResponse response;
        try
        {
            response = await primary.Client.GetResponseAsync(history, WithModel(options, primary), cancellationToken);
        }
        catch (Exception ex) when (fallback is not null && IsRetryable(ex))
        {
            LogFallback(ex);
            used = fallback;
            response = await fallback.Client.GetResponseAsync(history, WithModel(options, fallback), cancellationToken);
        }

        Tag(response.AdditionalProperties ??= [], used);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // The fallback is only allowed while nothing has been yielded, so pull the first update before deciding.
        var used = primary;
        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        bool hasUpdate;
        try
        {
            updates = primary.Client.GetStreamingResponseAsync(history, WithModel(options, primary), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            hasUpdate = await updates.MoveNextAsync();
        }
        catch (Exception ex) when (fallback is not null && IsRetryable(ex))
        {
            LogFallback(ex);
            if (updates is not null)
            {
                await updates.DisposeAsync();
            }

            used = fallback;
            updates = fallback.Client.GetStreamingResponseAsync(history, WithModel(options, fallback), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            hasUpdate = await updates.MoveNextAsync();
        }

        await using (updates)
        {
            // From here on errors propagate: the caller has already received part of the answer.
            while (hasUpdate)
            {
                var update = updates.Current;
                Tag(update.AdditionalProperties ??= [], used);
                yield return update;
                hasUpdate = await updates.MoveNextAsync();
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : primary.Client.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        primary.Client.Dispose();
        fallback?.Client.Dispose();
    }

    /// <summary>429 (rate limit) or any 5xx. 4xx such as 400/401 are our bug or config, so another provider would not help.</summary>
    public static bool IsRetryable(Exception ex)
    {
        int? status = ex switch
        {
            ClientResultException e => e.Status, // thrown by the OpenAI SDK
            HttpRequestException e => (int?)e.StatusCode,
            _ => null,
        };

        return status is (int)HttpStatusCode.TooManyRequests or >= 500;
    }

    // Each provider has its own model name, so a ModelId set for one must not leak to the other.
    private static ChatOptions WithModel(ChatOptions? options, LlmEndpoint endpoint)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        clone.ModelId = endpoint.Model;
        return clone;
    }

    private static void Tag(AdditionalPropertiesDictionary properties, LlmEndpoint endpoint)
    {
        properties[ProviderKey] = endpoint.Provider;
        properties[ModelKey] = endpoint.Model;
    }

    private void LogFallback(Exception ex) =>
        logger?.LogWarning(ex, "LLM provider {Provider} failed; retrying once with {Fallback}", primary.Provider, fallback!.Provider);
}
