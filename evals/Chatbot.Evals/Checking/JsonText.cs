using System.Text.Encodings.Web;
using System.Text.Json;

namespace Chatbot.Evals.Checking;

/// <summary>JSON as it is shown to people: one line, and "María" instead of "Mar\u00EDa" (the trace stores it escaped).</summary>
internal static class JsonText
{
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Show(JsonElement value) => JsonSerializer.Serialize(value, Readable);
}
