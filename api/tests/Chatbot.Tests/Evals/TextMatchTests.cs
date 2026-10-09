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
    [InlineData("Your appointment is confirmed!", true)]
    [InlineData("Great, it's confirmed. See you!", true)]
    [InlineData("No problem! Your visit is confirmed.", true)]
    [InlineData("Tu reserva está confirmada.", false)] // not the phrase "confirmed"
    [InlineData("It is not confirmed yet.", false)]
    [InlineData("It isn't confirmed until the business reviews it.", false)]
    [InlineData("You will be notified once it is confirmed by the business.", false)]
    [InlineData("The request is registered and pending confirmation.", false)]
    public void Forbidden_phrase_is_found_unless_it_is_negated_or_conditional(string reply, bool expected) =>
        Assert.Equal(expected, TextMatch.ContainsUnhedged(reply, "confirmed"));

    [Theory]
    [InlineData("Tu reserva está confirmada.", true)]
    [InlineData("Todavía no está confirmada.", false)]
    [InlineData("Te avisamos cuando esté confirmada.", false)]
    public void Spanish_negations_and_conditions_are_hedges_too(string reply, bool expected) =>
        Assert.Equal(expected, TextMatch.ContainsUnhedged(reply, "confirmada"));

    [Fact]
    public void A_hedge_in_an_earlier_sentence_does_not_excuse_the_phrase()
    {
        Assert.True(TextMatch.ContainsUnhedged("It was not available yesterday. Now it is confirmed.", "confirmed"));
        Assert.False(TextMatch.ContainsUnhedged("Not confirmed yet, but soon.", "confirmed"));
        Assert.True(TextMatch.ContainsUnhedged("Sorry, your table is confirmed", "confirmed"));
    }
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
