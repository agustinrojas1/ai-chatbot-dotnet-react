using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Infrastructure.Persistence;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Chatbot.Tests.Tools;

/// <summary>A clock that always returns the same instant, so slot times are deterministic.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Two businesses in a fresh migrated database: A (real estate, Buenos Aires, UTC-3) and B (restaurant, Chicago),
/// plus helpers to open sessions and call the tools the way the model does (JSON arguments).
/// </summary>
internal sealed class ToolWorld
{
    /// <summary>2026-05-18 12:00 in Buenos Aires.</summary>
    public static readonly DateTimeOffset Now = new(2026, 5, 18, 15, 0, 0, TimeSpan.Zero);

    public static readonly TimeProvider Clock = new FixedClock(Now);

    private ToolWorld(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }
    public Business A { get; private set; } = null!;
    public Business B { get; private set; } = null!;

    public Offering Palermo2 { get; private set; } = null!;
    public Offering Palermo3 { get; private set; } = null!;
    public Offering Belgrano4 { get; private set; } = null!;
    public Offering Retired { get; private set; } = null!;
    public Offering Pepperoni { get; private set; } = null!;
    public Offering GlutenFreeSalad { get; private set; } = null!;
    public Offering Margherita { get; private set; } = null!;

    public static async Task<ToolWorld> CreateAsync(PostgresFixture postgres)
    {
        // Every test gets its own database, and Npgsql keeps one pool per connection string: without this the idle
        // connections of finished tests pile up until PostgreSQL refuses new clients.
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();
        var world = new ToolWorld(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await using var db = world.NewDb();

        world.A = new Business { Niche = Niche.RealEstate, Name = "PrimeHaven", Timezone = "America/Argentina/Buenos_Aires", Currency = "USD" };
        world.B = new Business { Niche = Niche.Restaurant, Name = "Luigi's", Timezone = "America/Chicago", Currency = "USD" };

        world.Palermo2 = Offer(world.A, OfferingKind.Property, "2-room apartment in Palermo", 550, "month",
            new { neighborhood = "Palermo", rooms = 2, features = new[] { "balcony", "low HOA fees" } });
        world.Palermo3 = Offer(world.A, OfferingKind.Property, "3-room apartment in Palermo", 700, "month",
            new { neighborhood = "Palermo", rooms = 3, features = new[] { "pool" } });
        world.Belgrano4 = Offer(world.A, OfferingKind.Property, "4-room house in Belgrano", 320000, "sale",
            new { neighborhood = "Belgrano", rooms = 4, features = new[] { "private garden" } });
        world.Retired = Offer(world.A, OfferingKind.Property, "Retired flat in Palermo", 400, "month",
            new { neighborhood = "Palermo", rooms = 2, features = Array.Empty<string>() });
        world.Retired.IsActive = false;

        world.Pepperoni = Offer(world.B, OfferingKind.MenuItem, "Pepperoni Pizza", 15.50m, "item", new { category = "pizza", glutenFree = false });
        world.GlutenFreeSalad = Offer(world.B, OfferingKind.MenuItem, "Garden Salad", 9, "item", new { category = "salad", glutenFree = true });
        world.Margherita = Offer(world.B, OfferingKind.MenuItem, "Margherita Pizza", 12, "item", new { category = "pizza", glutenFree = false });

        db.AddRange(world.A, world.B);
        db.AddRange(world.Palermo2, world.Palermo3, world.Belgrano4, world.Retired, world.Pepperoni, world.GlutenFreeSalad, world.Margherita);
        await db.SaveChangesAsync();
        return world;
    }

    private static Offering Offer(Business business, OfferingKind kind, string name, decimal price, string unit, object attributes) => new()
    {
        BusinessId = business.Id,
        Kind = kind,
        Name = name,
        Description = $"{name}. Great choice.",
        Price = price,
        PriceUnit = unit,
        Attributes = JsonSerializer.SerializeToElement(attributes),
    };

    public ChatbotDbContext NewDb() => PostgresFixture.CreateContext(ConnectionString);

    /// <summary>Adds a slot starting <paramref name="startsAt"/> (UTC).</summary>
    public async Task<AvailabilitySlot> AddSlotAsync(Business business, DateTimeOffset startsAt, int capacity = 1, int booked = 0, Offering? offering = null)
    {
        await using var db = NewDb();
        var slot = new AvailabilitySlot
        {
            BusinessId = business.Id,
            OfferingId = offering?.Id,
            StartsAt = startsAt.ToUniversalTime(),
            EndsAt = startsAt.ToUniversalTime().AddMinutes(30),
            Capacity = capacity,
            Booked = booked,
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    /// <summary>What the test customers "say" unless a test gives its own messages: every name and phone the booking tests use.</summary>
    public const string DefaultCustomerMessage =
        "Hi! I'm Ana Perez, +54 11 5555-4444. My friend Beto: +54 11 6666-7777. Nico Client, (312) 555-0101 or 312-555-0101.";

    /// <summary>Opens a session whose customer already wrote <paramref name="userMessages"/> (default: <see cref="DefaultCustomerMessage"/>).</summary>
    public async Task<Session> NewSessionAsync(Business business, params string[] userMessages)
    {
        var db = NewDb();
        var chat = new ChatSession { BusinessId = business.Id, ClientIpHash = "test" };
        db.ChatSessions.Add(chat);
        await db.SaveChangesAsync();
        var context = new ToolContext(business.Id, chat.Id, business.Timezone);
        var session = new Session(db, context, AgentToolFactory.Create(db, context, Clock));
        foreach (var text in userMessages.Length == 0 ? [DefaultCustomerMessage] : userMessages)
            await session.SayAsync(text);
        return session;
    }

    public async Task<int> BookedAsync(Guid slotId)
    {
        await using var db = NewDb();
        return await db.AvailabilitySlots.Where(s => s.Id == slotId).Select(s => s.Booked).SingleAsync();
    }

    public async Task<int> BookingCountAsync()
    {
        await using var db = NewDb();
        return await db.Bookings.CountAsync();
    }
}

/// <summary>One conversation: its own DbContext, tool context and tools (what a request would have).</summary>
internal sealed class Session(ChatbotDbContext db, ToolContext context, IList<AITool> tools) : IAsyncDisposable
{
    public ChatbotDbContext Db => db;
    public ToolContext Context => context;
    public IList<AITool> Tools => tools;

    /// <summary>The customer writes a message (persisted before tools run, like the real agent does).</summary>
    public async Task SayAsync(string text, MessageRole role = MessageRole.User)
    {
        db.ChatMessages.Add(new Chatbot.Core.Domain.ChatMessage { SessionId = context.SessionId, Role = role, Content = text, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Invokes a tool with JSON arguments, exactly as a model's tool call would.</summary>
    public async Task<JsonElement> CallAsync(string tool, string argumentsJson = "{}")
    {
        var function = tools.OfType<AIFunction>().Single(t => t.Name == tool);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!;
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments));
        return Assert.IsType<JsonElement>(result);
    }

    public ValueTask DisposeAsync() => db.DisposeAsync();
}
