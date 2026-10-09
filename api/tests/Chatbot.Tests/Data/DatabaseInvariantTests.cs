using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chatbot.Tests.Data;

[Collection(PostgresCollection.Name)]
public class DatabaseInvariantTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_ApplyOnCleanDatabase()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        // The model and the migrations agree: every table is queryable.
        Assert.Empty(await db.Businesses.ToListAsync());
        Assert.Empty(await db.Offerings.ToListAsync());
        Assert.Empty(await db.AvailabilitySlots.ToListAsync());
        Assert.Empty(await db.Bookings.ToListAsync());
        Assert.Empty(await db.HandoffRequests.ToListAsync());
        Assert.Empty(await db.ChatSessions.ToListAsync());
        Assert.Empty(await db.ChatMessages.ToListAsync());
        Assert.Empty(await db.AgentRuns.ToListAsync());
        Assert.Empty(await db.TraceEvents.ToListAsync());
        Assert.Empty(await db.ModelPricings.ToListAsync());
    }

    [Fact]
    public async Task Booking_DuplicateIdempotencyKey_ViolatesUniqueIndex()
    {
        await using var db = PostgresFixture.CreateContext(await postgres.CreateMigratedDatabaseAsync());
        var (business, session) = await AddBusinessAndSessionAsync(db);

        db.Bookings.Add(NewBooking(business, session, "key-1"));
        await db.SaveChangesAsync();

        db.Bookings.Add(NewBooking(business, session, "key-1"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Contains("IdempotencyKey", pg.ConstraintName);
    }

    [Fact]
    public async Task Slot_BookedAboveCapacity_IsRejectedByCheckConstraint()
    {
        await using var db = PostgresFixture.CreateContext(await postgres.CreateMigratedDatabaseAsync());
        var business = new Business { Niche = Niche.Clinic, Name = "Clinic" };
        var slot = NewSlot(business, capacity: 2);
        db.AddRange(business, slot);
        await db.SaveChangesAsync();

        // Via UPDATE of a tracked entity...
        slot.Booked = 3;
        var update = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(update, "ck_availability_slots_booked_lte_capacity");
        db.ChangeTracker.Clear();

        // ...via INSERT...
        db.AvailabilitySlots.Add(NewSlot(business, capacity: 1, booked: 2));
        var insert = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(insert, "ck_availability_slots_booked_lte_capacity");
        db.ChangeTracker.Clear();

        // ...and a negative counter.
        db.AvailabilitySlots.Add(NewSlot(business, capacity: 1, booked: -1));
        var negative = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(negative, "ck_availability_slots_booked_non_negative");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TryReserve_ConcurrentCallers_ExactlyCapacitySucceed(int capacity)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        Guid slotId;
        await using (var setup = PostgresFixture.CreateContext(connectionString))
        {
            var business = new Business { Niche = Niche.Clinic, Name = "Clinic" };
            var slot = NewSlot(business, capacity);
            setup.AddRange(business, slot);
            await setup.SaveChangesAsync();
            slotId = slot.Id;
        }

        const int callers = 10;
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            await using var db = PostgresFixture.CreateContext(connectionString);
            gate.Wait();
            return await SlotBooking.TryReserveAsync(db, slotId);
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(capacity, results.Count(r => r));
        await using var check = PostgresFixture.CreateContext(connectionString);
        Assert.Equal(capacity, (await check.AvailabilitySlots.SingleAsync(s => s.Id == slotId)).Booked);
    }

    [Fact]
    public async Task TryReserve_UnknownSlot_ReturnsFalse()
    {
        await using var db = PostgresFixture.CreateContext(await postgres.CreateMigratedDatabaseAsync());

        Assert.False(await SlotBooking.TryReserveAsync(db, Guid.NewGuid()));
    }

    [Fact]
    public async Task TraceEvent_DuplicateSequenceInRun_ViolatesUniqueIndex()
    {
        await using var db = PostgresFixture.CreateContext(await postgres.CreateMigratedDatabaseAsync());
        var run = await AddRunAsync(db);

        db.TraceEvents.Add(new TraceEvent { RunId = run.Id, Sequence = 1, Type = TraceEventType.LlmCall, Name = "llm" });
        await db.SaveChangesAsync();

        db.TraceEvents.Add(new TraceEvent { RunId = run.Id, Sequence = 1, Type = TraceEventType.ToolCall, Name = "tool" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task TraceEvent_TypeOutsideClosedSet_IsRejected()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var run = await AddRunAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO trace_events (\"Id\", \"RunId\", \"Sequence\", \"Type\", \"Name\", \"DurationMs\", \"CreatedAt\") " +
            "VALUES (gen_random_uuid(), {0}, 1, 'Custom', 'x', 0, now())", run.Id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_trace_events_type_closed_set", ex.ConstraintName);
    }

    [Fact]
    public async Task Jsonb_And_StringEnums_RoundTrip()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var business = new Business { Niche = Niche.RealEstate, Name = "Realty", QuickReplies = ["Hi", "Hola"] };
        var offering = new Offering
        {
            BusinessId = business.Id,
            Kind = OfferingKind.Property,
            Name = "Loft",
            Attributes = JsonSerializer.SerializeToElement(new { neighborhood = "Palermo", rooms = 2 }),
        };
        await using (var db = PostgresFixture.CreateContext(connectionString))
        {
            db.AddRange(business, offering);
            await db.SaveChangesAsync();
        }

        await using var read = PostgresFixture.CreateContext(connectionString);
        var loaded = await read.Offerings.SingleAsync();
        Assert.Equal("Palermo", loaded.Attributes.GetProperty("neighborhood").GetString());
        Assert.Equal(["Hi", "Hola"], (await read.Businesses.SingleAsync()).QuickReplies);

        // jsonb containment (what search_catalog filters will use) and string-stored enums.
        Assert.Equal(1, await read.Offerings.CountAsync(o =>
            EF.Functions.JsonContains(o.Attributes, "{\"rooms\": 2}")));
        Assert.Equal("Property", await read.Database
            .SqlQueryRaw<string>("SELECT \"Kind\" AS \"Value\" FROM offerings").SingleAsync());
    }

    private static void AssertCheckViolation(DbUpdateException ex, string constraint)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
        Assert.Equal(constraint, pg.ConstraintName);
    }

    private static AvailabilitySlot NewSlot(Business business, int capacity, int booked = 0) => new()
    {
        BusinessId = business.Id,
        StartsAt = DateTimeOffset.UtcNow.AddDays(1),
        EndsAt = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
        Capacity = capacity,
        Booked = booked,
    };

    private static Booking NewBooking(Business business, ChatSession session, string key) => new()
    {
        BusinessId = business.Id,
        SessionId = session.Id,
        CustomerName = "Ana",
        CustomerPhone = "+54 11 5555-4444",
        IdempotencyKey = key,
    };

    private static async Task<(Business, ChatSession)> AddBusinessAndSessionAsync(ChatbotDbContext db)
    {
        var business = new Business { Niche = Niche.Restaurant, Name = "Resto" };
        var session = new ChatSession { BusinessId = business.Id, ClientIpHash = "hash" };
        db.AddRange(business, session);
        await db.SaveChangesAsync();
        return (business, session);
    }

    private static async Task<AgentRun> AddRunAsync(ChatbotDbContext db)
    {
        var (_, session) = await AddBusinessAndSessionAsync(db);
        var message = new ChatMessage { SessionId = session.Id, Role = MessageRole.User, Content = "hi" };
        var run = new AgentRun { SessionId = session.Id, UserMessageId = message.Id, Model = "m" };
        db.AddRange(message, run);
        await db.SaveChangesAsync();
        return run;
    }
}
