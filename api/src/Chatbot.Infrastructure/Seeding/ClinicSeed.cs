using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>
/// Smile Dental Clinic (Alex). Names and prices of the first three treatments are ported from the v1 app;
/// their descriptions, categories and durations, and every other treatment, are invented (see docs/seed-notes.md).
/// Attribute keys: category, durationMinutes. Orthodontics is deliberately absent so the "not in the catalog" path can be exercised.
/// </summary>
internal static class ClinicSeed
{
    // v1: "Hours: Mon-Fri 9:00-18:00, Sat 9:00-13:00".
    private static readonly Dictionary<DayOfWeek, (TimeOnly, TimeOnly)> Hours = new()
    {
        [DayOfWeek.Monday] = (new TimeOnly(9, 0), new TimeOnly(18, 0)),
        [DayOfWeek.Tuesday] = (new TimeOnly(9, 0), new TimeOnly(18, 0)),
        [DayOfWeek.Wednesday] = (new TimeOnly(9, 0), new TimeOnly(18, 0)),
        [DayOfWeek.Thursday] = (new TimeOnly(9, 0), new TimeOnly(18, 0)),
        [DayOfWeek.Friday] = (new TimeOnly(9, 0), new TimeOnly(18, 0)),
        [DayOfWeek.Saturday] = (new TimeOnly(9, 0), new TimeOnly(13, 0)),
    };

    public static BusinessSeed Create() => new(
        Niche.Clinic,
        "Smile Dental Clinic",
        "Alex",
        "Personas.alex.md",
        "Hi! I'm Alex, the receptionist at Smile Dental Clinic. Ask me about treatments, prices or insurance, or tap a suggestion below.",
        [
            "How much is teeth cleaning?",
            "Do you take insurance?",
            "Book an appointment for tomorrow",
        ],
        "America/New_York",
        "USD",
        Offerings,
        new SlotPlan(TimeSpan.FromMinutes(30), Capacity: 1, Hours));

    private static OfferingSeed Treatment(string name, string description, decimal price, string category, int durationMinutes) =>
        new(OfferingKind.Treatment, name, description, price, "item", new { category, durationMinutes });

    private static readonly OfferingSeed[] Offerings =
    [
        // Ported from v1 (name and price only).
        Treatment("Teeth Cleaning", "Professional cleaning and polish to remove plaque and tartar.", 80, "preventive", 30),
        Treatment("Teeth Whitening", "In-office whitening for a brighter smile.", 200, "cosmetic", 60),
        Treatment("Check-up & Consultation", "Full oral exam and a conversation with the dentist about your treatment options.", 50, "diagnostic", 30),

        // Invented for this version.
        Treatment("Fluoride Treatment", "Topical fluoride application to strengthen enamel.", 35, "preventive", 30),
        Treatment("Dental X-Rays", "Digital X-rays of the teeth and jaw.", 60, "diagnostic", 30),
        Treatment("Children's Check-up", "Gentle exam and cleaning for children under 12.", 40, "pediatric", 30),
        Treatment("Composite Filling", "Tooth-colored filling to repair a cavity.", 150, "restorative", 60),
        Treatment("Simple Tooth Extraction", "Removal of a single, non-impacted tooth under local anesthesia.", 180, "surgical", 60),
        Treatment("Emergency Dental Visit", "Same-day assessment and pain relief for urgent dental problems.", 90, "emergency", 30),
    ];
}
