using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chatbot.Tests.Data;

[Collection(PostgresCollection.Name)]
public class DatabaseSeederTests(PostgresFixture postgres)
{
    /// <summary>Friday 2026-10-09 12:00 UTC: early enough that no slot of the first day is already in the past.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private async Task<(string ConnectionString, FixedClock Clock)> SeededDatabaseAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var clock = new FixedClock(Now);
        await SeedAsync(connectionString, clock);
        return (connectionString, clock);
    }

    private static async Task SeedAsync(string connectionString, TimeProvider clock)
    {
        await using var db = PostgresFixture.CreateContext(connectionString);
        await new DatabaseSeeder(db, clock, NullLogger<DatabaseSeeder>.Instance).SeedAsync(CancellationToken.None);
    }

    private sealed record Counts(int Businesses, int Offerings, int Slots, int Pricings);

    private static async Task<Counts> CountAsync(ChatbotDbContext db) => new(
        await db.Businesses.CountAsync(),
        await db.Offerings.CountAsync(),
        await db.AvailabilitySlots.CountAsync(),
        await db.ModelPricings.CountAsync());

    [Fact]
    public async Task Seeding_Twice_YieldsIdenticalRows()
    {
        var (connectionString, clock) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var before = await CountAsync(db);
        var idsBefore = await db.Offerings.Select(o => o.Id).OrderBy(i => i).ToListAsync();

        await SeedAsync(connectionString, clock);
        await SeedAsync(connectionString, clock);

        Assert.Equal(before, await CountAsync(db));
        Assert.Equal(idsBefore, await db.Offerings.Select(o => o.Id).OrderBy(i => i).ToListAsync());
        Assert.Equal(3, before.Businesses);
    }

    [Fact]
    public async Task Seeding_DoesNotOverwriteExistingRows()
    {
        var (connectionString, clock) = await SeededDatabaseAsync();
        await using (var db = PostgresFixture.CreateContext(connectionString))
        {
            var clinic = await db.Businesses.SingleAsync(b => b.Niche == Niche.Clinic);
            clinic.Greeting = "edited";
            var cleaning = await db.Offerings.SingleAsync(o => o.Name == "Teeth Cleaning");
            cleaning.Price = 99;
            await db.SaveChangesAsync();
        }

        await SeedAsync(connectionString, clock);

        await using var verify = PostgresFixture.CreateContext(connectionString);
        Assert.Equal("edited", (await verify.Businesses.SingleAsync(b => b.Niche == Niche.Clinic)).Greeting);
        Assert.Equal(99, (await verify.Offerings.SingleAsync(o => o.Name == "Teeth Cleaning")).Price);
    }

    [Fact]
    public async Task Businesses_HaveV1PersonaContent()
    {
        var (connectionString, _) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var businesses = await db.Businesses.ToDictionaryAsync(b => b.Niche);

        Assert.Equal(3, businesses.Count);

        var sofia = businesses[Niche.RealEstate];
        Assert.Equal(("PrimeHaven Realty", "Sofia"), (sofia.Name, sofia.PersonaName));
        Assert.Equal(
            "Hi! I'm Sofia, your real estate AI assistant at PrimeHaven Realty. Ask me about available properties or tap a suggestion below.",
            sofia.Greeting);
        Assert.Equal(
            ["Show me rentals under $600/month", "Tell me about the 4-room house in Belgrano", "I want to schedule a viewing"],
            sofia.QuickReplies);

        var alex = businesses[Niche.Clinic];
        Assert.Equal(("Smile Dental Clinic", "Alex"), (alex.Name, alex.PersonaName));
        Assert.Equal(
            ["How much is teeth cleaning?", "Do you take insurance?", "Book an appointment for tomorrow"],
            alex.QuickReplies);

        var nico = businesses[Niche.Restaurant];
        Assert.Equal(("Luigi's Pizzeria", "Nico"), (nico.Name, nico.PersonaName));
        Assert.Equal(
            ["Send me the menu", "Do you have gluten-free pizza?", "I want to order a Pepperoni pizza"],
            nico.QuickReplies);

        foreach (var b in businesses.Values)
        {
            Assert.Equal("USD", b.Currency);
            Assert.NotNull(TimeZoneInfo.FindSystemTimeZoneById(b.Timezone));
            Assert.StartsWith($"You are {b.PersonaName}", b.PersonaPrompt);
            Assert.Contains("WHATSAPP FORMATTING RULES", b.PersonaPrompt);
            Assert.Contains("FEW-SHOT EXAMPLES", b.PersonaPrompt);
            // Catalog lives in Offering rows; v1 claims that the bot registers things belong to tools, not to the persona.
            Assert.DoesNotContain("GROUND TRUTH", b.PersonaPrompt);
            Assert.DoesNotContain("HONESTY RULES", b.PersonaPrompt);
            Assert.DoesNotContain("HANDOFF_TRIGGERED", b.PersonaPrompt);
            Assert.DoesNotContain("I've registered", b.PersonaPrompt);
            Assert.DoesNotContain("I've noted", b.PersonaPrompt);
        }
    }

    [Fact]
    public async Task Offerings_AreSplitByNicheAndKind_WithConsistentAttributes()
    {
        var (connectionString, _) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var businesses = await db.Businesses.ToListAsync();
        var allOfferings = await db.Offerings.ToListAsync();
        var byNiche = businesses.ToDictionary(b => b.Niche, b => allOfferings.Where(o => o.BusinessId == b.Id).ToList());

        foreach (var (niche, kind) in new[]
                 {
                     (Niche.RealEstate, OfferingKind.Property),
                     (Niche.Clinic, OfferingKind.Treatment),
                     (Niche.Restaurant, OfferingKind.MenuItem),
                 })
        {
            var offerings = byNiche[niche];
            Assert.True(offerings.Count >= 9, $"{niche} has {offerings.Count} offerings");
            Assert.All(offerings, o =>
            {
                Assert.Equal(kind, o.Kind);
                Assert.True(o.IsActive);
                Assert.True(o.Price > 0);
                Assert.False(string.IsNullOrWhiteSpace(o.Description));
            });
            Assert.Equal(offerings.Count, offerings.Select(o => o.Name).Distinct().Count());

            // Same attribute keys on every offering of a niche, so jsonb filters behave uniformly.
            var keySets = offerings
                .Select(o => string.Join(",", o.Attributes.EnumerateObject().Select(p => p.Name).Order()))
                .Distinct()
                .ToList();
            Assert.Single(keySets);
        }

        // Ported from v1, exact.
        var realEstate = byNiche[Niche.RealEstate];
        AssertOffering(realEstate, "2-room apartment in Palermo", 550, "month");
        AssertOffering(realEstate, "4-room house in Belgrano", 320000, "sale");
        AssertOffering(realEstate, "3-room townhouse (PH) in Almagro", 600, "month");
        var clinic = byNiche[Niche.Clinic];
        AssertOffering(clinic, "Teeth Cleaning", 80, "item");
        AssertOffering(clinic, "Teeth Whitening", 200, "item");
        AssertOffering(clinic, "Check-up & Consultation", 50, "item");
        var restaurant = byNiche[Niche.Restaurant];
        AssertOffering(restaurant, "Margherita", 12, "item");
        AssertOffering(restaurant, "Pepperoni", 15, "item");
        AssertOffering(restaurant, "Veggie", 14, "item");

        // Filters make sense.
        Assert.Contains(realEstate, o => o.Attributes.GetProperty("listing").GetString() == "rent");
        Assert.Contains(realEstate, o => o.Attributes.GetProperty("listing").GetString() == "sale");
        Assert.True(realEstate.Select(o => o.Attributes.GetProperty("neighborhood").GetString()).Distinct().Count() >= 6);
        Assert.All(clinic, o => Assert.True(o.Attributes.GetProperty("durationMinutes").GetInt32() > 0));
        Assert.Contains(restaurant, o => o.Attributes.GetProperty("glutenFree").GetBoolean());
        Assert.Contains(restaurant, o => o.Attributes.GetProperty("vegetarian").GetBoolean());
        Assert.Contains(restaurant, o => !o.Attributes.GetProperty("vegetarian").GetBoolean());
        Assert.Contains(restaurant, o => o.Attributes.GetProperty("category").GetString() == "drink");
        Assert.Contains(restaurant, o => o.Attributes.GetProperty("category").GetString() == "side");
    }

    private static void AssertOffering(List<Offering> offerings, string name, decimal price, string unit)
    {
        var offering = Assert.Single(offerings, o => o.Name == name);
        Assert.Equal(price, offering.Price);
        Assert.Equal(unit, offering.PriceUnit);
    }

    [Fact]
    public async Task Slots_CoverTheNext14Days_WithinLocalOpeningHours()
    {
        var (connectionString, _) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var businesses = await db.Businesses.ToListAsync();

        // Opening hours stated independently of the seeder: Mon-Sat 10-18 / Mon-Fri 9-18 + Sat 9-13 / daily 12-22.
        var expected = new Dictionary<Niche, (Func<DayOfWeek, (int Open, int Close)?> Hours, int Capacity, int Count)>
        {
            [Niche.RealEstate] = (d => d == DayOfWeek.Sunday ? null : (10, 18), 1, 12 * 16),
            [Niche.Clinic] = (d => d switch { DayOfWeek.Sunday => null, DayOfWeek.Saturday => (9, 13), _ => (9, 18) }, 1, 10 * 18 + 2 * 8),
            [Niche.Restaurant] = (_ => (12, 22), 4, 14 * 20),
        };

        foreach (var business in businesses)
        {
            var (hours, capacity, count) = expected[business.Niche];
            var zone = TimeZoneInfo.FindSystemTimeZoneById(business.Timezone);
            var slots = await db.AvailabilitySlots.Where(s => s.BusinessId == business.Id).ToListAsync();

            Assert.Equal(count, slots.Count);
            Assert.All(slots, s =>
            {
                Assert.Equal(capacity, s.Capacity);
                Assert.True(s.Booked <= s.Capacity);
                Assert.Equal(0, s.Booked);
                Assert.Null(s.OfferingId);
                Assert.Equal(TimeSpan.FromMinutes(30), s.EndsAt - s.StartsAt);
                Assert.True(s.StartsAt > Now && s.StartsAt < Now.AddDays(15));

                var start = TimeZoneInfo.ConvertTime(s.StartsAt, zone);
                var end = TimeZoneInfo.ConvertTime(s.EndsAt, zone);
                var open = hours(start.DayOfWeek);
                Assert.NotNull(open);
                Assert.True(start.TimeOfDay >= TimeSpan.FromHours(open.Value.Open), $"{business.Niche} {start}");
                Assert.True(end.TimeOfDay <= TimeSpan.FromHours(open.Value.Close), $"{business.Niche} {end}");
                Assert.Equal(0, start.Minute % 30);
            });

            // Every open local day in the window has slots.
            var daysWithSlots = slots
                .Select(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.StartsAt, zone).DateTime))
                .ToHashSet();
            for (var i = 0; i < 14; i++)
            {
                var day = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(i);
                Assert.Equal(hours(day.DayOfWeek) is not null, daysWithSlots.Contains(day));
            }

            Assert.Equal(slots.Count, slots.Select(s => s.StartsAt).Distinct().Count());
        }
    }

    [Fact]
    public async Task Seeding_OnALaterDay_TopsUpTheRollingWindowWithoutDuplicates()
    {
        var (connectionString, clock) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var before = await db.AvailabilitySlots.CountAsync();
        var lastBefore = await db.AvailabilitySlots.MaxAsync(s => s.StartsAt);

        // A booked slot must survive the top-up untouched.
        var taken = await db.AvailabilitySlots.OrderBy(s => s.StartsAt).FirstAsync();
        Assert.True(await SlotBooking.TryReserveAsync(db, taken.Id));

        clock.Current = Now.AddDays(3);
        await SeedAsync(connectionString, clock);

        Assert.True(await db.AvailabilitySlots.CountAsync() > before);
        Assert.True(await db.AvailabilitySlots.MaxAsync(s => s.StartsAt) > lastBefore);

        var duplicates = await db.AvailabilitySlots
            .GroupBy(s => new { s.BusinessId, s.OfferingId, s.StartsAt })
            .Where(g => g.Count() > 1)
            .CountAsync();
        Assert.Equal(0, duplicates);

        // The top-up equals what a fresh database seeded on the later day holds (past slots aside).
        var fresh = await postgres.CreateMigratedDatabaseAsync();
        await SeedAsync(fresh, clock);
        await using var freshDb = PostgresFixture.CreateContext(fresh);
        var wanted = (await freshDb.AvailabilitySlots.Select(s => s.StartsAt).ToListAsync()).ToHashSet();
        var have = (await db.AvailabilitySlots.Where(s => s.StartsAt > clock.Current).Select(s => s.StartsAt).ToListAsync()).ToHashSet();
        Assert.True(wanted.SetEquals(have));

        Assert.Equal(1, await db.AvailabilitySlots.Where(s => s.Id == taken.Id).Select(s => s.Booked).SingleAsync());
    }

    [Fact]
    public async Task ModelPricing_OnlyHoldsVerifiedRows()
    {
        var (connectionString, _) = await SeededDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);

        var row = Assert.Single(await db.ModelPricings.ToListAsync());
        Assert.Equal("gemini-3.5-flash-lite", row.Model);
        Assert.Equal(0.30m, row.InputPerMillionUsd);
        Assert.Equal(2.50m, row.OutputPerMillionUsd);
    }

    [Fact]
    public async Task Infrastructure_RegistersTheRealSeeder()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();

        await using var scope = services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>();
        Assert.IsType<DatabaseSeeder>(seeder);

        await seeder.SeedAsync(CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<ChatbotDbContext>();
        Assert.Equal(3, await db.Businesses.CountAsync());
        Assert.True(await db.AvailabilitySlots.AnyAsync(s => s.StartsAt > DateTimeOffset.UtcNow));
    }
}
