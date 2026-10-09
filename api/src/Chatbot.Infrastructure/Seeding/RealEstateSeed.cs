using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>
/// PrimeHaven Realty (Sofia). The first three properties are ported from the v1 app; the rest are invented
/// (see docs/seed-notes.md). Attribute keys are the same for every property: type, neighborhood, rooms, listing, features.
/// </summary>
internal static class RealEstateSeed
{
    private static readonly Dictionary<DayOfWeek, (TimeOnly, TimeOnly)> Hours = Enumerable
        .Range(1, 6) // Monday..Saturday
        .ToDictionary(d => (DayOfWeek)d, _ => (new TimeOnly(10, 0), new TimeOnly(18, 0)));

    public static BusinessSeed Create() => new(
        Niche.RealEstate,
        "PrimeHaven Realty",
        "Sofia",
        "Personas.sofia.md",
        "Hi! I'm Sofia, your real estate AI assistant at PrimeHaven Realty. Ask me about available properties or tap a suggestion below.",
        [
            "Show me rentals under $600/month",
            "Tell me about the 4-room house in Belgrano",
            "I want to schedule a viewing",
        ],
        "America/Argentina/Buenos_Aires",
        "USD",
        Offerings,
        new SlotPlan(TimeSpan.FromMinutes(30), Capacity: 1, Hours));

    private static OfferingSeed Rent(string name, string description, decimal price, string type, string neighborhood, int rooms, params string[] features) =>
        new(OfferingKind.Property, name, description, price, "month",
            new { type, neighborhood, rooms, listing = "rent", features });

    private static OfferingSeed Sale(string name, string description, decimal price, string type, string neighborhood, int rooms, params string[] features) =>
        new(OfferingKind.Property, name, description, price, "sale",
            new { type, neighborhood, rooms, listing = "sale", features });

    private static readonly OfferingSeed[] Offerings =
    [
        // Ported from v1.
        Rent("2-room apartment in Palermo", "2-room apartment in Palermo with low HOA fees and a balcony.", 550, "apartment", "Palermo", 2, "low HOA fees", "balcony"),
        Sale("4-room house in Belgrano", "4-room house in Belgrano with a private garden, swimming pool and 2-car garage.", 320000, "house", "Belgrano", 4, "private garden", "swimming pool", "2-car garage"),
        Rent("3-room townhouse (PH) in Almagro", "Newly renovated 3-room townhouse (PH) in Almagro with a private patio and no HOA fees.", 600, "townhouse (PH)", "Almagro", 3, "newly renovated", "private patio", "no HOA"),

        // Invented for this version.
        Rent("1-room studio in Recoleta", "Furnished studio in Recoleta, in a building with a 24-hour doorman.", 420, "studio", "Recoleta", 1, "furnished", "24-hour doorman"),
        Rent("3-room apartment in Caballito", "Bright 3-room apartment in Caballito with two balconies, a laundry room and pets welcome.", 780, "apartment", "Caballito", 3, "two balconies", "laundry room", "pet friendly"),
        Rent("2-room loft in San Telmo", "2-room loft in San Telmo with high ceilings and exposed brick walls.", 650, "loft", "San Telmo", 2, "high ceilings", "exposed brick", "hardwood floors"),
        Sale("2-room apartment in Villa Crespo", "2-room apartment in Villa Crespo with a renovated kitchen and shared rooftop terrace.", 145000, "apartment", "Villa Crespo", 2, "renovated kitchen", "rooftop terrace", "bright"),
        Sale("3-room apartment in Belgrano", "3-room apartment in Belgrano with a balcony, storage unit and building gym.", 210000, "apartment", "Belgrano", 3, "balcony", "storage unit", "building gym"),
        Sale("5-room house in Núñez", "5-room house in Núñez with a private garden, grill area and 2-car garage.", 480000, "house", "Núñez", 5, "private garden", "grill area", "2-car garage"),
    ];
}
