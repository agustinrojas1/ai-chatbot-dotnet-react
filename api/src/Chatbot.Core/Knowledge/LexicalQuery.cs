using System.Text.RegularExpressions;

namespace Chatbot.Core.Knowledge;

/// <summary>
/// Turns a natural-language question into the text given to PostgreSQL <c>websearch_to_tsquery('simple', ...)</c>, which ANDs
/// its words: a chunk matches only if it contains EVERY content word of the question. That is deliberate. The lexical side
/// exists for exact lookups ("OSDE 210", "ZX-9981", a phone number, an amount), where it is precise; an OR query over a whole
/// sentence matches almost every chunk through common words and, fused with the semantic ranking, pushed the right document
/// out of the top 4 in the retrieval probe (recall@4 0.86 against 1.00, ADR-016). A long question that no single chunk contains
/// word for word gives no lexical hits and the semantic ranking decides alone.
/// Common words of both languages are dropped (the config is <c>simple</c>: no stop words, no stemming), otherwise one of
/// them missing in a chunk would reject it. Numbers, amounts and acronyms ("OSDE", "210", "48") are kept.
/// </summary>
public static partial class LexicalQuery
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English (includes "or" and "and": operators of websearch_to_tsquery must never come from the user)
        "a", "an", "the", "is", "are", "was", "were", "be", "do", "does", "did", "can", "could", "would", "should", "will", "may",
        "i", "you", "we", "they", "he", "she", "it", "my", "your", "our", "me", "us", "of", "to", "in", "on", "at", "for", "from",
        "with", "about", "and", "or", "not", "no", "if", "what", "which", "who", "when", "where", "how", "why", "there", "this",
        "that", "have", "has", "any", "some", "much", "many", "than", "then", "so", "as", "by", "into", "up", "out",
        // Spanish
        "el", "la", "los", "las", "un", "una", "unos", "unas", "de", "del", "al", "y", "o", "e", "u", "en", "con", "sin", "por",
        "para", "que", "qué", "cual", "cuál", "cuales", "cuáles", "quien", "quién", "cuando", "cuándo", "donde", "dónde", "como",
        "cómo", "cuanto", "cuánto", "cuánta", "cuántos", "es", "son", "ser", "hay", "tienen", "tiene", "tengo", "puedo", "puede",
        "pueden", "se", "me", "mi", "mis", "su", "sus", "lo", "le", "les", "si", "no", "ya", "más", "mas", "muy", "pero", "este",
        "esta", "esto", "ese", "esa", "hacen", "hace", "hago", "tienen", "ustedes", "usted", "nos", "nuestro", "nuestra",
    };

    /// <summary>The text for <c>websearch_to_tsquery</c>, or null when nothing but common words is left (no lexical search then).</summary>
    public static string? Build(string question)
    {
        var terms = Word().Matches(question)
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .Take(MaxTerms)
            .ToList();

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    /// <summary>A bound on the query size; a question has far fewer content words.</summary>
    public const int MaxTerms = 12;

    // Letters and digits, with a dot, comma, apostrophe or hyphen between two of them ("1,500", "9.99", "24-hour", "o'clock").
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[.,'’\-][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
