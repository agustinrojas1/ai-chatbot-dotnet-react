using System.ComponentModel;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary><c>check_availability</c>: free slots of the session's business, in the business time zone.</summary>
internal sealed class AvailabilityTools(ChatbotDbContext db, ToolContext context, TimeProvider clock)
{
    private const int MaxSlots = 6;
    private const int MaxRangeDays = 14;

    private readonly TimeZoneInfo _zone = ToolSupport.ResolveTimezone(context.Timezone);

    public async Task<object> CheckAvailabilityAsync(
        [Description("Optional offeringId from search_catalog. Also returns the business's general slots that are not tied to one offering.")]
        string? offeringId = null,
        [Description("First day to look at, yyyy-MM-dd, in the business time zone. Required.")]
        string? fromDate = null,
        [Description("Last day to look at (inclusive), yyyy-MM-dd. Required. At most 14 days after fromDate.")]
        string? toDate = null,
        [Description("Optional part of the day in business-local time: morning (before 12:00), afternoon (12:00-17:59) or evening (18:00 on).")]
        string? partOfDay = null,
        CancellationToken cancellationToken = default)
    {
        if (!ToolSupport.TryParseDate(fromDate, _zone, out var from))
            return ToolSupport.Error("invalid_date", "fromDate is required and must be a date like 2026-05-20.");
        if (!ToolSupport.TryParseDate(toDate, _zone, out var to))
            return ToolSupport.Error("invalid_date", "toDate is required and must be a date like 2026-05-27.");
        if (to < from)
            return ToolSupport.Error("invalid_range", "toDate must not be before fromDate.");
        if (to.DayNumber - from.DayNumber > MaxRangeDays)
            return ToolSupport.Error("range_too_long", $"The range cannot exceed {MaxRangeDays} days. Ask again with a shorter range.");

        Func<int, bool>? hourMatches = null;
        if (!string.IsNullOrWhiteSpace(partOfDay))
        {
            hourMatches = partOfDay.Trim().ToLowerInvariant() switch
            {
                "morning" => hour => hour < 12,
                "afternoon" => hour => hour is >= 12 and < 18,
                "evening" => hour => hour >= 18,
                _ => null,
            };
            if (hourMatches is null)
                return ToolSupport.Error("invalid_part_of_day", "partOfDay must be morning, afternoon or evening.");
        }

        Guid? offering = null;
        if (!string.IsNullOrWhiteSpace(offeringId))
        {
            if (!Guid.TryParse(offeringId, out var parsed))
                return ToolSupport.Error("invalid_offering_id", "offeringId must be an id returned by search_catalog.");
            var exists = await db.Offerings.AnyAsync(o => o.Id == parsed && o.BusinessId == context.BusinessId && o.IsActive, cancellationToken);
            if (!exists)
                return ToolSupport.Error("offering_not_found", "Offering not found.");
            offering = parsed;
        }

        var now = clock.GetUtcNow();
        var rangeStart = ToolSupport.StartOfDayUtc(from, _zone);
        var rangeEnd = ToolSupport.StartOfDayUtc(to.AddDays(1), _zone);
        var earliest = rangeStart > now ? rangeStart : now;

        var query = db.AvailabilitySlots.AsNoTracking().Where(s =>
            s.BusinessId == context.BusinessId
            && s.StartsAt >= earliest && s.StartsAt < rangeEnd
            && s.Booked < s.Capacity);
        if (offering is { } id)
            query = query.Where(s => s.OfferingId == null || s.OfferingId == id);

        // Part of day depends on the business zone, so it is applied while streaming the (small, ordered) range.
        var slots = new List<AvailabilitySlot>(MaxSlots + 1);
        await foreach (var slot in query.OrderBy(s => s.StartsAt).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (hourMatches is not null && !hourMatches(TimeZoneInfo.ConvertTime(slot.StartsAt, _zone).Hour))
                continue;
            slots.Add(slot);
            if (slots.Count > MaxSlots)
                break;
        }

        return new
        {
            timezone = _zone.Id,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            slots = slots.Take(MaxSlots).Select(s => ToolSupport.SlotView(s, _zone)),
            hasMore = slots.Count > MaxSlots,
            note = slots.Count == 0 ? "No free slots in that range. Offer another range or day." : null,
        };
    }
}
