using System.Text;
using System.Text.RegularExpressions;

namespace Chatbot.Core.Knowledge;

/// <summary>
/// Sizes of a chunk, in approximate tokens (<see cref="Chunker.CharsPerToken"/> characters per token: no tokenizer is
/// needed to bound sizes, and the real count only matters for display and the embedding cost estimate).
/// </summary>
public sealed record ChunkerOptions(int TargetTokens = 400, int MaxTokens = 500, int OverlapTokens = 50)
{
    public static ChunkerOptions Default { get; } = new();

    /// <summary>Changes whenever the way text is cut changes, so the ingestion hash re-chunks documents after a tuning or a code change.</summary>
    public string Signature => $"chunker-v1/{TargetTokens}/{MaxTokens}/{OverlapTokens}";

    public void Validate()
    {
        if (TargetTokens < 20 || MaxTokens < TargetTokens || OverlapTokens < 0 || OverlapTokens >= TargetTokens)
            throw new ArgumentException("Chunker sizes must satisfy 20 <= TargetTokens <= MaxTokens and 0 <= OverlapTokens < TargetTokens.");
    }
}

/// <summary>One piece of a document, ready to embed. <see cref="Text"/> starts with its <c>Title &gt; Heading</c> context line.</summary>
public sealed record KnowledgeChunkDraft(int Ordinal, string Heading, string Text, int TokenCount);

/// <summary>
/// Structure-aware chunking of a markdown body (SPEC 7.6): one chunk per heading section, and a section longer than
/// <see cref="ChunkerOptions.MaxTokens"/> is cut into pieces of about <see cref="ChunkerOptions.TargetTokens"/>, at the
/// largest natural boundary that fits (paragraph, line, sentence, word), each piece starting with the tail of the previous
/// one (<see cref="ChunkerOptions.OverlapTokens"/>) so a sentence cut at a boundary is whole in one of them. Every chunk
/// starts with a <c>Title &gt; Heading</c> line so it makes sense alone, for the embedding and for the passage the model reads.
/// Pure and deterministic: the same input always gives the same chunks with the same ordinals.
/// </summary>
public static partial class Chunker
{
    public const int CharsPerToken = 4;

    /// <summary>Smallest body budget per piece, so a very long heading path cannot shrink the pieces to nothing.</summary>
    private const int MinBodyChars = 200;

    public static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    public static IReadOnlyList<KnowledgeChunkDraft> Split(string title, string markdownBody, ChunkerOptions? options = null)
    {
        options ??= ChunkerOptions.Default;
        options.Validate();

        var chunks = new List<KnowledgeChunkDraft>();
        foreach (var section in ReadSections(title, markdownBody))
        {
            var context = string.Equals(section.Heading, title, StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{title} > {section.Heading}";
            var prefix = context + "\n";

            var whole = prefix + section.Body;
            if (EstimateTokens(whole) <= options.MaxTokens)
            {
                chunks.Add(new KnowledgeChunkDraft(chunks.Count, section.Heading, whole, EstimateTokens(whole)));
                continue;
            }

            var budget = Math.Max(MinBodyChars, options.TargetTokens * CharsPerToken - prefix.Length);
            var overlap = options.OverlapTokens * CharsPerToken;
            foreach (var piece in Pack(Atomize(section.Body, budget), budget, overlap))
            {
                var text = prefix + piece;
                chunks.Add(new KnowledgeChunkDraft(chunks.Count, section.Heading, text, EstimateTokens(text)));
            }
        }

        return chunks;
    }

    private sealed record Section(string Heading, string Body);

    /// <summary>
    /// Sections in document order. The heading is the path of the enclosing headings (<c>Parent &gt; Child</c>) without the
    /// ones that repeat the title; text before the first heading belongs to the title. Sections with no body are dropped.
    /// Lines inside code fences are never headings.
    /// </summary>
    private static List<Section> ReadSections(string title, string body)
    {
        var sections = new List<Section>();
        var path = new List<(int Level, string Text)>();
        var current = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();
            if (text.Length == 0)
                return;

            var names = path.Select(p => p.Text).Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase)).ToList();
            sections.Add(new Section(names.Count == 0 ? title : string.Join(" > ", names), text));
        }

        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && HeadingLine().Match(line) is { Success: true } heading)
            {
                Flush();
                var level = heading.Groups[1].Length;
                path.RemoveAll(p => p.Level >= level);
                path.Add((level, heading.Groups[2].Value.Trim()));
                continue;
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into units of at most <paramref name="maxChars"/>, keeping each separator at the end of
    /// the unit it closes, so joining the units gives the text back. Tries paragraphs, then lines, sentences and words,
    /// and cuts blindly only for a single "word" longer than the budget.
    /// </summary>
    private static List<string> Atomize(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return [text];

        foreach (var boundary in Boundaries)
        {
            var parts = boundary(text);
            if (parts.Count > 1)
                return [.. parts.SelectMany(p => Atomize(p, maxChars))];
        }

        return HardCut(text, maxChars);
    }

    private static readonly Func<string, List<string>>[] Boundaries =
    [
        text => SplitAfter(text, "\n\n"),
        text => SplitAfter(text, "\n"),
        text => SplitAfterMatches(text, SentenceEnd()),
        text => SplitAfter(text, " "),
    ];

    private static List<string> SplitAfter(string text, string separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var at = text.IndexOf(separator, StringComparison.Ordinal); at >= 0; at = text.IndexOf(separator, start, StringComparison.Ordinal))
        {
            parts.Add(text[start..(at + separator.Length)]);
            start = at + separator.Length;
        }

        if (start < text.Length)
            parts.Add(text[start..]);
        return parts;
    }

    private static List<string> SplitAfterMatches(string text, Regex boundary)
    {
        var parts = new List<string>();
        var start = 0;
        foreach (Match match in boundary.Matches(text))
        {
            parts.Add(text[start..(match.Index + match.Length)]);
            start = match.Index + match.Length;
        }

        if (start < text.Length)
            parts.Add(text[start..]);
        return parts;
    }

    private static List<string> HardCut(string text, int maxChars)
    {
        var parts = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + maxChars);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
                end--;
            parts.Add(text[start..end]);
            start = end;
        }

        return parts;
    }

    /// <summary>Greedy packing of units into pieces of at most <paramref name="maxChars"/>; each piece after the first starts with the tail of the one before.</summary>
    private static List<string> Pack(List<string> units, int maxChars, int overlapChars)
    {
        var pieces = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            var piece = buffer.ToString().Trim();
            if (piece.Length > 0)
                pieces.Add(piece);
            buffer.Clear();
        }

        foreach (var unit in units)
        {
            if (buffer.Length > 0 && buffer.Length + unit.Length > maxChars)
            {
                Flush();
                var tail = pieces.Count > 0 ? Tail(pieces[^1], Math.Min(overlapChars, maxChars - unit.Length)) : "";
                if (tail.Length > 0)
                    buffer.Append(tail).Append(' ');
            }

            buffer.Append(unit);
        }

        Flush();
        return pieces;
    }

    /// <summary>The last <paramref name="max"/> characters of <paramref name="text"/> without a cut word at the start; empty if there is no room or no word boundary.</summary>
    private static string Tail(string text, int max)
    {
        if (max <= 0 || text.Length <= max)
            return "";

        var tail = text[^max..];
        var space = tail.IndexOfAny([' ', '\n', '\t']);
        return space < 0 || space == tail.Length - 1 ? "" : tail[(space + 1)..].TrimStart();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceEnd();
}
