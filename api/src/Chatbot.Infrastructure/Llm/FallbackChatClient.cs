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
/// (SPEC 8). A response the SDK cannot parse because of an unknown finish reason is first repeated once on the same
/// provider (<see cref="IsUnknownFinishReason"/>). The provider that answered is reported in
/// <c>AdditionalProperties["llm.provider"]</c> and <c>["llm.model"]</c>.
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
            response = await CompleteAsync(primary, history, options, cancellationToken);
        }
        catch (Exception ex) when (fallback is not null && ShouldFallBack(ex))
        {
            LogFallback(ex);
            used = fallback;
            response = await CompleteAsync(fallback, history, options, cancellationToken);
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
        IAsyncEnumerator<ChatResponseUpdate> updates;
        bool hasUpdate;
        try
        {
            (updates, hasUpdate) = await OpenStreamAsync(primary, history, options, cancellationToken);
        }
        catch (Exception ex) when (fallback is not null && ShouldFallBack(ex))
        {
            LogFallback(ex);
            used = fallback;
            (updates, hasUpdate) = await OpenStreamAsync(fallback, history, options, cancellationToken);
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

    /// <summary>
    /// The SDK cannot parse a finish reason it does not know and throws <see cref="ArgumentOutOfRangeException"/> from
    /// <c>ChatFinishReasonExtensions.ToChatFinishReason</c> (message "Unknown ChatFinishReason value"). Gemini sends
    /// <c>function_call_filter: MALFORMED_FUNCTION_CALL</c> when it fails to build a tool call: a transient, non-deterministic
    /// model failure that usually succeeds when repeated. Matched by exception type and message because the SDK has no
    /// dedicated exception for it (streaming and non-streaming responses throw the same one).
    /// </summary>
    public static bool IsUnknownFinishReason(Exception ex) =>
        ex is ArgumentOutOfRangeException && ex.Message.Contains("ChatFinishReason", StringComparison.Ordinal);

    // Another provider can help with a rate limit or an outage (IsRetryable) and with a model hiccup (IsUnknownFinishReason).
    private static bool ShouldFallBack(Exception ex) => IsRetryable(ex) || IsUnknownFinishReason(ex);

    // One call on one endpoint. An unknown finish reason is repeated once on the same endpoint before giving up on it.
    private async Task<ChatResponse> CompleteAsync(
        LlmEndpoint endpoint, IReadOnlyList<ChatMessage> history, ChatOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            return await endpoint.Client.GetResponseAsync(history, WithModel(options, endpoint), cancellationToken);
        }
        catch (Exception ex) when (IsUnknownFinishReason(ex))
        {
            LogRetrySameEndpoint(endpoint, ex);
            return await endpoint.Client.GetResponseAsync(history, WithModel(options, endpoint), cancellationToken);
        }
    }

    // Starts a stream and pulls its first update, with the same unknown-finish-reason retry. It never yields, so a retry
    // can only happen while the caller has received nothing.
    private async Task<(IAsyncEnumerator<ChatResponseUpdate> Updates, bool HasUpdate)> OpenStreamAsync(
        LlmEndpoint endpoint, IReadOnlyList<ChatMessage> history, ChatOptions? options, CancellationToken cancellationToken)
    {
        for (bool retried = false; ; retried = true)
        {
            var updates = endpoint.Client.GetStreamingResponseAsync(history, WithModel(options, endpoint), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                return (updates, await updates.MoveNextAsync());
            }
            catch (Exception ex)
            {
                await updates.DisposeAsync();
                if (retried || !IsUnknownFinishReason(ex))
                {
                    throw;
                }

                LogRetrySameEndpoint(endpoint, ex);
            }
        }
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

    private void LogRetrySameEndpoint(LlmEndpoint endpoint, Exception ex) =>
        logger?.LogWarning(ex, "LLM provider {Provider} ({Model}) returned an unknown finish reason; retrying once on the same provider", endpoint.Provider, endpoint.Model);
}
