using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Chatbot.Infrastructure.Agent;

/// <summary>
/// One executed tool call with its result. It is stored as a <c>ChatMessage</c> with role <c>Tool</c>, so the next
/// turn still sees the ids a tool returned (a <c>slotId</c> offered in one turn is needed to book in a later one).
/// Call and result live in one row: replaying them can never produce a call without its result, which providers reject.
/// </summary>
internal sealed record ToolExchange(string CallId, string Name, JsonElement Arguments, JsonElement Result)
{
    /// <summary>Payloads longer than this (as JSON text) are replaced by their truncated text, to keep the history small.</summary>
    public const int MaxPayloadChars = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ToolExchange Create(FunctionCallContent call, FunctionResultContent result) =>
        new(call.CallId, call.Name, Compact(call.Arguments), Compact(result.Result));

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for a row that cannot be read, so one bad row does not break every later turn of the session.</summary>
    public static ToolExchange? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ToolExchange>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The two messages the model expects: the assistant's call, then the tool's result.</summary>
    public IEnumerable<ChatMessage> ToMessages()
    {
        var arguments = Arguments.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(Arguments)
            : null;

        yield return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(CallId, Name, arguments)]);
        yield return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(CallId, Result)]);
    }

    private static JsonElement Compact(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        var raw = element.GetRawText();
        return raw.Length <= MaxPayloadChars
            ? element
            : JsonSerializer.SerializeToElement(raw[..MaxPayloadChars] + "…[truncated]");
    }
}
