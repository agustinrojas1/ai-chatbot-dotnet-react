using System.Globalization;

namespace Chatbot.Core.Knowledge;

/// <summary>A knowledge file that cannot be used. The message always starts with the file, so a bad document is easy to find.</summary>
public sealed class KnowledgeFormatException(string source, string message) : Exception($"{source}: {message}");

/// <summary>A knowledge markdown file split into its frontmatter fields and its body.</summary>
public sealed record ParsedKnowledgeDocument(
    string Title, string Language, IReadOnlyList<string> Tags, DateOnly? Updated, string Body);

/// <summary>
/// Reads the frontmatter of a knowledge markdown file (SPEC 7.6): a <c>---</c> block with <c>title</c> and
/// <c>language</c> (<c>es</c> or <c>en</c>) as required fields, and <c>tags</c> and <c>updated</c> (<c>yyyy-MM-dd</c>)
/// as optional ones. Only the YAML subset the documents use is understood: <c>key: value</c> lines, quoted values, and
/// lists written inline (<c>[a, b]</c>), comma separated or as <c>- item</c> lines. Unknown keys are ignored.
/// Everything else is a <see cref="KnowledgeFormatException"/> that names the file and the line.
/// </summary>
public static class KnowledgeDocumentParser
{
    private const string Fence = "---";

    /// <summary>Line endings as <c>\n</c> and no BOM, so the same file hashes the same on every platform.</summary>
    public static string Normalize(string content) =>
        content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static ParsedKnowledgeDocument Parse(string source, string content)
    {
        var lines = Normalize(content).Split('\n');

        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0 || lines[first].TrimEnd() != Fence)
            throw new KnowledgeFormatException(source, "the file must start with a frontmatter block delimited by '---' lines.");

        var close = Array.FindIndex(lines, first + 1, l => l.TrimEnd() == Fence);
        if (close < 0)
            throw new KnowledgeFormatException(source, "the frontmatter block is not closed: add a '---' line after the last field.");

        var fields = ReadFields(source, lines, first + 1, close);

        var title = Single(fields, "title");
        if (string.IsNullOrWhiteSpace(title))
            throw new KnowledgeFormatException(source, "frontmatter 'title' is required.");

        var language = Single(fields, "language")?.Trim().ToLowerInvariant();
        if (language is not ("es" or "en"))
            throw new KnowledgeFormatException(source, $"frontmatter 'language' must be 'es' or 'en' (found '{language ?? "nothing"}').");

        DateOnly? updated = null;
        if (Single(fields, "updated") is { Length: > 0 } updatedText)
        {
            if (!DateOnly.TryParseExact(updatedText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new KnowledgeFormatException(source, $"frontmatter 'updated' must be a date like 2026-09-30 (found '{updatedText}').");
            updated = date;
        }

        var tags = fields.TryGetValue("tags", out var rawTags) ? ParseTags(rawTags) : [];

        var body = string.Join('\n', lines[(close + 1)..]).Trim();
        if (body.Length == 0)
            throw new KnowledgeFormatException(source, "the document has no content after the frontmatter.");

        return new ParsedKnowledgeDocument(title.Trim(), language, tags, updated, body);
    }

    /// <summary>Field name to its values: one per <c>- item</c> line, or a single entry holding the inline value.</summary>
    private static Dictionary<string, List<string>> ReadFields(string source, string[] lines, int from, int to)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? listKey = null;

        for (var i = from; i < to; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            if (trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' '))
            {
                if (listKey is null)
                    throw new KnowledgeFormatException(source, $"frontmatter line {i + 1}: a list item without a field before it.");
                fields[listKey].Add(Unquote(trimmed[1..].Trim()));
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
                throw new KnowledgeFormatException(source, $"frontmatter line {i + 1}: expected 'key: value' (found '{trimmed}').");

            var key = trimmed[..colon].Trim().ToLowerInvariant();
            if (fields.ContainsKey(key))
                throw new KnowledgeFormatException(source, $"frontmatter line {i + 1}: '{key}' appears twice.");

            var value = trimmed[(colon + 1)..].Trim();
            fields[key] = value.Length == 0 ? [] : [Unquote(value)];
            listKey = value.Length == 0 ? key : null;
        }

        return fields;
    }

    private static string? Single(Dictionary<string, List<string>> fields, string key) =>
        fields.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : null;

    private static List<string> ParseTags(List<string> raw)
    {
        // A block list already has one tag per entry; an inline value is "[a, b]" or "a, b".
        var items = raw.Count == 1 ? SplitInline(raw[0]) : raw;
        return [.. items.Select(t => Unquote(t.Trim())).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> SplitInline(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];
        return value.Split(',');
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
            ? value[1..^1]
            : value;
}
