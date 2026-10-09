using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chatbot.Evals.Checking;

/// <summary>How the harness looks for text in a reply: case, accents and typographic apostrophes do not matter.</summary>
internal static partial class TextMatch
{
    /// <summary>
    /// How many characters before a forbidden phrase are read to see whether it is hedged. A clause
    /// ("it is not confirmed yet", "once it is confirmed by the business") is short.
    /// </summary>
    private const int HedgeWindow = 30;

    /// <summary>
    /// Words that turn a mention of "confirmed" into the honest thing to say: a negation ("not confirmed yet") or a
    /// future condition ("once the business confirms it, you will be notified"). Matched on normalized text.
    /// </summary>
    [GeneratedRegex(@"(?:\b(?:not|never|no|nor|once|until|when|after|if|unless|till|will be|to be|as soon as|aun no|todavia no|nunca|ni|hasta que|cuando|una vez|apenas|en cuanto|sera|seran|se confirmara)\b|n't\b)")]
    private static partial Regex Hedge();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"^\d+(?:[.,]\d+)*$")]
    private static partial Regex NumberOnly();

    /// <summary>Lowercase, without accents (ñ becomes n), straight apostrophes, single spaces.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(c is '\u2019' or '\u2018' ? '\'' : char.ToLowerInvariant(c));
        }

        return WhiteSpace().Replace(builder.ToString(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    /// <summary>
    /// True when <paramref name="reply"/> mentions <paramref name="text"/>. A text made only of digits ("80") is
    /// compared as a number, so it does not match inside "180", and "$80.00" or "80,00" do match it.
    /// </summary>
    public static bool Mentions(string reply, string text)
    {
        text = text.Trim();
        if (NumberOnly().IsMatch(text) && ParseNumber(text) is { } number)
            return MentionsNumber(reply, number);

        return Normalize(reply).Contains(Normalize(text), StringComparison.Ordinal);
    }

    /// <summary>True when some number in <paramref name="reply"/> equals <paramref name="value"/> ("320,000", "320.000" and "320000" are the same).</summary>
    public static bool MentionsNumber(string reply, decimal value) =>
        NumberToken().Matches(reply).Any(m => ParseNumber(m.Value) == value);

    /// <summary>True when the reply contains <paramref name="phrase"/> outside a negation or a future condition (see <see cref="Hedge"/>).</summary>
    public static bool ContainsUnhedged(string reply, string phrase)
    {
        var text = Normalize(reply);
        var needle = Normalize(phrase);

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            var before = text[Math.Max(0, at - HedgeWindow)..at];
            var clauseStart = before.LastIndexOfAny(['.', '!', '?', ':', ';', '\n', ',']);
            if (!Hedge().IsMatch(before[(clauseStart + 1)..]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// "1,234.5", "1.234,5", "320,000", "320.000", "12.50" and "12,5". One separator that is repeated or followed by
    /// exactly three digits is a thousands separator; otherwise it is the decimal point.
    /// </summary>
    private static decimal? ParseNumber(string token)
    {
        var lastDot = token.LastIndexOf('.');
        var lastComma = token.LastIndexOf(',');
        string normalized;

        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalAt = Math.Max(lastDot, lastComma);
            normalized = token[..decimalAt].Replace(".", "").Replace(",", "") + "." + token[(decimalAt + 1)..];
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var separator = lastDot >= 0 ? '.' : ',';
            var last = Math.Max(lastDot, lastComma);
            var repeated = token.Count(c => c == separator) > 1;
            var thousands = repeated || token.Length - last - 1 == 3;
            normalized = thousands ? token.Replace(separator.ToString(), "") : token.Replace(separator, '.');
        }
        else
        {
            normalized = token;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
