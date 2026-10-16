using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>
/// Idempotent seed: creates whatever is missing and never overwrites existing rows, so edits made in the
/// database survive a restart. Availability is a rolling window: every run tops up the next
/// <see cref="SlotWindowDays"/> days.
/// </summary>
public sealed class DatabaseSeeder(ChatbotDbContext db, TimeProvider clock, ILogger<DatabaseSeeder> logger) : IDatabaseSeeder
{
    public const int SlotWindowDays = 14;

    private static readonly BusinessSeed[] Businesses =
    [
        RealEstateSeed.Create(),
        ClinicSeed.Create(),
        RestaurantSeed.Create(),
    ];

    /// <summary>
    /// Gemini verified on https://ai.google.dev/gemini-api/docs/pricing (paid tier, standard) on 2026-10-09. The Groq
    /// models verified on https://console.groq.com/docs/model/openai/gpt-oss-120b and .../gpt-oss-20b on 2026-10-16.
    /// </summary>
    private static readonly ModelPricing[] Pricing =
    [
        new() { Model = "gemini-3.5-flash-lite", InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m },
        new() { Model = "openai/gpt-oss-120b", InputPerMillionUsd = 0.15m, OutputPerMillionUsd = 0.60m },
        new() { Model = "openai/gpt-oss-20b", InputPerMillionUsd = 0.075m, OutputPerMillionUsd = 0.30m },
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        int businessesAdded = 0, offeringsAdded = 0, slotsAdded = 0;

        foreach (var seed in Businesses)
        {
            var business = await db.Businesses.SingleOrDefaultAsync(b => b.Niche == seed.Niche, cancellationToken);
            if (business is null)
            {
                business = new Business
                {
                    Niche = seed.Niche,
                    Name = seed.Name,
                    PersonaName = seed.PersonaName,
                    PersonaPrompt = PersonaPrompts.Load(seed.PersonaPromptResource),
                    Greeting = seed.Greeting,
                    QuickReplies = [.. seed.QuickReplies],
                    Timezone = seed.Timezone,
                    Currency = seed.Currency,
                };
                db.Businesses.Add(business);
                businessesAdded++;
            }

            offeringsAdded += await AddMissingOfferingsAsync(business, seed, cancellationToken);
            slotsAdded += await AddMissingSlotsAsync(business, seed.Slots, now, cancellationToken);
        }

        var knownModels = await db.ModelPricings.Select(p => p.Model).ToListAsync(cancellationToken);
        db.ModelPricings.AddRange(Pricing.Where(p => !knownModels.Contains(p.Model)));

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seed finished: {Businesses} businesses, {Offerings} offerings and {Slots} slots added.",
            businessesAdded, offeringsAdded, slotsAdded);
    }

    private async Task<int> AddMissingOfferingsAsync(Business business, BusinessSeed seed, CancellationToken ct)
    {
        var existing = (await db.Offerings
                .Where(o => o.BusinessId == business.Id)
                .Select(o => o.Name)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = seed.Offerings.Where(o => !existing.Contains(o.Name)).ToList();
        db.Offerings.AddRange(missing.Select(o => new Offering
        {
            BusinessId = business.Id,
            Kind = o.Kind,
            Name = o.Name,
            Description = o.Description,
            Price = o.Price,
            PriceUnit = o.PriceUnit,
            Attributes = o.AttributesJson(),
        }));
        return missing.Count;
    }

    /// <summary>
    /// Adds general (no offering) slots for the window starting today (UTC date), skipping slots that already started
    /// and slots that already exist. Two slots are the same when their <c>StartsAt</c> matches.
    /// </summary>
    private async Task<int> AddMissingSlotsAsync(Business business, SlotPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(business.Timezone);
        var firstDay = DateOnly.FromDateTime(now.UtcDateTime);

        var generated = GenerateSlots(plan, zone, firstDay, SlotWindowDays)
            .Where(s => s.StartsAt > now)
            .ToList();
        if (generated.Count == 0) return 0;

        var from = generated.Min(s => s.StartsAt);
        var existing = (await db.AvailabilitySlots
                .Where(s => s.BusinessId == business.Id && s.OfferingId == null && s.StartsAt >= from)
                .Select(s => s.StartsAt)
                .ToListAsync(ct))
            .ToHashSet();

        var missing = generated.Where(s => !existing.Contains(s.StartsAt)).ToList();
        db.AvailabilitySlots.AddRange(missing.Select(s => new AvailabilitySlot
        {
            BusinessId = business.Id,
            StartsAt = s.StartsAt,
            EndsAt = s.EndsAt,
            Capacity = plan.Capacity,
        }));
        return missing.Count;
    }

    /// <summary>Slots for <paramref name="days"/> consecutive local dates; opening hours are interpreted in <paramref name="zone"/>.</summary>
    internal static IEnumerable<(DateTimeOffset StartsAt, DateTimeOffset EndsAt)> GenerateSlots(
        SlotPlan plan, TimeZoneInfo zone, DateOnly firstDay, int days)
    {
        for (var i = 0; i < days; i++)
        {
            var day = firstDay.AddDays(i);
            if (!plan.Hours.TryGetValue(day.DayOfWeek, out var hours)) continue;

            var close = day.ToDateTime(hours.Close);
            for (var start = day.ToDateTime(hours.Open); start + plan.SlotLength <= close; start += plan.SlotLength)
            {
                yield return (ToUtc(start, zone), ToUtc(start + plan.SlotLength, zone));
            }
        }
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
