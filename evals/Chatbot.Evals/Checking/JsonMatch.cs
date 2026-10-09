using System.Globalization;
using System.Text.Json;

namespace Chatbot.Evals.Checking;

/// <summary>
/// Partial matching of the arguments a model sent against the arguments a case expects. Everything the case lists
/// must be there; anything else the model sent is ignored.
/// </summary>
internal static class JsonMatch
{
    /// <summary>
    /// Objects: every expected property exists in the actual object (names ignore case) and matches.
    /// Arrays: every expected element matches some actual element. Text: the actual text contains the expected one
    /// (ignoring case and accents), so <c>query: pepperoni</c> matches "pepperoni pizza". Numbers and booleans are
    /// compared by value, also when the model sent them as text. A list the model sent matches a single expected value it contains.
    /// </summary>
    public static bool IsSubset(JsonElement expected, JsonElement actual)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                return actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(property =>
                    actual.EnumerateObject().Any(a => a.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)
                        && IsSubset(property.Value, a.Value)));
            case JsonValueKind.Array:
                return actual.ValueKind == JsonValueKind.Array && expected.EnumerateArray().All(item =>
                    actual.EnumerateArray().Any(a => IsSubset(item, a)));
        }

        if (actual.ValueKind == JsonValueKind.Array)
            return actual.EnumerateArray().Any(a => IsSubset(expected, a));

        return expected.ValueKind switch
        {
            JsonValueKind.String => actual.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                && TextMatch.Normalize(actual.ValueKind == JsonValueKind.String ? actual.GetString()! : actual.GetRawText())
                    .Contains(TextMatch.Normalize(expected.GetString()!), StringComparison.Ordinal),
            JsonValueKind.Number => AsNumber(actual) == expected.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False => AsBool(actual) == (expected.ValueKind == JsonValueKind.True),
            JsonValueKind.Null => actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
            _ => false,
        };
    }

    private static decimal? AsNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : null,
        JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static bool? AsBool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(element.GetString(), out var b) => b,
        _ => null,
    };
}
