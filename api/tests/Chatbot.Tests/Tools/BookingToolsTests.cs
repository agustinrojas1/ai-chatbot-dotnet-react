using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Data;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chatbot.Tests.Tools;

[Collection(PostgresCollection.Name)]
public class BookingToolsTests(PostgresFixture postgres)
{
    private static DateTimeOffset Local(int day, int hour) => new(2026, 5, day, hour, 0, 0, TimeSpan.FromHours(-3));

    private static string SlotBooking(Guid slotId, string name = "Ana Perez", string phone = "+54 11 5555-4444") =>
        JsonSerializer.Serialize(new { slotId, customerName = name, customerPhone = phone });

    private static string Code(JsonElement result) => result.GetProperty("code").GetString()!;

    [Fact]
    public async Task SlotBooking_IsPendingConfirmation_StoresFullPhoneAndTakesOneSeat()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10));
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("PendingConfirmation", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("alreadyRegistered").GetBoolean());
        var message = result.GetProperty("message").GetString()!;
        Assert.Contains("registered", message);
        Assert.Contains("PENDING", message);
        Assert.Equal("Tue, May 19 at 10:00 AM", result.GetProperty("slot").GetProperty("label").GetString());

        await using var db = world.NewDb();
        var booking = await db.Bookings.SingleAsync();
        Assert.Equal(result.GetProperty("bookingId").GetGuid(), booking.Id);
        Assert.Equal(BookingStatus.PendingConfirmation, booking.Status);
        Assert.Equal("+54 11 5555-4444", booking.CustomerPhone);
        Assert.Equal("Ana Perez", booking.CustomerName);
        Assert.Equal(world.A.Id, booking.BusinessId);
        Assert.Equal(session.Context.SessionId, booking.SessionId);
        Assert.Equal(slot.Id, booking.SlotId);
        Assert.Null(booking.Total);
        Assert.Equal(1, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task Retry_ReturnsTheSameBookingAndConsumesCapacityOnce()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 3);
        await using var session = await world.NewSessionAsync(world.A);

        var first = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));
        var retry = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));
        // The model may retry with a slightly different wording of the same request.
        var reworded = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id, name: "  Ana  Perez ", phone: "541155554444"));

        Assert.False(first.GetProperty("alreadyRegistered").GetBoolean());
        Assert.True(retry.GetProperty("alreadyRegistered").GetBoolean());
        Assert.True(reworded.GetProperty("alreadyRegistered").GetBoolean());
        Assert.Equal(first.GetProperty("bookingId").GetGuid(), retry.GetProperty("bookingId").GetGuid());
        Assert.Equal(first.GetProperty("bookingId").GetGuid(), reworded.GetProperty("bookingId").GetGuid());
        Assert.Equal(1, await world.BookedAsync(slot.Id));
        Assert.Equal(1, await world.BookingCountAsync());
    }

    [Fact]
    public async Task RetryOfFullSlotBooking_StillReturnsTheExistingBooking()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 1);
        await using var session = await world.NewSessionAsync(world.A);

        var first = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));
        var retry = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id)); // slot is now full

        Assert.True(retry.GetProperty("alreadyRegistered").GetBoolean());
        Assert.Equal(first.GetProperty("bookingId").GetGuid(), retry.GetProperty("bookingId").GetGuid());
    }

    [Fact]
    public async Task DifferentSessions_BookTheSameSlotSeparately()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 2);
        await using var one = await world.NewSessionAsync(world.A);
        await using var two = await world.NewSessionAsync(world.A);

        var a = await one.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));
        var b = await two.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.NotEqual(a.GetProperty("bookingId").GetGuid(), b.GetProperty("bookingId").GetGuid());
        Assert.Equal(2, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task FullSlot_ReturnsReadableErrorAndCreatesNothing()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 1, booked: 1);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("slot_full", Code(result));
        Assert.Contains("full", result.GetProperty("error").GetString());
        Assert.Equal(0, await world.BookingCountAsync());
        Assert.Equal(1, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task TwoSessionsRacingForTheLastSeat_ExactlyOneBookingWins()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 1);
        await using var one = await world.NewSessionAsync(world.A);
        await using var two = await world.NewSessionAsync(world.A);

        var results = await Task.WhenAll(
            Task.Run(() => one.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id, "Ana", "+54 11 5555-4444"))),
            Task.Run(() => two.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id, "Beto", "+54 11 6666-7777"))));

        Assert.Single(results, r => r.TryGetProperty("bookingId", out _));
        Assert.Single(results, r => r.TryGetProperty("code", out var c) && c.GetString() == "slot_full");
        Assert.Equal(1, await world.BookingCountAsync());
        Assert.Equal(1, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task SeatGoesBackWhenTheInsertFails()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 1);
        await using var real = await world.NewSessionAsync(world.A);
        // Same session, but its context fails when saving: the seat was already taken inside the transaction.
        await using var db = new ChatbotDbContext(new DbContextOptionsBuilder<ChatbotDbContext>()
            .UseNpgsql(world.ConnectionString).AddInterceptors(new FailingSave()).Options);
        var failing = new Session(db, real.Context, AgentToolFactory.Create(db, real.Context, ToolWorld.Clock));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id)));

        Assert.Equal(0, await world.BookedAsync(slot.Id));
        Assert.Equal(0, await world.BookingCountAsync());
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("insert failed");
    }

    [Fact]
    public async Task Notes_AreStoredTrimmedAndTruncatedTo500Characters()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var first = await world.AddSlotAsync(world.A, Local(19, 10));
        var second = await world.AddSlotAsync(world.A, Local(19, 11));
        var third = await world.AddSlotAsync(world.A, Local(19, 12));
        await using var session = await world.NewSessionAsync(world.A);
        string With(Guid slotId, string? notes) => JsonSerializer.Serialize(
            new { slotId, customerName = "Ana", customerPhone = "+54 11 5555-4444", notes });

        await session.CallAsync(AgentToolFactory.CreateBooking, With(first.Id, "  Ground floor please  "));
        await session.CallAsync(AgentToolFactory.CreateBooking, With(second.Id, new string('x', 600)));
        await session.CallAsync(AgentToolFactory.CreateBooking, With(third.Id, "   "));

        await using var db = world.NewDb();
        var notes = await db.Bookings.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).ToDictionaryAsync(b => b.SlotId!.Value, b => b.Notes);
        Assert.Equal("Ground floor please", notes[first.Id]);
        Assert.Equal(new string('x', 500), notes[second.Id]);
        Assert.Null(notes[third.Id]);
    }

    [Fact]
    public async Task AwaitingHumanSession_CannotCreateBookings()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), capacity: 2);
        await using var session = await world.NewSessionAsync(world.A);
        await session.CallAsync(AgentToolFactory.HandoffToHuman, """{"reason":"wants a person","summary":"asked for a person"}""");

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("session_awaiting_human", Code(result));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
        Assert.Equal(0, await world.BookingCountAsync());
        Assert.Equal(0, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task ClosedSession_CannotCreateBookings()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10));
        await using var session = await world.NewSessionAsync(world.A);
        await using (var db = world.NewDb())
        {
            await db.ChatSessions.Where(s => s.Id == session.Context.SessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Closed));
        }

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("session_closed", Code(result));
        Assert.Equal(0, await world.BookingCountAsync());
    }

    [Fact]
    public async Task UnknownSession_IsRejected()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10));
        await using var db = world.NewDb();
        var context = new ToolContext(world.A.Id, Guid.CreateVersion7(), world.A.Timezone);
        var orphan = new Session(db, context, AgentToolFactory.Create(db, context, ToolWorld.Clock));

        var result = await orphan.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("session_not_found", Code(result));
        Assert.Equal(0, await world.BookedAsync(slot.Id));
    }

    [Fact]
    public async Task Order_TotalIsComputedByTheServerFromCatalogPrices()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.B);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, $$"""
            {
              "items": [
                {"offeringId":"{{world.Pepperoni.Id}}","qty":2,"options":{"crust":"gluten-free"} },
                {"offeringId":"{{world.GlutenFreeSalad.Id}}","qty":1,"options":"no onions"}
              ],
              "customerName":"Nico Client","customerPhone":"(312) 555-0101","total":1
            }
            """);

        Assert.Equal(2 * 15.50m + 9m, result.GetProperty("total").GetDecimal());
        Assert.Equal("USD", result.GetProperty("currency").GetString());
        Assert.Equal("PendingConfirmation", result.GetProperty("status").GetString());

        await using var db = world.NewDb();
        var booking = await db.Bookings.SingleAsync();
        Assert.Equal(40m, booking.Total);
        Assert.Null(booking.SlotId);
        var lines = booking.Items!.Value.EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(world.Pepperoni.Id, lines[0].GetProperty("offeringId").GetGuid());
        Assert.Equal(15.50m, lines[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal("gluten-free", lines[0].GetProperty("options").GetProperty("crust").GetString());
        Assert.Equal("no onions", lines[1].GetProperty("options").GetString());
    }

    [Fact]
    public async Task OrderWithDeliveryWindow_TakesTheSeatAndComputesTotal()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var window = await world.AddSlotAsync(world.B, ToolWorld.Now.AddHours(2), capacity: 4);
        await using var session = await world.NewSessionAsync(world.B);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, $$"""
            {"slotId":"{{window.Id}}","items":[{"offeringId":"{{world.Margherita.Id}}","qty":3}],"customerName":"Nico Client","customerPhone":"312-555-0101"}
            """);

        Assert.Equal(36m, result.GetProperty("total").GetDecimal());
        Assert.Equal(1, await world.BookedAsync(window.Id));
    }

    [Fact]
    public async Task OrderRetry_SameLinesInAnyOrder_ReturnsTheSameBooking()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.B);
        string Order(params Offering[] lines) => JsonSerializer.Serialize(new
        {
            items = lines.Select(o => new { offeringId = o.Id, qty = 1 }),
            customerName = "Nico Client",
            customerPhone = "312-555-0101",
        });

        var first = await session.CallAsync(AgentToolFactory.CreateBooking, Order(world.Pepperoni, world.Margherita));
        var retry = await session.CallAsync(AgentToolFactory.CreateBooking, Order(world.Margherita, world.Pepperoni));
        var different = await session.CallAsync(AgentToolFactory.CreateBooking, Order(world.Pepperoni));

        Assert.True(retry.GetProperty("alreadyRegistered").GetBoolean());
        Assert.Equal(first.GetProperty("bookingId").GetGuid(), retry.GetProperty("bookingId").GetGuid());
        Assert.False(different.GetProperty("alreadyRegistered").GetBoolean());
        Assert.Equal(2, await world.BookingCountAsync());
    }

    [Fact]
    public async Task SlotAndOfferingOfAnotherBusiness_BehaveAsNotFound()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var foreignSlot = await world.AddSlotAsync(world.B, ToolWorld.Now.AddHours(5), capacity: 4);
        var ownSlot = await world.AddSlotAsync(world.A, Local(19, 10));
        await using var tenantA = await world.NewSessionAsync(world.A);

        var bySlot = await tenantA.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(foreignSlot.Id));
        var byOffering = await tenantA.CallAsync(AgentToolFactory.CreateBooking, JsonSerializer.Serialize(
            new { slotId = ownSlot.Id, offeringId = world.Pepperoni.Id, customerName = "Ana", customerPhone = "+54 11 5555-4444" }));
        var byItems = await tenantA.CallAsync(AgentToolFactory.CreateBooking, JsonSerializer.Serialize(
            new { items = new[] { new { offeringId = world.Pepperoni.Id, qty = 1 } }, customerName = "Ana", customerPhone = "+54 11 5555-4444" }));

        Assert.Equal("slot_not_found", Code(bySlot));
        Assert.Equal("offering_not_found", Code(byOffering));
        Assert.Equal("offering_not_found", Code(byItems));
        Assert.Equal(0, await world.BookingCountAsync());
        Assert.Equal(0, await world.BookedAsync(foreignSlot.Id));
        Assert.Equal(0, await world.BookedAsync(ownSlot.Id));
    }

    [Fact]
    public async Task InactiveOffering_CannotBeOrdered()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, JsonSerializer.Serialize(
            new { items = new[] { new { offeringId = world.Retired.Id, qty = 1 } }, customerName = "Ana", customerPhone = "+54 11 5555-4444" }));

        Assert.Equal("offering_not_found", Code(result));
    }

    [Fact]
    public async Task PastSlot_IsRejected()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var past = await world.AddSlotAsync(world.A, ToolWorld.Now.AddMinutes(-1));
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(past.Id));

        Assert.Equal("slot_in_past", Code(result));
        Assert.Equal(0, await world.BookedAsync(past.Id));
    }

    [Fact]
    public async Task SlotForAnotherOffering_IsRejected()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        var slot = await world.AddSlotAsync(world.A, Local(19, 10), offering: world.Palermo2);
        await using var session = await world.NewSessionAsync(world.A);

        var mismatch = await session.CallAsync(AgentToolFactory.CreateBooking, JsonSerializer.Serialize(
            new { slotId = slot.Id, offeringId = world.Belgrano4.Id, customerName = "Ana", customerPhone = "+54 11 5555-4444" }));
        var inherited = await session.CallAsync(AgentToolFactory.CreateBooking, SlotBooking(slot.Id));

        Assert.Equal("slot_offering_mismatch", Code(mismatch));
        await using var db = world.NewDb();
        Assert.Equal(world.Palermo2.Id, (await db.Bookings.SingleAsync()).OfferingId); // the booking inherits the slot's offering
        Assert.Equal("PendingConfirmation", inherited.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"customerName":"","customerPhone":"+54 11 5555-4444"}""", "invalid_customer_name")]
    [InlineData("""{"customerName":"   ","customerPhone":"+54 11 5555-4444"}""", "invalid_customer_name")]
    [InlineData("""{"customerPhone":"+54 11 5555-4444"}""", "invalid_customer_name")]
    [InlineData("""{"customerName":"Ana","customerPhone":"12345"}""", "invalid_customer_phone")]
    [InlineData("""{"customerName":"Ana","customerPhone":"call me maybe"}""", "invalid_customer_phone")]
    [InlineData("""{"customerName":"Ana","customerPhone":"1234567890123456"}""", "invalid_customer_phone")]
    [InlineData("""{"customerName":"Ana"}""", "invalid_customer_phone")]
    [InlineData("""{"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "missing_target")]
    [InlineData("""{"slotId":"nope","customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_slot_id")]
    [InlineData("""{"slotId":"019e0000-0000-7000-8000-000000000000","customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "slot_not_found")]
    [InlineData("""{"offeringId":"nope","slotId":"019e0000-0000-7000-8000-000000000000","customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_offering_id")]
    [InlineData("""{"items":[{"offeringId":"nope","qty":1}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_items")]
    [InlineData("""{"items":[{"qty":1}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_items")]
    [InlineData("""{"items":[{"offeringId":"019e0000-0000-7000-8000-000000000000","qty":0}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_items")]
    [InlineData("""{"items":[{"offeringId":"019e0000-0000-7000-8000-000000000000","qty":21}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_items")]
    [InlineData("""{"items":[{"offeringId":"019e0000-0000-7000-8000-000000000000","qty":1,"options":[1,2]}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "invalid_items")]
    [InlineData("""{"items":[{"offeringId":"019e0000-0000-7000-8000-000000000000","qty":1}],"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", "offering_not_found")]
    public async Task InvalidArguments_ReturnReadableErrorAndCreateNothing(string arguments, string code)
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.CreateBooking, arguments);

        Assert.Equal(code, Code(result));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
        Assert.Equal(0, await world.BookingCountAsync());
    }
}
