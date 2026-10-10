using System.Text.Json;
using Chatbot.Evals.Checking;

namespace Chatbot.Tests.Evals;

public class TextMatchTests
{
    [Theory]
    [InlineData("A cleaning is $80.", "80", true)]
    [InlineData("A cleaning is $80.00 today", "80", true)]
    [InlineData("A cleaning is 80,00 USD", "80", true)]
    [InlineData("Whitening is $180", "80", false)]
    [InlineData("Whitening is $180", "180", true)]
    [InlineData("The house is $320,000", "320000", true)]
    [InlineData("La casa sale 320.000 dólares", "320000", true)]
    [InlineData("The house is $320,000", "320", false)]
    [InlineData("Total: 27.5", "27.50", true)]
    [InlineData("Total: 27,5", "27.5", true)]
    [InlineData("Pay in 2 installments", "2", true)]
    [InlineData("The fee is 45 dollars", "$45", true)] // the symbol is not compared, the number is
    [InlineData("El cargo es de $45,00.", "$45", true)]
    [InlineData("It is $145", "$45", false)]
    [InlineData("Up to 80 % less radiation", "80%", true)]
    [InlineData("Delivery is $4.75", "$4.75", true)]
    [InlineData("Delivery is $4", "$4.75", false)]
    [InlineData("Hipotecas Norte cobra 0.5% del préstamo", "0,5", true)]
    public void Numbers_are_compared_by_value(string reply, string wanted, bool expected) =>
        Assert.Equal(expected, TextMatch.Mentions(reply, wanted));

    [Theory]
    [InlineData("¡Claro! El Núñez es lindo", "nunez", true)]
    [InlineData("Pending CONFIRMATION by the business", "pending confirmation", true)]
    [InlineData("It’s ready", "it's ready", true)]
    [InlineData("Nothing here", "pending", false)]
    public void Text_ignores_case_accents_and_typographic_apostrophes(string reply, string wanted, bool expected) =>
        Assert.Equal(expected, TextMatch.Mentions(reply, wanted));

    [Theory]
    // Claims.
    [InlineData("Your appointment is confirmed!", true)]
    [InlineData("Great, it's confirmed. See you!", true)]
    [InlineData("No problem! Your visit is confirmed.", true)]
    [InlineData("Your booking has no issues and is confirmed.", true)] // the "no" negates something else
    [InlineData("Perfect, when you arrive your table is confirmed", true)] // the "when" governs "arrive"
    [InlineData("Once you arrive your table is confirmed", true)]
    [InlineData("It was not available yesterday. Now it is confirmed.", true)] // a hedge in an earlier sentence
    [InlineData("Sorry, your table is confirmed", true)]
    [InlineData("There is nothing to worry about, your visit is now confirmed", true)]
    // Honest mentions.
    [InlineData("It is not confirmed yet.", false)]
    [InlineData("Not confirmed yet, but soon.", false)]
    [InlineData("It is not yet confirmed.", false)]
    [InlineData("It hasn't been confirmed.", false)]
    [InlineData("It has never been confirmed by anyone.", false)]
    [InlineData("It isn't confirmed until the business reviews it.", false)]
    [InlineData("It can't be confirmed from here.", false)]
    [InlineData("It will be confirmed by the clinic.", false)]
    [InlineData("It'll be confirmed by the clinic soon.", false)]
    [InlineData("The status is still to be confirmed.", false)]
    [InlineData("You will be notified once it is confirmed by the business.", false)]
    [InlineData("You will be notified once the booking is confirmed.", false)]
    [InlineData("We call you when it is confirmed.", false)]
    [InlineData("It stays pending until the business has confirmed.", false)]
    [InlineData("Unless it is confirmed by the clinic, it is only registered.", false)]
    [InlineData("The request is registered and pending confirmation.", false)]
    [InlineData("Once we set up a booking, it goes to our team as pending confirmation rather than instantly confirmed.", false)]
    [InlineData("It is registered rather than confirmed.", false)]
    [InlineData("It is pending instead of being confirmed right away.", false)]
    [InlineData("It is not instantly confirmed.", false)]
    [InlineData("Rather than waiting, your visit is confirmed.", true)] // the contrast is about waiting, not about the phrase
    [InlineData("Instead of calling, just know it is confirmed.", true)]
    public void Forbidden_phrase_is_found_unless_a_hedge_governs_it(string reply, bool flagged) =>
        Assert.Equal(flagged, TextMatch.ContainsUnhedged(reply, "confirmed"));

    [Theory]
    // Claims.
    [InlineData("Tu reserva está confirmada.", true)]
    [InlineData("Tu reserva no tiene problemas y está confirmada.", true)]
    [InlineData("Listo, ya está confirmada, te esperamos.", true)]
    [InlineData("Cuando llegues tu mesa está confirmada.", true)]
    [InlineData("Si, tu reserva esta confirmada.", true)] // "sí" (yes) is not the conditional "si"
    // Honest mentions.
    [InlineData("Todavía no está confirmada.", false)]
    [InlineData("Queda pendiente, aún no está confirmada.", false)]
    [InlineData("Aún no ha sido confirmada por la clínica.", false)]
    [InlineData("Nunca fue confirmada.", false)]
    [InlineData("Te avisamos cuando esté confirmada.", false)]
    [InlineData("Te avisamos cuando la reserva esté confirmada.", false)]
    [InlineData("Queda pendiente en lugar de confirmada.", false)]
    [InlineData("Queda registrada y no confirmada.", false)]
    [InlineData("Queda pendiente en vez de estar confirmada de inmediato.", false)]
    [InlineData("En lugar de esperar, tu turno está confirmada.", true)]
    [InlineData("Una vez que la clínica la tenga confirmada te llegará un aviso.", false)]
    [InlineData("Será confirmada por la clínica.", false)]
    [InlineData("Está pendiente de ser confirmada por el negocio.", false)]
    [InlineData("Hasta que esté confirmada no cuenta como reservada.", false)]
    public void Spanish_claims_are_found_and_negations_futures_and_conditions_are_not(string reply, bool flagged) =>
        Assert.Equal(flagged, TextMatch.ContainsUnhedged(reply, "confirmada"));

    [Theory]
    [InlineData("Your booking is unconfirmed until the business replies.", "confirmed", false)]
    [InlineData("It is un-confirmed for now.", "confirmed", false)]
    [InlineData("Your booking is unconfirmed, but it is confirmed.", "confirmed", true)]
    [InlineData("Está sin confirmar, no hay nada reconfirmada.", "confirmada", false)]
    [InlineData("The Margherita is $12", "Margherita", true)]
    [InlineData("Margheritas are great", "Margherita", false)]
    [InlineData("The cleaning is $80.", "$80", true)]
    [InlineData("Whitening is $800 today", "$80", false)]
    [InlineData("It costs US$80.", "$80", true)] // the phrase starts with a symbol: no boundary needed in front
    [InlineData("Orthodontics: braces cost $50", "braces cost $", true)]
    [InlineData("We do not have sushi or ramen.", "have sushi", false)] // negated by "not"
    [InlineData("Yes, we have sushi.", "have sushi", true)]
    public void Phrases_match_whole_words(string reply, string phrase, bool flagged) =>
        Assert.Equal(flagged, TextMatch.ContainsUnhedged(reply, phrase));
}

public class JsonPathTests
{
    private static readonly JsonElement Result = JsonDocument.Parse("""
        { "totalMatches": 2, "results": [ { "name": "A", "price": 550 }, { "name": "B", "price": 650 } ], "Total": 31 }
        """).RootElement;

    [Theory]
    [InlineData("totalMatches", "2")]
    [InlineData("total", "31")] // names ignore case
    [InlineData("results[*].price", "550,650")]
    [InlineData("results[1].name", "B")]
    [InlineData("results[5].name", "")]
    [InlineData("results.price", "")] // an array needs an index
    [InlineData("missing", "")]
    public void Selects_values(string path, string expected) =>
        Assert.Equal(expected, string.Join(",", JsonPath.Select(Result, path).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())));

    [Theory]
    [InlineData("results[*].price", true)]
    [InlineData("a.b[0][1].c", true)]
    [InlineData("results[", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    [InlineData("results[x]", false)]
    public void Validates_paths(string path, bool valid) => Assert.Equal(valid, JsonPath.IsValid(path));
}

public class JsonMatchTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"maxPrice":600}""", """{"maxPrice":600,"query":"palermo"}""", true)]
    [InlineData("""{"maxPrice":600}""", """{"maxPrice":"600"}""", true)]
    [InlineData("""{"maxPrice":600}""", """{"maxPrice":550}""", false)]
    [InlineData("""{"maxPrice":600}""", """{"minPrice":600}""", false)]
    [InlineData("""{"query":"pepperoni"}""", """{"query":"Pepperoni pizza"}""", true)]
    [InlineData("""{"filters":{"neighborhood":"Palermo"}}""", """{"filters":{"neighborhood":"palermo","rooms":2}}""", true)]
    [InlineData("""{"filters":{"neighborhood":"Palermo"}}""", """{"query":"Palermo"}""", false)]
    [InlineData("""{"glutenFree":true}""", """{"glutenFree":"true"}""", true)]
    [InlineData("""{"glutenFree":true}""", """{"glutenFree":false}""", false)]
    [InlineData("""{"features":"balcony"}""", """{"features":["pool","balcony"]}""", true)]
    [InlineData("""{"items":[{"qty":2}]}""", """{"items":[{"offeringId":"x","qty":1},{"offeringId":"y","qty":2}]}""", true)]
    [InlineData("""{"items":[{"qty":3}]}""", """{"items":[{"offeringId":"x","qty":1},{"offeringId":"y","qty":2}]}""", false)]
    [InlineData("""{"customerName":"Maria Gomez"}""", """{"customerName":"María Gómez"}""", true)]
    public void Matches_expected_arguments_as_a_subset(string expected, string actual, bool matches) =>
        Assert.Equal(matches, JsonMatch.IsSubset(J(expected), J(actual)));
}
