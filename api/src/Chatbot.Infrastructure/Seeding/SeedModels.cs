using System.Text.Json;
using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>Everything the seeder needs to create one business: identity, catalog and availability plan.</summary>
internal sealed record BusinessSeed(
    Niche Niche,
    string Name,
    string PersonaName,
    string PersonaPromptResource,
    string Greeting,
    IReadOnlyList<string> QuickReplies,
    string Timezone,
    string Currency,
    IReadOnlyList<OfferingSeed> Offerings,
    SlotPlan Slots);

internal sealed record OfferingSeed(
    OfferingKind Kind,
    string Name,
    string Description,
    decimal Price,
    string PriceUnit,
    object Attributes)
{
    public JsonElement AttributesJson() => JsonSerializer.SerializeToElement(Attributes);
}

/// <summary>Opening hours, in the business time zone, expressed as one bookable slot grid per weekday.</summary>
internal sealed record SlotPlan(
    TimeSpan SlotLength,
    int Capacity,
    IReadOnlyDictionary<DayOfWeek, (TimeOnly Open, TimeOnly Close)> Hours);
