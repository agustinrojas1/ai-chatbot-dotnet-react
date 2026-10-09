using System.Text.Json;

namespace Chatbot.Core.Domain;

/// <summary>One record per niche; the agent persona and prompt live in the database.</summary>
public class Business
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Niche Niche { get; set; }
    public string Name { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public string PersonaPrompt { get; set; } = "";
    public string Greeting { get; set; } = "";

    /// <summary>Stored as a jsonb array of strings.</summary>
    public List<string> QuickReplies { get; set; } = [];

    /// <summary>IANA time zone id, e.g. <c>America/Argentina/Buenos_Aires</c>.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>ISO 4217 code, e.g. <c>ARS</c>.</summary>
    public string Currency { get; set; } = "USD";
}

public class Offering
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BusinessId { get; set; }
    public OfferingKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }

    /// <summary><c>month</c>, <c>sale</c> or <c>item</c>.</summary>
    public string PriceUnit { get; set; } = "item";

    /// <summary>Free-form jsonb object (neighborhood, rooms, gluten-free, ...). Defaults to <c>{}</c>.</summary>
    public JsonElement Attributes { get; set; } = JsonDefaults.EmptyObject;

    public bool IsActive { get; set; } = true;
}

public class AvailabilitySlot
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BusinessId { get; set; }
    public Guid? OfferingId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int Capacity { get; set; } = 1;

    /// <summary>
    /// Reserved seats. Never mutate it from application code: use the atomic
    /// <c>SlotBooking.TryReserveAsync</c> in Infrastructure. The DB enforces 0 &lt;= Booked &lt;= Capacity.
    /// </summary>
    public int Booked { get; set; }
}

public class Booking
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BusinessId { get; set; }
    public Guid SessionId { get; set; }
    public Guid? SlotId { get; set; }
    public Guid? OfferingId { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";

    /// <summary>Optional free-text note from the customer for the business (max 500 characters).</summary>
    public string? Notes { get; set; }

    /// <summary>jsonb array of order lines (<c>[{offeringId, qty, options}]</c>); null for slot bookings.</summary>
    public JsonElement? Items { get; set; }

    public decimal? Total { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingConfirmation;

    /// <summary>Unique in the database: a retry never duplicates a booking.</summary>
    public string IdempotencyKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AvailabilitySlot? Slot { get; set; }
    public Offering? Offering { get; set; }
}

public class HandoffRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public string Reason { get; set; } = "";
    public string Summary { get; set; } = "";
    public HandoffStatus Status { get; set; } = HandoffStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ChatSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BusinessId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    /// <summary><c>en</c> or <c>es</c>; null until detected.</summary>
    public string? Language { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public string ClientIpHash { get; set; } = "";

    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AgentRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public Guid UserMessageId { get; set; }
    public string Model { get; set; } = "";
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Ok;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public decimal CostUsd { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TraceEvent> Events { get; set; } = [];
}

public class TraceEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RunId { get; set; }

    /// <summary>Position within the run; (RunId, Sequence) is unique.</summary>
    public int Sequence { get; set; }

    public TraceEventType Type { get; set; }
    public string Name { get; set; } = "";
    public JsonElement? Input { get; set; }
    public JsonElement? Output { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ModelPricing
{
    /// <summary>Model name; primary key.</summary>
    public string Model { get; set; } = "";
    public decimal InputPerMillionUsd { get; set; }
    public decimal OutputPerMillionUsd { get; set; }
}

public static class JsonDefaults
{
    /// <summary>A shared, immutable <c>{}</c>.</summary>
    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}
