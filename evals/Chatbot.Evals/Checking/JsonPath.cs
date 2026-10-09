using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chatbot.Evals.Checking;

/// <summary>
/// The tiny path language of <c>mustMention.path</c>: property names (case-insensitive) separated by dots, each
/// optionally followed by <c>[n]</c> or <c>[*]</c>. Examples: <c>total</c>, <c>results[*].price</c>, <c>slots[0].label</c>.
/// </summary>
internal static partial class JsonPath
{
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)?(?<index>(?:\[(?:\*|\d+)\])*)$")]
    private static partial Regex Segment();

    [GeneratedRegex(@"\[(\*|\d+)\]")]
    private static partial Regex Index();

    public static bool IsValid(string path) =>
        path.Split('.').All(s => s.Length > 0 && Segment().IsMatch(s));

    /// <summary>Every value the path reaches in <paramref name="root"/>; empty when it reaches nothing.</summary>
    public static IReadOnlyList<JsonElement> Select(JsonElement root, string path)
    {
        var current = new List<JsonElement> { root };
        foreach (var raw in path.Split('.'))
        {
            var segment = Segment().Match(raw);
            if (!segment.Success)
                throw new FormatException($"'{path}' is not a valid path.");

            var name = segment.Groups["name"].Value;
            var next = new List<JsonElement>();
            foreach (var element in current)
            {
                if (name.Length == 0)
                {
                    next.Add(element);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        next.Add(property.Value);
                }
            }

            foreach (Match index in Index().Matches(segment.Groups["index"].Value))
            {
                var expanded = new List<JsonElement>();
                foreach (var element in next.Where(e => e.ValueKind == JsonValueKind.Array))
                {
                    if (index.Groups[1].Value == "*")
                        expanded.AddRange(element.EnumerateArray());
                    else if (int.Parse(index.Groups[1].Value) < element.GetArrayLength())
                        expanded.Add(element[int.Parse(index.Groups[1].Value)]);
                }

                next = expanded;
            }

            current = next;
        }

        return current;
    }
}
