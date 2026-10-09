using Chatbot.Core.Agent;

namespace Chatbot.Tests.Agent;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("Hola, quiero ver departamentos", Language.Spanish)]
    [InlineData("¿Cuánto cuesta el menú del día?", Language.Spanish)]
    [InlineData("Necesito un turno para mañana", Language.Spanish)]
    [InlineData("Gracias!", Language.Spanish)]
    [InlineData("I want to book a visit tomorrow", Language.English)]
    [InlineData("What is the price of the 2-room apartment?", Language.English)]
    [InlineData("Hello", Language.English)]
    [InlineData("Can you show me the menu please", Language.English)]
    public void Detects_the_language_of_clear_messages(string text, Language expected)
    {
        // The previous language is the opposite one, so the result cannot come from the fallback.
        var previous = expected == Language.Spanish ? Language.English : Language.Spanish;

        Assert.Equal(expected, LanguageDetector.Detect(text, previous));
    }

    [Theory]
    [InlineData("ok", Language.Spanish)]
    [InlineData("ok", Language.English)]
    [InlineData("2", Language.English)]
    [InlineData("2", Language.Spanish)]
    [InlineData("👍", Language.Spanish)]
    [InlineData("", Language.Spanish)]
    [InlineData("5555-4444", Language.Spanish)]
    public void Ambiguous_text_keeps_the_previous_language(string text, Language previous)
    {
        Assert.Equal(previous, LanguageDetector.Detect(text, previous));
    }

    [Fact]
    public void Ambiguous_text_without_previous_language_defaults_to_English()
    {
        Assert.Equal(Language.English, LanguageDetector.Detect("ok", null));
        Assert.Equal(Language.English, LanguageDetector.Detect("2", null));
    }

    [Fact]
    public void A_clear_message_beats_the_previous_language()
    {
        Assert.Equal(Language.English, LanguageDetector.Detect("I want to book a visit tomorrow", Language.Spanish));
        Assert.Equal(Language.Spanish, LanguageDetector.Detect("Hola, quiero ver departamentos", Language.English));
    }

    [Fact]
    public void Mixed_message_follows_the_dominant_language()
    {
        Assert.Equal(Language.Spanish, LanguageDetector.Detect("quiero un deposit please", Language.English));
        Assert.Equal(Language.English, LanguageDetector.Detect("I want to see the departamento", Language.Spanish));
    }

    [Fact]
    public void Language_switch_mid_conversation()
    {
        Language? current = null;
        string[] turns = ["Hi, do you have 2-room apartments?", "ok", "Perfecto, ¿puedo verlo mañana?", "2", "Sorry, can I do it on Friday instead?"];
        Language[] expected = [Language.English, Language.English, Language.Spanish, Language.Spanish, Language.English];

        for (int i = 0; i < turns.Length; i++)
        {
            current = LanguageDetector.Detect(turns[i], current);
            Assert.Equal(expected[i], current);
        }
    }

    [Fact]
    public void Spanish_only_characters_are_enough()
    {
        Assert.Equal(Language.Spanish, LanguageDetector.Detect("Año", Language.English));
        Assert.Equal(Language.Spanish, LanguageDetector.Detect("¡Genial!", Language.English));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(Language.Spanish, LanguageDetector.Detect("HOLA QUIERO RESERVAR", Language.English));
        Assert.Equal(Language.English, LanguageDetector.Detect("THE PRICE PLEASE", Language.Spanish));
    }
}
