using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI;

namespace Chatbot.Infrastructure.Llm;

/// <summary>
/// Gemini 3 answers a tool call with a <c>thought_signature</c> (<c>extra_content.google.thought_signature</c> on the
/// tool call) and rejects the next request with HTTP 400 if the replayed assistant message does not carry it back.
/// Microsoft.Extensions.AI.OpenAI drops that field when it rebuilds the history, so this policy puts Google's documented
/// sentinel on every replayed tool call that has none (ADR-013). It only exists on Gemini clients, see <see cref="AddTo"/>.
/// </summary>
public sealed class GeminiThoughtSignaturePolicy : PipelinePolicy
{
    /// <summary>Google's value for "this history was not produced in this generation": skips the validation.</summary>
    public const string SkipValidatorSignature = "skip_thought_signature_validator";

    private const string GeminiHost = "generativelanguage.googleapis.com";

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Adds the policy for the Gemini host only; any other provider (Groq, ...) keeps its pipeline untouched.</summary>
    public static void AddTo(OpenAIClientOptions options, Uri baseUrl)
    {
        if (string.Equals(baseUrl.Host, GeminiHost, StringComparison.OrdinalIgnoreCase))
        {
            // PerCall: runs once per request, before the SDK retry loop, so a retry re-sends the rewritten body.
            options.AddPolicy(new GeminiThoughtSignaturePolicy(), PipelinePosition.PerCall);
        }
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void Rewrite(PipelineMessage message)
    {
        // Only the chat completions call (streaming or not) has assistant messages to fix.
        if (message.Request.Content is not { } content
            || message.Request.Uri is not { } uri
            || !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return;
        }

        // The body is a small in-memory JSON document (the SDK serializes it before the pipeline runs).
        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        var body = new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);

        // Cheap exit for the first call of a run: no tool call in the history, nothing to parse.
        if (body.IndexOf("\"tool_calls\""u8) < 0)
        {
            return;
        }

        var root = JsonNode.Parse(body);
        if (!AddSignatures(root))
        {
            return;
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromString(root!.ToJsonString(WriteOptions)));
        content.Dispose();
    }

    /// <summary>Returns true if at least one tool call was changed.</summary>
    private static bool AddSignatures(JsonNode? root)
    {
        if (root?["messages"] is not JsonArray messages)
        {
            return false;
        }

        bool changed = false;
        foreach (var message in messages)
        {
            if (message is not JsonObject { } obj
                || obj["role"]?.GetValue<string>() != "assistant"
                || obj["tool_calls"] is not JsonArray toolCalls)
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                if (toolCall is not JsonObject call)
                {
                    continue;
                }

                var extra = call["extra_content"] as JsonObject ?? (JsonObject)(call["extra_content"] = new JsonObject());
                var google = extra["google"] as JsonObject ?? (JsonObject)(extra["google"] = new JsonObject());

                // A real signature (if it was ever kept) always wins over the sentinel.
                if (google["thought_signature"] is null)
                {
                    google["thought_signature"] = SkipValidatorSignature;
                    changed = true;
                }
            }
        }

        return changed;
    }
}
