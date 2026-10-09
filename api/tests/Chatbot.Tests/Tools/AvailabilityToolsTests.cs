using System.Text.Json;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Data;

namespace Chatbot.Tests.Tools;

[Collection(PostgresCollection.Name)]
public class AvailabilityToolsTests(PostgresFixture postgres)
{
    // Business A is in Buenos Aires (UTC-3). 2026-05-19 is a Tuesday.
    private static DateTimeOffset Local(int day, int hour, int minute = 0) =>
        new(2026, 5, day, hour, minute, 0, TimeSpan.FromHours(-3));

    private static string[] Labels(JsonElement result) =>
        result.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startsAt").GetString()!).ToArray();

    private static string Range(string from = "2026-05-19", string to = "2026-05-19", string? partOfDay = null, Guid? offeringId = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["fromDate"] = from,
            ["toDate"] = to,
            ["partOfDay"] = partOfDay,
            ["offeringId"] = offeringId?.ToString(),
        }.Where(p => p.Value is not null).ToDictionary());

    [Fact]
    public async Task PartOfDay_IsAppliedInBusinessLocalTime()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        foreach (var (hour, minute) in new[] { (9, 0), (11, 30), (12, 0), (17, 30), (18, 0), (20, 0) })
            await world.AddSlotAsync(world.A, Local(19, hour, minute));
        await using var session = await world.NewSessionAsync(world.A);

        var morning = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(partOfDay: "morning"));
        var afternoon = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(partOfDay: "Afternoon"));
        var evening = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(partOfDay: "evening"));
        var any = await session.CallAsync(AgentToolFactory.CheckAvailability, Range());

        Assert.Equal(["2026-05-19T09:00:00-03:00", "2026-05-19T11:30:00-03:00"], Labels(morning));
        Assert.Equal(["2026-05-19T12:00:00-03:00", "2026-05-19T17:30:00-03:00"], Labels(afternoon));
        Assert.Equal(["2026-05-19T18:00:00-03:00", "2026-05-19T20:00:00-03:00"], Labels(evening));
        Assert.Equal(6, Labels(any).Length);
    }

    [Fact]
    public async Task Slots_AreReturnedInBusinessTimezoneWithFriendlyLabel()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 15, 30));
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, Range());

        var first = result.GetProperty("slots")[0];
        Assert.Equal(slot.Id, first.GetProperty("slotId").GetGuid());
        Assert.Equal("2026-05-19T15:30:00-03:00", first.GetProperty("startsAt").GetString());
        Assert.Equal("2026-05-19T16:00:00-03:00", first.GetProperty("endsAt").GetString());
        Assert.Equal("Tue, May 19 at 3:30 PM", first.GetProperty("label").GetString());
        Assert.Equal("America/Argentina/Buenos_Aires", result.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task OnlyFreeFutureSlotsOfTheSessionBusiness()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var free = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 2, booked: 1);
        await world.AddSlotAsync(world.A, Local(19, 11), capacity: 1, booked: 1);                 // full
        await world.AddSlotAsync(world.A, Local(18, 9));                                          // before "now" (12:00 on the 18th)
        await world.AddSlotAsync(world.B, Local(19, 12));                                         // other business
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(from: "2026-05-18", to: "2026-05-19"));

        var only = Assert.Single(result.GetProperty("slots").EnumerateArray());
        Assert.Equal(free.Id, only.GetProperty("slotId").GetGuid());
        Assert.Equal(1, only.GetProperty("spotsLeft").GetInt32());
    }

    [Fact]
    public async Task OfferingFilter_IncludesGeneralSlotsAndExcludesOtherOfferings()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var general = await world.AddSlotAsync(world.A, Local(19, 9));
        var forPalermo = await world.AddSlotAsync(world.A, Local(19, 10), offering: world.Palermo2);
        await world.AddSlotAsync(world.A, Local(19, 11), offering: world.Belgrano4);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(offeringId: world.Palermo2.Id));

        var ids = result.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("slotId").GetGuid()).ToArray();
        Assert.Equal([general.Id, forPalermo.Id], ids);
    }

    [Fact]
    public async Task ReturnsAtMostSixSlotsAndSaysThereAreMore()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        for (var i = 0; i < 8; i++)
            await world.AddSlotAsync(world.A, Local(19, 8 + i));
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, Range());

        Assert.Equal(6, Labels(result).Length);
        Assert.True(result.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task RangeOf14DaysIsAllowedAndLongerIsRejected()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var lastDay = await world.AddSlotAsync(world.A, Local(19, 10).AddDays(14));
        await using var session = await world.NewSessionAsync(world.A);

        var ok = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(from: "2026-05-19", to: "2026-06-02"));
        var tooLong = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(from: "2026-05-19", to: "2026-06-03"));

        Assert.Equal(lastDay.Id, ok.GetProperty("slots")[0].GetProperty("slotId").GetGuid());
        Assert.Equal("range_too_long", tooLong.GetProperty("code").GetString());
        Assert.Contains("14", tooLong.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{"toDate":"2026-05-20"}""", "invalid_date")]
    [InlineData("""{"fromDate":"next tuesday","toDate":"2026-05-20"}""", "invalid_date")]
    [InlineData("""{"fromDate":"2026-05-20"}""", "invalid_date")]
    [InlineData("""{"fromDate":"2026-05-21","toDate":"2026-05-20"}""", "invalid_range")]
    [InlineData("""{"fromDate":"2026-05-20","toDate":"2026-05-21","partOfDay":"midnight"}""", "invalid_part_of_day")]
    [InlineData("""{"fromDate":"2026-05-20","toDate":"2026-05-21","offeringId":"nope"}""", "invalid_offering_id")]
    [InlineData("""{"fromDate":"2026-05-20","toDate":"2026-05-21","offeringId":"019e0000-0000-7000-8000-000000000000"}""", "offering_not_found")]
    public async Task InvalidArguments_ReturnReadableError(string arguments, string code)
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, arguments);

        Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task OfferingOfAnotherBusiness_IsNotFound()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CheckAvailability, Range(offeringId: world.Pepperoni.Id));

        Assert.Equal("offering_not_found", result.GetProperty("code").GetString());
    }
}
