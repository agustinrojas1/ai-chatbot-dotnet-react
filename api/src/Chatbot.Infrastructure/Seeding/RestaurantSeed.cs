using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>
/// Luigi's Pizzeria (Nico). Margherita, Pepperoni and Veggie (names, prices; Margherita and Pepperoni descriptions) are ported
/// from the v1 app; everything else is invented (see docs/seed-notes.md).
/// Attribute keys: category, vegetarian, glutenFree, glutenFreeCrustAvailable, glutenFreeCrustSurcharge, ingredients.
/// <c>glutenFree</c> means the item itself is gluten-free; pizzas are not, but accept a gluten-free crust for +$2.
/// Slots are delivery windows, not table reservations: 4 orders per half hour.
/// </summary>
internal static class RestaurantSeed
{
    private static readonly Dictionary<DayOfWeek, (TimeOnly, TimeOnly)> Hours = Enum
        .GetValues<DayOfWeek>()
        .ToDictionary(d => d, _ => (new TimeOnly(12, 0), new TimeOnly(22, 0)));

    public static BusinessSeed Create() => new(
        Niche.Restaurant,
        "Luigi's Pizzeria",
        "Nico",
        "Personas.nico.md",
        "Hi! I'm Nico, Luigi's Pizzeria ordering assistant. See the menu, ask about options or order — tap a suggestion below.",
        [
            "Send me the menu",
            "Do you have gluten-free pizza?",
            "I want to order a Pepperoni pizza",
        ],
        "America/Chicago",
        "USD",
        Offerings,
        new SlotPlan(TimeSpan.FromMinutes(30), Capacity: 4, Hours));

    private static OfferingSeed Pizza(string name, string description, decimal price, bool vegetarian, params string[] ingredients) =>
        Item(name, description, price, "pizza", vegetarian, glutenFree: false, glutenFreeCrust: true, ingredients);

    private static OfferingSeed Item(string name, string description, decimal price, string category, bool vegetarian, bool glutenFree, bool glutenFreeCrust, params string[] ingredients) =>
        new(OfferingKind.MenuItem, name, description, price, "item", new
        {
            category,
            vegetarian,
            glutenFree,
            glutenFreeCrustAvailable = glutenFreeCrust,
            glutenFreeCrustSurcharge = glutenFreeCrust ? 2 : 0,
            ingredients,
        });

    private static readonly OfferingSeed[] Offerings =
    [
        // Ported from v1. Margherita's vegetarian flag is derived from its ingredients; Veggie's ingredients are invented.
        Pizza("Margherita", "Fresh mozzarella, basil & tomato sauce", 12, vegetarian: true, "mozzarella", "basil", "tomato sauce"),
        Pizza("Pepperoni", "Double pepperoni & melted cheese", 15, vegetarian: false, "pepperoni", "mozzarella", "tomato sauce"),
        Pizza("Veggie", "Roasted bell peppers, mushrooms, olives and red onion on tomato sauce", 14, vegetarian: true, "bell pepper", "mushrooms", "olives", "red onion", "mozzarella", "tomato sauce"),

        // Invented for this version.
        Pizza("Quattro Formaggi", "Mozzarella, gorgonzola, parmesan and fontina", 16, vegetarian: true, "mozzarella", "gorgonzola", "parmesan", "fontina"),
        Pizza("Prosciutto & Mushroom", "Prosciutto cotto, mushrooms and mozzarella on tomato sauce", 17, vegetarian: false, "prosciutto cotto", "mushrooms", "mozzarella", "tomato sauce"),
        Item("Caprese Salad", "Fresh mozzarella, ripe tomato and basil with olive oil", 9, "side", vegetarian: true, glutenFree: true, glutenFreeCrust: false, "mozzarella", "tomato", "basil", "olive oil"),
        Item("Gluten-Free Penne Arrabbiata", "Gluten-free penne in a spicy tomato and garlic sauce", 13, "pasta", vegetarian: true, glutenFree: true, glutenFreeCrust: false, "gluten-free penne", "tomato", "garlic", "chili"),
        Item("Garlic Bread", "Oven-baked bread with garlic butter and parsley", 5, "side", vegetarian: true, glutenFree: false, glutenFreeCrust: false, "bread", "butter", "garlic", "parsley"),
        Item("Tiramisu", "Classic mascarpone and espresso dessert", 7, "dessert", vegetarian: true, glutenFree: false, glutenFreeCrust: false, "mascarpone", "espresso", "ladyfingers", "cocoa"),
        Item("Limonata", "Sparkling Italian lemonade, 330 ml", 4, "drink", vegetarian: true, glutenFree: true, glutenFreeCrust: false, "lemon", "sparkling water", "sugar"),
    ];
}
