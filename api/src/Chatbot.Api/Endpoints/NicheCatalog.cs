using Chatbot.Core.Domain;

namespace Chatbot.Api.Endpoints;

/// <summary>Presentation of one niche. The business data (persona, greeting, quick replies) lives in the database.</summary>
/// <param name="Id">Public id used by the web app; it is not the <see cref="Niche"/> enum name.</param>
/// <param name="Name">Label of the niche selector, from the v1 UI.</param>
/// <param name="HeaderName">Chat header title, from the v1 UI.</param>
/// <param name="Colors">Start and end color of the avatar gradient.</param>
internal sealed record NicheInfo(string Id, Niche Niche, string Name, string HeaderName, string Emoji, string Letter, string[] Colors);

/// <summary>Static map between public niche ids and the database niches, with the v1 avatar look.</summary>
internal static class NicheCatalog
{
    public static readonly IReadOnlyList<NicheInfo> All =
    [
        new("realestate", Niche.RealEstate, "Real Estate", "Sofia | Real Estate AI", "🏠", "S", ["#128c7e", "#25d366"]),
        new("dental", Niche.Clinic, "Dental Clinic", "Dr. Smile Assistant", "🦷", "D", ["#0ea5e9", "#22d3ee"]),
        new("restaurant", Niche.Restaurant, "Restaurant", "Nico | Pizza Bot", "🍔", "N", ["#f97316", "#fbbf24"]),
    ];

    public static NicheInfo? FindById(string? id) =>
        All.FirstOrDefault(n => string.Equals(n.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static NicheInfo? FindByNiche(Niche niche) => All.FirstOrDefault(n => n.Niche == niche);
}
