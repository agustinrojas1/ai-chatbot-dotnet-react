using System.Runtime.CompilerServices;
using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace Chatbot.Evals.Running;

/// <summary>
/// The model of <c>--fake</c>: a deliberately dumb stand-in that lets the whole harness run offline. For each user
/// message it calls <c>search_catalog</c> once and then answers with a canned sentence in the language it was told
/// to use. It never books, never hands off and never refuses, so most cases fail: that is what proves the checks bite.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    public const string Model = "fake-model";
    public const string Provider = "fake";

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var conversation = history.Where(m => m.Role != ChatRole.System).ToList();
        var toolAnswered = conversation.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

        // The language instruction is the last system message (SPEC 7.3).
        var spanish = history.LastOrDefault(m => m.Role == ChatRole.System)?.Text.Contains("Spanish", StringComparison.Ordinal) == true;

        AIContent content = toolAnswered
            ? new TextContent(spanish
                ? "Esto es lo que encontré. Avisame si te puedo ayudar con algo más."
                : "Here is what I found. Let me know if I can help with anything else.")
            : new FunctionCallContent($"call-{Guid.NewGuid():N}", "search_catalog", new Dictionary<string, object?>());

        var promptChars = history.Sum(m => m.Text.Length);
        var answerChars = content is TextContent text ? text.Text.Length : 40;

        await Task.Yield();
        yield return Tagged(content);
        yield return Tagged(new UsageContent(new UsageDetails
        {
            InputTokenCount = promptChars / 4,
            OutputTokenCount = answerChars / 4,
        }));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The agent always streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>Tags the update with the provider and model the way <c>FallbackChatClient</c> does, so the run reports them.</summary>
    private static ChatResponseUpdate Tagged(AIContent content) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [content],
        AdditionalProperties = new()
        {
            [FallbackChatClient.ProviderKey] = Provider,
            [FallbackChatClient.ModelKey] = Model,
        },
    };
}
