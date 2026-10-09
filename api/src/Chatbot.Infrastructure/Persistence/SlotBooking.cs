using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Persistence;

/// <summary>Atomic slot capacity operations. Capacity is decided by the database, never by the model or app code.</summary>
public static class SlotBooking
{
    /// <summary>
    /// Takes one seat with a single statement:
    /// <c>UPDATE availability_slots SET "Booked" = "Booked" + 1 WHERE "Id" = @id AND "Booked" &lt; "Capacity"</c>.
    /// Concurrent callers are serialized by the row lock, so exactly <c>Capacity</c> of them win.
    /// </summary>
    /// <returns><c>true</c> if a seat was taken; <c>false</c> if the slot is full or does not exist.</returns>
    public static async Task<bool> TryReserveAsync(ChatbotDbContext db, Guid slotId, CancellationToken cancellationToken = default)
    {
        var updated = await db.AvailabilitySlots
            .Where(s => s.Id == slotId && s.Booked < s.Capacity)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Booked, x => x.Booked + 1), cancellationToken);
        return updated == 1;
    }
}
