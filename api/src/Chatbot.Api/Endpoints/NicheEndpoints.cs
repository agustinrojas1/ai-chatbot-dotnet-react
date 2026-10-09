using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Api.Endpoints;

internal static class NicheEndpoints
{
    public static IEndpointRouteBuilder MapNicheEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/niches", ListNichesAsync);
        return app;
    }

    private static async Task<IResult> ListNichesAsync(ChatbotDbContext db, CancellationToken ct)
    {
        var businesses = await db.Businesses.AsNoTracking().ToListAsync(ct);

        var niches = NicheCatalog.All
            .Join(businesses, n => n.Niche, b => b.Niche, (n, b) => new
            {
                id = n.Id,
                name = n.Name,
                persona = b.PersonaName,
                headerName = n.HeaderName,
                avatar = new { emoji = n.Emoji, letter = n.Letter, colors = n.Colors },
                greeting = b.Greeting,
                quickReplies = b.QuickReplies,
            });

        return Results.Ok(niches);
    }
}
