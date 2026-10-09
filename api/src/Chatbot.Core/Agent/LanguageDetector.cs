using System.Collections.Frozen;

namespace Chatbot.Core.Agent;

public enum Language
{
    English,
    Spanish,
}

/// <summary>
/// Deterministic English/Spanish detector (SPEC 7.3). Scores function words and Spanish-only characters;
/// the language with more points wins, and a tie (including "ok", "2", "👍") keeps the previous language.
/// </summary>
public static class LanguageDetector
{
    // Words that exist in only one of the two languages. Shared spellings ("a", "me", "no", "he", "si") are left out
    // on purpose: they would score for both sides and only add noise.
    private static readonly FrozenSet<string> EnglishWords = new[]
    {
        "the", "and", "is", "are", "was", "were", "be", "am", "i", "you", "we", "it", "they", "my", "your", "our",
        "this", "that", "these", "those", "what", "which", "who", "how", "when", "where", "why", "do", "does", "did",
        "can", "could", "would", "should", "will", "want", "wanna", "like", "need", "have", "has", "there", "any",
        "some", "of", "in", "on", "at", "to", "for", "with", "from", "about", "an", "or", "but", "not", "if",
        "hello", "hi", "hey", "please", "thanks", "thank", "yes", "yeah", "book", "booking", "visit", "tomorrow",
        "today", "tonight", "price", "much", "many", "available", "availability", "looking", "show", "tell", "see",
        "apartment", "appointment", "table", "menu", "order", "morning", "afternoon", "evening", "week", "next",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SpanishWords = new[]
    {
        "el", "la", "los", "las", "un", "una", "unos", "unas", "y", "o", "es", "soy", "eres", "está", "esta",
        "están", "estoy", "yo", "tú", "vos", "usted", "ustedes", "nosotros", "mi", "mis", "tu", "tus", "su", "sus",
        "este", "ese", "esto", "eso", "qué", "que", "cuál", "cual", "cómo", "como", "cuándo", "cuando", "dónde",
        "donde", "cuánto", "cuanto", "cuánta", "quién", "quien", "por", "para", "con", "sin", "de", "del", "al", "en",
        "se", "te", "lo", "le", "les", "nos", "ya", "muy", "más", "pero", "también", "tengo", "tiene", "tienen",
        "hay", "puedo", "puede", "podés", "puedes", "podría", "quiero", "quisiera", "necesito", "busco", "gustaría",
        "hola", "buenas", "buenos", "buen", "gracias", "favor", "reservar", "reserva", "turno", "cita", "mesa",
        "hoy", "mañana", "noche", "tarde", "semana", "próxima", "proxima", "precio", "disponible", "disponibilidad",
        "departamento", "depto", "ver", "mostrar", "decime", "avisame", "sí", "cuesta", "vale", "dale",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Span-based lookups so scoring a message does not allocate a substring per word.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> EnglishLookup =
        EnglishWords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SpanishLookup =
        SpanishWords.GetAlternateLookup<ReadOnlySpan<char>>();

    public static Language Detect(string text, Language? previous)
    {
        int english = 0, spanish = 0;
        ReadOnlySpan<char> rest = text;

        while (!rest.IsEmpty)
        {
            // Skip non-letters, then take the next run of letters as a word.
            int start = 0;
            while (start < rest.Length && !char.IsLetter(rest[start]))
            {
                // Inverted punctuation only exists in Spanish: strong signal.
                if (rest[start] is '¿' or '¡')
                {
                    spanish += 2;
                }

                start++;
            }

            rest = rest[start..];
            if (rest.IsEmpty)
            {
                break;
            }

            int end = 0;
            while (end < rest.Length && char.IsLetter(rest[end]))
            {
                if (rest[end] == 'ñ' || rest[end] == 'Ñ')
                {
                    spanish += 2;
                }
                else if (rest[end] is 'á' or 'é' or 'í' or 'ó' or 'ú' or 'ü' or 'Á' or 'É' or 'Í' or 'Ó' or 'Ú')
                {
                    spanish++;
                }

                end++;
            }

            var word = rest[..end];
            if (SpanishLookup.Contains(word))
            {
                spanish++;
            }

            if (EnglishLookup.Contains(word))
            {
                english++;
            }

            rest = rest[end..];
        }

        if (spanish == english)
        {
            return previous ?? Language.English;
        }

        return spanish > english ? Language.Spanish : Language.English;
    }
}
