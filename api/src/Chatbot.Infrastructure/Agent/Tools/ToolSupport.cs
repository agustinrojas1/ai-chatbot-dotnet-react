using System.Globalization;
using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary>Small helpers shared by the tools: error shape, time zone handling and slot formatting.</summary>
internal static class ToolSupport
{
    /// <summary>
    /// The error shape every tool returns for invalid arguments or a business-rule rejection.
    /// <c>code</c> is stable (for traces and evals); <c>error</c> is the readable text the model reacts to.
    /// </summary>
    public static object Error(string code, string message) => new { error = message, code };

    public static TimeZoneInfo ResolveTimezone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Accepts <c>yyyy-MM-dd</c>, or any ISO date-time (converted to a date in the business zone).</summary>
    public static bool TryParseDate(string? text, TimeZoneInfo zone, out DateOnly date)
    {
        text = text?.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
        {
            // No offset in the text: it is already a business-local date. With an offset or Z: convert it.
            date = moment.Kind == DateTimeKind.Unspecified
                ? DateOnly.FromDateTime(moment)
                : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(moment), zone).DateTime);
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>The UTC instant at which <paramref name="date"/> starts in <paramref name="zone"/>.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1); // midnight skipped by a DST jump
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>Slot as the model sees it: ISO-8601 with the business offset, plus a friendly label.</summary>
    public static object SlotView(AvailabilitySlot slot, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(slot.StartsAt, zone);
        var end = TimeZoneInfo.ConvertTime(slot.EndsAt, zone);
        return new
        {
            slotId = slot.Id,
            offeringId = slot.OfferingId,
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            endsAt = end.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            label = start.ToString("ddd, MMM d 'at' h:mm tt", CultureInfo.InvariantCulture),
            spotsLeft = slot.Capacity - slot.Booked,
        };
    }
}
