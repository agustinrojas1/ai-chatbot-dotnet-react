using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Chatbot.Core.Agent;

namespace Chatbot.Infrastructure.Agent.Tracing;

/// <summary>
/// What is allowed to reach the public trace (SPEC 7.5, ADR-005): phone numbers are masked in every string and a
/// payload never exceeds <see cref="MaxChars"/> characters of JSON. Masking runs first, so a number cut by the
/// truncation cannot leak.
/// </summary>
public static class TracePayload
{
    /// <summary>Largest stored payload, as JSON text.</summary>
    public const int MaxChars = 4096;

    public const string TruncatedMarker = "…[truncated]";

    // The default encoder writes "+" as \u002B; keeping it literal gives the phone masker the text it expects.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The storable form of <paramref name="value"/>: null for null, otherwise the masked JSON. Over
    /// <see cref="MaxChars"/> it becomes a JSON string holding the first <see cref="MaxChars"/> characters plus <see cref="TruncatedMarker"/>.
    /// </summary>
    public static JsonElement? Sanitize(object? value)
    {
        if (value is null)
            return null;

        var element = value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, Json);
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            WriteMasked(writer, element);

        if (buffer.Length <= MaxChars) // bytes >= chars: a payload under the limit in bytes is under it in characters
            return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();

        var raw = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return raw.Length <= MaxChars
            ? JsonDocument.Parse(buffer.ToArray()).RootElement.Clone()
            : JsonSerializer.SerializeToElement(Clip(raw, MaxChars), Json);
    }

    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/> plus the marker, or the text itself if it fits. Not masked.</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end] + TruncatedMarker;
    }

    /// <summary>Masks phone numbers first, then clips: a number cut at the limit must not leave readable digits behind.</summary>
    public static string MaskAndClip(string text, int max) => Clip(PhoneMasker.MaskInText(text), max);

    private static void WriteMasked(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteMasked(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteMasked(writer, item);

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(PhoneMasker.MaskInText(element.GetString()!));
                break;
            case JsonValueKind.Number:
                // A phone sent as a JSON number (5491155554444) is still a phone: store it as masked text.
                var raw = element.GetRawText();
                var masked = PhoneMasker.MaskInText(raw);
                if (masked == raw)
                    element.WriteTo(writer);
                else
                    writer.WriteStringValue(masked);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
