using System.Text.Json;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Data;

namespace Chatbot.Tests.Tools;

[Collection(PostgresCollection.Name)]
public class CatalogToolsTests(PostgresFixture postgres)
{
    private static string[] Names(JsonElement result) =>
        result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task Search_FilterByNeighborhoodAndRooms_ReturnsOnlyMatches()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"filters":{"neighborhood":"Palermo","rooms":2}}""");

        Assert.Equal(["2-room apartment in Palermo"], Names(result));
        var first = result.GetProperty("results")[0];
        Assert.Equal(world.Palermo2.Id, first.GetProperty("offeringId").GetGuid());
        Assert.Equal(550, first.GetProperty("price").GetDecimal());
        Assert.Equal("Palermo", first.GetProperty("attributes").GetProperty("neighborhood").GetString());
    }

    [Fact]
    public async Task Search_ListAttribute_MatchesWhenItContainsTheValue()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var asString = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"filters":{"features":"balcony"}}""");
        var asList = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"filters":{"features":["balcony","low HOA fees"]}}""");

        Assert.Equal(["2-room apartment in Palermo"], Names(asString));
        Assert.Equal(["2-room apartment in Palermo"], Names(asList));
    }

    [Fact]
    public async Task Search_MaxAndMinPrice_AreInclusiveAndOrderedByPrice()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var cheap = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"maxPrice":700}""");
        var mid = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"minPrice":600,"maxPrice":1000}""");

        Assert.Equal(["2-room apartment in Palermo", "3-room apartment in Palermo"], Names(cheap));
        Assert.Equal(["3-room apartment in Palermo"], Names(mid));
    }

    [Fact]
    public async Task Search_GlutenFreeBooleanFilter_FindsOnlyGlutenFreeItems()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.B);

        var result = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"filters":{"glutenFree":true}}""");

        Assert.Equal(["Garden Salad"], Names(result));
    }

    [Fact]
    public async Task Search_QueryIsCaseInsensitiveAndEveryWordMustMatch()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.B);

        var both = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"query":"PIZZA"}""");
        var narrowed = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"query":"pepperoni pizza"}""");
        var wildcardIsLiteral = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"query":"%"}""");

        Assert.Equal(["Margherita Pizza", "Pepperoni Pizza"], Names(both));
        Assert.Equal(["Pepperoni Pizza"], Names(narrowed));
        Assert.Empty(Names(wildcardIsLiteral));
    }

    [Fact]
    public async Task Search_OnlyActiveOfferingsOfTheSessionBusiness_AtMostFive()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using (var db = world.NewDb())
        {
            for (var i = 0; i < 6; i++)
            {
                db.Offerings.Add(new Chatbot.Core.Domain.Offering
                {
                    BusinessId = world.A.Id,
                    Kind = Chatbot.Core.Domain.OfferingKind.Property,
                    Name = $"Extra {i}",
                    Price = 1000 + i,
                });
            }

            await db.SaveChangesAsync();
        }

        await using var session = await world.NewSessionAsync(world.A);
        var result = await session.CallAsync(AgentToolFactory.SearchCatalog);

        Assert.Equal(5, result.GetProperty("shown").GetInt32());
        Assert.Equal(9, result.GetProperty("totalMatches").GetInt32()); // 3 active A offerings + 6 extras; B and the retired one excluded
        var names = Names(result);
        Assert.DoesNotContain("Retired flat in Palermo", names);
        Assert.DoesNotContain("Pepperoni Pizza", names);
    }

    [Fact]
    public async Task Search_NoMatch_ExplainsHowToRetry()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"filters":{"neighborhood":"palermo"}}""");

        Assert.Empty(Names(result));
        Assert.Contains("exact", result.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("""{"kind":"Spaceship"}""", "invalid_kind")]
    [InlineData("""{"minPrice":10,"maxPrice":5}""", "invalid_price")]
    [InlineData("""{"minPrice":-1}""", "invalid_price")]
    [InlineData("""{"filters":{"neighborhood":null}}""", "invalid_filter")]
    [InlineData("""{"filters":{"rooms":{"min":2}}}""", "invalid_filter")]
    [InlineData("""{"filters":{"":"x"}}""", "invalid_filter")]
    public async Task Search_InvalidArguments_ReturnReadableError(string arguments, string code)
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.SearchCatalog, arguments);

        Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Search_KindAcceptsLooseSpelling()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.B);

        var result = await session.CallAsync(AgentToolFactory.SearchCatalog, """{"kind":"menu item"}""");

        Assert.Equal(3, result.GetProperty("totalMatches").GetInt32());
    }

    [Fact]
    public async Task Details_OwnOffering_ReturnsFullObject()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.GetOfferingDetails, $$"""{"offeringId":"{{world.Belgrano4.Id}}"}""");

        Assert.Equal("4-room house in Belgrano", result.GetProperty("name").GetString());
        Assert.Equal("sale", result.GetProperty("priceUnit").GetString());
        Assert.Equal("USD", result.GetProperty("currency").GetString());
        Assert.Equal(4, result.GetProperty("attributes").GetProperty("rooms").GetInt32());
        Assert.Contains("Great choice", result.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Details_OfferingOfAnotherBusinessOrInactive_IsNotFound()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var other = await session.CallAsync(AgentToolFactory.GetOfferingDetails, $$"""{"offeringId":"{{world.Pepperoni.Id}}"}""");
        var retired = await session.CallAsync(AgentToolFactory.GetOfferingDetails, $$"""{"offeringId":"{{world.Retired.Id}}"}""");
        var garbage = await session.CallAsync(AgentToolFactory.GetOfferingDetails, """{"offeringId":"not-an-id"}""");
        var missing = await session.CallAsync(AgentToolFactory.GetOfferingDetails);

        Assert.Equal("offering_not_found", other.GetProperty("code").GetString());
        Assert.Equal("offering_not_found", retired.GetProperty("code").GetString());
        Assert.Equal("invalid_offering_id", garbage.GetProperty("code").GetString());
        Assert.Equal("invalid_offering_id", missing.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Factory_ExposesTheFiveToolsWithoutBusinessOrSessionParameters()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var functions = session.Tools.OfType<Microsoft.Extensions.AI.AIFunction>().ToList();

        Assert.Equal(
            ["check_availability", "create_booking", "get_offering_details", "handoff_to_human", "search_catalog"],
            functions.Select(f => f.Name).Order().ToArray());
        Assert.All(functions, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
            var schema = f.JsonSchema.GetRawText().ToLowerInvariant();
            Assert.DoesNotContain("businessid", schema);
            Assert.DoesNotContain("sessionid", schema);
        });
    }
}
