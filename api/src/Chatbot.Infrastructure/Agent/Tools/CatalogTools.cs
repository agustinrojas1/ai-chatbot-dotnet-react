using System.ComponentModel;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary>
/// <c>search_catalog</c> and <c>get_offering_details</c>. Every query is filtered by the business of the
/// session; an offering of another business is indistinguishable from one that does not exist.
/// </summary>
internal sealed class CatalogTools(ChatbotDbContext db, ToolContext context)
{
    private const int MaxResults = 5;
    private const int MaxQueryTerms = 8;

    public async Task<object> SearchCatalogAsync(
        [Description("Free text matched (case-insensitive) against offering names and descriptions, e.g. 'pepperoni' or 'balcony'. Omit to list everything.")]
        string? query = null,
        [Description("Maximum price, inclusive, in the business currency.")]
        decimal? maxPrice = null,
        [Description("Minimum price, inclusive, in the business currency.")]
        decimal? minPrice = null,
        [Description("Optional kind: Property, Treatment or MenuItem.")]
        string? kind = null,
        [Description("Attribute filters as an object, e.g. {\"neighborhood\":\"Palermo\",\"rooms\":2} or {\"glutenFree\":true} or {\"features\":\"balcony\"}. " +
                     "Keys and string values are matched exactly (case-sensitive); use numbers and booleans for numeric and boolean attributes. " +
                     "A list attribute matches when it contains the value.")]
        Dictionary<string, JsonElement>? filters = null,
        CancellationToken cancellationToken = default)
    {
        if (minPrice < 0 || maxPrice < 0)
            return ToolSupport.Error("invalid_price", "minPrice and maxPrice must not be negative.");
        if (minPrice > maxPrice)
            return ToolSupport.Error("invalid_price", "minPrice cannot be greater than maxPrice.");

        OfferingKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!TryParseKind(kind, out var k))
                return ToolSupport.Error("invalid_kind", $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", Enum.GetNames<OfferingKind>())}.");
            parsedKind = k;
        }

        var offerings = db.Offerings.AsNoTracking().Where(o => o.BusinessId == context.BusinessId && o.IsActive);

        if (parsedKind is { } wanted)
            offerings = offerings.Where(o => o.Kind == wanted);
        if (minPrice is { } min)
            offerings = offerings.Where(o => o.Price >= min);
        if (maxPrice is { } max)
            offerings = offerings.Where(o => o.Price <= max);

        // Every word must appear in the name or the description, in any order.
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxQueryTerms);
        foreach (var term in terms)
        {
            var pattern = $"%{EscapeLike(term)}%";
            offerings = offerings.Where(o => EF.Functions.ILike(o.Name, pattern) || EF.Functions.ILike(o.Description, pattern));
        }

        if (filters is not null)
        {
            foreach (var (key, value) in filters)
            {
                if (!TryBuildContainment(key, value, out var asGiven, out var asListItem, out var problem))
                    return ToolSupport.Error("invalid_filter", problem);

                // jsonb containment (@>, served by the GIN index). The same value is also tried as an element
                // of a list attribute, so {"features":"balcony"} matches {"features":["balcony","pool"]}.
                offerings = offerings.Where(o =>
                    EF.Functions.JsonContains(o.Attributes, asGiven) || EF.Functions.JsonContains(o.Attributes, asListItem));
            }
        }

        var total = await offerings.CountAsync(cancellationToken);
        var found = await offerings.OrderBy(o => o.Price).ThenBy(o => o.Name).Take(MaxResults).ToListAsync(cancellationToken);
        var currency = await CurrencyAsync(cancellationToken);

        var results = found.Select(o => new
        {
            offeringId = o.Id,
            kind = o.Kind.ToString(),
            name = o.Name,
            price = o.Price,
            priceUnit = o.PriceUnit,
            currency,
            attributes = o.Attributes,
        });

        return new
        {
            totalMatches = total,
            shown = found.Count,
            results,
            note = found.Count == 0
                ? "No active offering matches. Filter keys and string values are exact; retry with fewer filters or without them to see the attribute keys."
                : total > found.Count ? $"Showing the {found.Count} cheapest of {total} matches; narrow the search to see others." : null,
        };
    }

    public async Task<object> GetOfferingDetailsAsync(
        [Description("The offeringId returned by search_catalog.")]
        string? offeringId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(offeringId, out var id))
            return ToolSupport.Error("invalid_offering_id", "offeringId is required and must be an id returned by search_catalog.");

        var offering = await db.Offerings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.BusinessId == context.BusinessId && o.IsActive, cancellationToken);
        if (offering is null)
            return ToolSupport.Error("offering_not_found", "Offering not found.");

        return new
        {
            offeringId = offering.Id,
            kind = offering.Kind.ToString(),
            name = offering.Name,
            description = offering.Description,
            price = offering.Price,
            priceUnit = offering.PriceUnit,
            currency = await CurrencyAsync(cancellationToken),
            attributes = offering.Attributes,
        };
    }

    private Task<string> CurrencyAsync(CancellationToken cancellationToken) =>
        db.Businesses.AsNoTracking().Where(b => b.Id == context.BusinessId).Select(b => b.Currency).FirstAsync(cancellationToken);

    /// <summary>Accepts "MenuItem", "menu item", "menu_item"... (case, spaces, dashes and underscores ignored).</summary>
    private static bool TryParseKind(string text, out OfferingKind kind)
    {
        var normalized = new string(text.Where(char.IsLetter).ToArray());
        return Enum.TryParse(normalized, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Builds <c>{"key": value}</c> and <c>{"key": [value]}</c> for a scalar value (a list value is used as is).</summary>
    private static bool TryBuildContainment(string key, JsonElement value, out string asGiven, out string asListItem, out string problem)
    {
        asGiven = asListItem = problem = "";
        if (string.IsNullOrWhiteSpace(key))
        {
            problem = "Filter keys must not be empty.";
            return false;
        }

        var scalar = value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
        var list = value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
        if (!scalar && !list)
        {
            problem = $"Filter '{key}' must be a string, number, boolean or a list of those.";
            return false;
        }

        asGiven = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [key] = value });
        asListItem = scalar
            ? JsonSerializer.Serialize(new Dictionary<string, JsonElement[]> { [key] = [value] })
            : asGiven;
        return true;
    }
}
