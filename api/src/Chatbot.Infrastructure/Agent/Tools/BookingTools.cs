using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary>One order line as the model sends it.</summary>
public sealed record OrderItemArgument(
    [property: Description("offeringId of a menu item, from search_catalog.")] string? OfferingId,
    [property: Description("Quantity, 1 to 20.")] int Qty = 1,
    [property: Description("Optional customisation: an object of strings like {\"crust\":\"gluten-free\"} or a free text string.")] JsonElement? Options = null);

/// <summary>
/// <c>create_booking</c>. The rules that must not fail live here, not in the prompt: scoping by business, input
/// validation, totals computed from the catalog, an idempotency key, and capacity decided by the database.
/// </summary>
internal sealed class BookingTools(ChatbotDbContext db, ToolContext context, TimeProvider clock)
{
    private const int MaxLines = 20;
    private const int MaxQuantity = 20;
    private const int MaxNameLength = 200;
    private const int MaxPhoneLength = 50; // bookings.CustomerPhone column length
    private const int MaxNotesLength = 500; // bookings.Notes column length

    private readonly TimeZoneInfo _zone = ToolSupport.ResolveTimezone(context.Timezone);

    public async Task<object> CreateBookingAsync(
        [Description("Appointment, visit or delivery window: a slotId returned by check_availability.")]
        string? slotId = null,
        [Description("Optional offeringId the booking is about (for example the property to visit).")]
        string? offeringId = null,
        [Description("For orders: the lines [{offeringId, qty, options}]. The total is computed by the server.")]
        List<OrderItemArgument>? items = null,
        [Description("Customer full name. Required.")]
        string? customerName = null,
        [Description("Customer phone number, digits with an optional + prefix. Required.")]
        string? customerPhone = null,
        [Description("Optional note for the business.")]
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Shape validation: nothing here touches the database.
        var name = customerName?.Trim() ?? "";
        if (name.Length == 0)
            return ToolSupport.Error("invalid_customer_name", "customerName is required. Ask the customer for their name.");
        if (name.Length > MaxNameLength)
            return ToolSupport.Error("invalid_customer_name", "customerName is too long.");

        var phone = customerPhone?.Trim() ?? "";
        if (!IsPlausiblePhone(phone))
            return ToolSupport.Error("invalid_customer_phone", "customerPhone is required and must be a valid phone number with at least 7 digits. Ask the customer for it.");

        Guid? slotGuid = null;
        if (!string.IsNullOrWhiteSpace(slotId))
        {
            if (!Guid.TryParse(slotId, out var parsed))
                return ToolSupport.Error("invalid_slot_id", "slotId must be an id returned by check_availability.");
            slotGuid = parsed;
        }

        Guid? offeringGuid = null;
        if (!string.IsNullOrWhiteSpace(offeringId))
        {
            if (!Guid.TryParse(offeringId, out var parsed))
                return ToolSupport.Error("invalid_offering_id", "offeringId must be an id returned by search_catalog.");
            offeringGuid = parsed;
        }

        var lines = new List<OrderLine>();
        if (items is { Count: > 0 })
        {
            if (items.Count > MaxLines)
                return ToolSupport.Error("invalid_items", $"An order can have at most {MaxLines} lines.");
            foreach (var item in items)
            {
                if (!Guid.TryParse(item?.OfferingId, out var lineOffering))
                    return ToolSupport.Error("invalid_items", "Every item needs an offeringId returned by search_catalog.");
                if (item!.Qty is < 1 or > MaxQuantity)
                    return ToolSupport.Error("invalid_items", $"Item quantity must be between 1 and {MaxQuantity}.");
                if (!TryNormalizeOptions(item.Options, out var options))
                    return ToolSupport.Error("invalid_items", "Item options must be an object of strings or a text.");
                lines.Add(new OrderLine(lineOffering, item.Qty, options));
            }
        }

        if (slotGuid is null && lines.Count == 0)
            return ToolSupport.Error("missing_target", "Provide a slotId (appointment, visit or delivery window) or items (an order). Use check_availability or search_catalog first.");

        // The conversation must be with the bot: once a person took over (or the session closed) no new booking starts.
        var sessionStatus = await db.ChatSessions.AsNoTracking()
            .Where(s => s.Id == context.SessionId && s.BusinessId == context.BusinessId)
            .Select(s => (SessionStatus?)s.Status)
            .FirstOrDefaultAsync(cancellationToken);
        switch (sessionStatus)
        {
            case null:
                return ToolSupport.Error("session_not_found", "Session not found.");
            case SessionStatus.AwaitingHuman:
                return ToolSupport.Error("session_awaiting_human", "A person from the business is taking over this conversation, so no new booking can be created here. Tell the customer that the person will continue.");
            case SessionStatus.Closed:
                return ToolSupport.Error("session_closed", "This session is closed and cannot create bookings.");
        }

        // 2. Retry of a booking that already exists: same key, same booking, no second seat.
        var key = IdempotencyKey(context.SessionId, slotGuid, lines);
        var existing = await FindByKeyAsync(key, cancellationToken);
        if (existing is not null)
            return await ViewAsync(existing, alreadyRegistered: true, cancellationToken);

        // 3. Validation against the database, always scoped to the session's business.
        Offering? offering = null;
        if (offeringGuid is { } offeringId1)
        {
            offering = await db.Offerings.AsNoTracking().FirstOrDefaultAsync(
                o => o.Id == offeringId1 && o.BusinessId == context.BusinessId && o.IsActive, cancellationToken);
            if (offering is null)
                return ToolSupport.Error("offering_not_found", "Offering not found.");
        }

        AvailabilitySlot? slot = null;
        if (slotGuid is { } slotGuid1)
        {
            slot = await db.AvailabilitySlots.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == slotGuid1 && s.BusinessId == context.BusinessId, cancellationToken);
            if (slot is null)
                return ToolSupport.Error("slot_not_found", "Slot not found. Call check_availability again to get valid slots.");
            if (slot.StartsAt <= clock.GetUtcNow())
                return ToolSupport.Error("slot_in_past", "That slot has already started. Offer another one from check_availability.");
            if (slot.Booked >= slot.Capacity)
                return ToolSupport.Error("slot_full", "That slot is full. Call check_availability again and offer another time.");
            if (slot.OfferingId is { } slotOffering && offeringGuid is { } wanted && slotOffering != wanted)
                return ToolSupport.Error("slot_offering_mismatch", "That slot is for a different offering. Call check_availability with this offeringId.");
        }

        var itemsJson = default(JsonElement?);
        decimal? total = null;
        if (lines.Count > 0)
        {
            var ids = lines.Select(l => l.OfferingId).Distinct().ToList();
            var catalog = await db.Offerings.AsNoTracking()
                .Where(o => o.BusinessId == context.BusinessId && o.IsActive && ids.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, cancellationToken);
            if (ids.Any(id => !catalog.ContainsKey(id)))
                return ToolSupport.Error("offering_not_found", "One or more items were not found in the catalog. Use search_catalog to get valid offeringIds.");

            total = lines.Sum(l => catalog[l.OfferingId].Price * l.Qty);
            itemsJson = JsonSerializer.SerializeToElement(lines.Select(l => new
            {
                offeringId = l.OfferingId,
                name = catalog[l.OfferingId].Name,
                qty = l.Qty,
                unitPrice = catalog[l.OfferingId].Price,
                options = l.Options,
            }));
        }

        // 4. Seat and booking in one transaction: if the insert fails, the seat goes back.
        var booking = new Booking
        {
            BusinessId = context.BusinessId,
            SessionId = context.SessionId,
            SlotId = slot?.Id,
            OfferingId = offering?.Id ?? slot?.OfferingId,
            CustomerName = name,
            CustomerPhone = phone,
            Notes = Truncate(notes),
            Items = itemsJson,
            Total = total,
            Status = BookingStatus.PendingConfirmation,
            IdempotencyKey = key,
            CreatedAt = clock.GetUtcNow(),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (slot is not null && !await SlotBooking.TryReserveAsync(db, slot.Id, cancellationToken))
                return ToolSupport.Error("slot_full", "That slot was just taken by someone else. Call check_availability again and offer another time.");

            db.Bookings.Add(booking);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same retry raced with itself: the other call won. Return its booking.
            await transaction.RollbackAsync(cancellationToken);
            db.Entry(booking).State = EntityState.Detached;
            var winner = await FindByKeyAsync(key, cancellationToken);
            if (winner is null)
                throw;
            return await ViewAsync(winner, alreadyRegistered: true, cancellationToken);
        }

        return await ViewAsync(booking, alreadyRegistered: false, cancellationToken);
    }

    private static string? Truncate(string? notes)
    {
        notes = notes?.Trim();
        if (string.IsNullOrEmpty(notes))
            return null;
        return notes.Length <= MaxNotesLength ? notes : notes[..MaxNotesLength];
    }

    private Task<Booking?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        db.Bookings.AsNoTracking().FirstOrDefaultAsync(
            b => b.IdempotencyKey == key && b.BusinessId == context.BusinessId, cancellationToken);

    /// <summary>What the model gets back: always "registered, pending confirmation", never "confirmed".</summary>
    private async Task<object> ViewAsync(Booking booking, bool alreadyRegistered, CancellationToken cancellationToken)
    {
        object? slotView = null;
        if (booking.SlotId is { } slotId)
        {
            var slot = await db.AvailabilitySlots.AsNoTracking().FirstAsync(s => s.Id == slotId, cancellationToken);
            slotView = ToolSupport.SlotView(slot, _zone);
        }

        return new
        {
            bookingId = booking.Id,
            status = booking.Status.ToString(),
            alreadyRegistered,
            message = "The request is registered and PENDING confirmation by the business. It is NOT confirmed yet: tell the customer the business will confirm it.",
            customerName = booking.CustomerName,
            slot = slotView,
            items = booking.Items,
            total = booking.Total,
            currency = booking.Total is null ? null : await db.Businesses.AsNoTracking()
                .Where(b => b.Id == context.BusinessId).Select(b => b.Currency).FirstAsync(cancellationToken),
        };
    }

    /// <summary>Digits with the usual separators, 7 to 15 digits (E.164 maximum), fits the column.</summary>
    private static bool IsPlausiblePhone(string phone)
    {
        if (phone.Length is 0 or > MaxPhoneLength)
            return false;
        var digits = 0;
        for (var i = 0; i < phone.Length; i++)
        {
            var c = phone[i];
            if (char.IsAsciiDigit(c))
                digits++;
            else if (!(c == '+' && i == 0) && c is not (' ' or '-' or '(' or ')' or '.'))
                return false;
        }

        return digits is >= 7 and <= 15;
    }

    /// <summary>Options are a string map (sorted, so equal orders hash equal), a free string, or null.</summary>
    private static bool TryNormalizeOptions(JsonElement? options, out object? normalized)
    {
        normalized = null;
        switch (options?.ValueKind)
        {
            case null or JsonValueKind.Null or JsonValueKind.Undefined:
                return true;
            case JsonValueKind.String:
                var text = options.Value.GetString()?.Trim();
                normalized = string.IsNullOrEmpty(text) ? null : text;
                return true;
            case JsonValueKind.Object:
                var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in options.Value.EnumerateObject())
                {
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                        return false;
                    map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!
                        : property.Value.GetRawText();
                }

                normalized = map.Count == 0 ? null : map;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Deterministic: the same session asking for the same slot, or the same set of order lines, always
    /// produces the same key, whatever the order of the lines.
    /// </summary>
    private static string IdempotencyKey(Guid sessionId, Guid? slotId, List<OrderLine> lines)
    {
        var target = new StringBuilder();
        if (slotId is { } slot)
            target.Append("slot:").Append(slot.ToString("N"));
        if (lines.Count > 0)
        {
            var canonical = lines
                .Select(l => $"{l.OfferingId:N}x{l.Qty}:{JsonSerializer.Serialize(l.Options)}")
                .Order(StringComparer.Ordinal);
            target.Append("|items:").AppendJoin(';', canonical);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:N}|{target}"));
        return "bk_" + Convert.ToHexStringLower(hash);
    }

    private sealed record OrderLine(Guid OfferingId, int Qty, object? Options);
}
