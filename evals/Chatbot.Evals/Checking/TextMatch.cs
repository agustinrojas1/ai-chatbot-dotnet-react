using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chatbot.Evals.Checking;

/// <summary>How the harness looks for text in a reply: case, accents and typographic apostrophes do not matter.</summary>
internal static partial class TextMatch
{
    /// <summary>How many characters before a forbidden phrase are read to look for the hedge that governs it.</summary>
    private const int HedgeWindow = 70;

    // A hedge makes a mention of "confirmed" the honest thing to say: a negation ("not confirmed yet"), a future
    // ("will be confirmed by the clinic") or a condition ("once it is confirmed"). It only counts when it GOVERNS the
    // phrase: everything between the hedge and the phrase may be an auxiliary or an adverb, nothing else. So
    // "no issues and is confirmed" is a claim (a "no" that negates something else), and "not confirmed yet" is not.
    // The patterns are matched at the END of the clause before the phrase, on normalized text (see Normalize).
    private const string EnglishFiller = @"(?:yet|been|be|being|really|fully|actually|officially|formally|still|currently|already|ever|then|also|just|only|soon|finally|instantly|immediately|automatically|get|gets|got|have|has|had|is|are|was|were|will)";
    private const string SpanishFiller = @"(?:esta|estan|este|esten|sea|sean|haya|hayan|ha|han|tenga|tengan|sido|fue|fueron|es|son|queda|quedo|quedan|quede|queden|sera|seran|se|encuentra|ya|aun|todavia|siendo|estar|oficialmente|finalmente|formalmente|inmediatamente|automaticamente|instantaneamente|recien|del|todo)";

    private static readonly Regex HedgeBefore = new(
        // Negation: "not (yet) confirmed", "isn't confirmed", "has never been confirmed", "no está confirmada", "aún no ha sido confirmada".
        $@"(?:\b(?:not|never|cannot)|n't|\bno longer)(?:\s+{EnglishFiller})*$"
        + $@"|\b(?:no|nunca|jamas|tampoco)(?:\s+{SpanishFiller})*$"
        + @"|\b(?:un|non)-$"
        // Contrast: "pending confirmation rather than instantly confirmed", "instead of being confirmed", "en lugar de confirmada".
        + @"|\b(?:rather than|instead of|as opposed to)(?:\s+(?:being|getting|simply|just|already|instantly|immediately|automatically))*$"
        + @"|\b(?:en lugar de|en vez de|y no|antes que)(?:\s+(?:ser|estar|quedar|ya|inmediatamente|automaticamente|instantaneamente))*$"
        // Future: "will be confirmed", "to be confirmed", "should be confirmed", "será confirmada", "pendiente de ser confirmada".
        + @"|(?:\b(?:will|would|shall|should|can|could|may|might|must|to)|'ll|'d)(?:\s+(?:also|then|soon|only|first|still|just|really|actually|not|never))*\s+be(?:\s+(?:officially|fully|then|formally|finally))*$"
        + @"|\b(?:sera|seran|quedara|quedaran|estara|estaran)(?:\s+(?:oficialmente|luego|finalmente|formalmente))*$"
        + @"|\b(?:de|para|a|por|debe|deben|debera|puede|podra|podria|va a|vaya a)\s+ser(?:\s+(?:oficialmente|finalmente|formalmente))*$"
        // Condition: "once (it) is confirmed", "until the business has confirmed", "cuando esté confirmada". A short
        // subject may sit between ("once the booking is"), a whole clause may not ("when you arrive your table is").
        + $@"|\b(?:once|until|till|unless|if|after|as soon as|as long as|in case|provided that)(?:\s+\S+){{0,3}}(?:\s+{EnglishFiller})*$"
        + $@"|\b(?:when|whenever)(?:\s+\S+){{0,2}}(?:\s+{EnglishFiller})*$"
        + $@"|\b(?:una vez(?: que)?|hasta que|luego de que|despues de que|siempre que|a menos que|apenas|en cuanto)(?:\s+\S+){{0,3}}(?:\s+{SpanishFiller})*$"
        + $@"|\bcuando(?:\s+\S+){{0,2}}(?:\s+{SpanishFiller})*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberToken();

    /// <summary>"80", "$45", "€ 3,50", "80%": an amount or a percentage. The symbol is not compared, only the number.</summary>
    [GeneratedRegex(@"^[$€£]?\s?(\d+(?:[.,]\d+)*)\s?%?$")]
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
    /// compared as a number (a leading currency symbol or a trailing % is ignored), so it does not match inside "180", and "$80.00" or "80,00" do match it.
    /// </summary>
    public static bool Mentions(string reply, string text)
    {
        text = text.Trim();
        if (NumberOnly().Match(text) is { Success: true } amount && ParseNumber(amount.Groups[1].Value) is { } number)
            return MentionsNumber(reply, number);

        return Normalize(reply).Contains(Normalize(text), StringComparison.Ordinal);
    }

    /// <summary>True when some number in <paramref name="reply"/> equals <paramref name="value"/> ("320,000", "320.000" and "320000" are the same).</summary>
    public static bool MentionsNumber(string reply, decimal value) =>
        NumberToken().Matches(reply).Any(m => ParseNumber(m.Value) == value);

    /// <summary>
    /// True when <paramref name="reply"/> contains <paramref name="phrase"/> as a whole word (or words: "unconfirmed"
    /// is not "confirmed") and no hedge in front of it governs it (see <see cref="HedgeBefore"/>).
    /// </summary>
    public static bool ContainsUnhedged(string reply, string phrase)
    {
        var text = Normalize(reply);
        foreach (Match match in WholeWord(Normalize(phrase)).Matches(text))
        {
            var before = text[Math.Max(0, match.Index - HedgeWindow)..match.Index].TrimEnd();
            var clauseStart = before.LastIndexOfAny(['.', '!', '?', ':', ';', '\n', ',', '(', ')']);
            if (!HedgeBefore.IsMatch(before[(clauseStart + 1)..].TrimStart()))
                return true;
        }

        return false;
    }

    /// <summary>The phrase, not inside a longer word or number. Only an edge that is a letter or digit needs the check ("$80" ends a number, not a word).</summary>
    private static Regex WholeWord(string normalizedPhrase)
    {
        var first = char.IsLetterOrDigit(normalizedPhrase[0]) ? @"(?<![\p{L}\p{N}])" : "";
        var last = char.IsLetterOrDigit(normalizedPhrase[^1]) ? @"(?![\p{L}\p{N}])" : "";
        return new Regex(first + Regex.Escape(normalizedPhrase) + last, RegexOptions.CultureInvariant);
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
