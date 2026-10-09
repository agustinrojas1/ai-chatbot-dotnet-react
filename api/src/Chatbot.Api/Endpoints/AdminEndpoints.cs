using System.Security.Cryptography;
using System.Text;
using Chatbot.Core.Agent;
using Chatbot.Api.Security;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tracing;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chatbot.Api.Endpoints;

/// <summary>Bound from the <c>Admin</c> configuration section (SPEC 9).</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Value the <c>X-Admin-Key</c> header must carry. Blank disables the admin endpoints (503).</summary>
    public string? Key { get; set; }

    /// <summary>
    /// Mask phone numbers in the admin responses (<c>+54 11 ****-4444</c>): <c>customerPhone</c>, and any number inside the
    /// free text (booking notes and order options, handoff reason and summary). On by default, for the public demo.
    /// </summary>
    public bool MaskPhones { get; set; } = true;
}

/// <summary>
/// Read-only lists for the business owner view. Demo-grade protection: one shared key in a header. The lists are
/// newest first and capped at <see cref="MaxItems"/>.
/// </summary>
internal static class AdminEndpoints
{
    public const string KeyHeader = "X-Admin-Key";
    public const int MaxItems = 200;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // The rate limit also bounds how fast a key can be guessed.
        var admin = app.MapGroup("/api/admin").RequireRateLimiting(AbuseProtection.AdminPolicy).AddEndpointFilter(RequireKeyAsync);
        admin.MapGet("/bookings", ListBookingsAsync);
        admin.MapGet("/handoffs", ListHandoffsAsync);
        return app;
    }

    /// <summary>503 when no key is configured (never an open door), 401 when the header is missing or wrong.</summary>
    private static async ValueTask<object?> RequireKeyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var expected = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.Key;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Results.Problem(
                title: "Admin disabled",
                detail: "The admin endpoints are disabled: set Admin:Key (ADMIN_KEY) to enable them.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!KeyMatches(expected, http.Request.Headers[KeyHeader].ToString()))
        {
            return Results.Problem(
                title: "Unauthorized",
                detail: $"Missing or invalid {KeyHeader} header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    /// <summary>Compares SHA-256 digests in constant time, so neither the content nor the length of the key leaks through timing.</summary>
    private static bool KeyMatches(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));

    private static async Task<IResult> ListBookingsAsync(
        string? niche, ChatbotDbContext db, IOptions<AdminOptions> options, CancellationToken ct)
    {
        if (!TryResolveNiche(niche, out var filter, out var problem))
            return problem!;

        var rows = await (
                from b in db.Bookings.AsNoTracking()
                join biz in db.Businesses.AsNoTracking() on b.BusinessId equals biz.Id
                where filter == null || biz.Niche == filter
                orderby b.CreatedAt descending, b.Id descending
                select new
                {
                    b.Id,
                    biz.Niche,
                    b.SessionId,
                    b.Status,
                    b.CustomerName,
                    b.CustomerPhone,
                    SlotStartsAt = b.Slot == null ? (DateTimeOffset?)null : b.Slot.StartsAt,
                    OfferingName = b.Offering == null ? null : b.Offering.Name,
                    b.Items,
                    b.Total,
                    b.Notes,
                    b.CreatedAt,
                })
            .Take(MaxItems)
            .ToListAsync(ct);

        // customerPhone is masked as a phone; notes and order options are free text the model wrote, so any number in them is masked too.
        var maskPhones = options.Value.MaskPhones;
        return Results.Ok(rows.Select(r => new
        {
            id = r.Id,
            niche = NicheCatalog.FindByNiche(r.Niche)!.Id,
            sessionId = r.SessionId,
            status = r.Status.ToString(),
            customerName = r.CustomerName,
            customerPhone = maskPhones ? PhoneMasker.Mask(r.CustomerPhone) : r.CustomerPhone,
            slotStartsAt = r.SlotStartsAt,
            offeringName = r.OfferingName,
            items = maskPhones && r.Items is { } items ? TracePayload.MaskPhones(items) : r.Items,
            total = r.Total,
            notes = maskPhones && r.Notes is not null ? PhoneMasker.MaskInText(r.Notes) : r.Notes,
            createdAt = r.CreatedAt,
        }));
    }

    private static async Task<IResult> ListHandoffsAsync(string? niche, ChatbotDbContext db, IOptions<AdminOptions> options, CancellationToken ct)
    {
        if (!TryResolveNiche(niche, out var filter, out var problem))
            return problem!;

        var rows = await (
                from h in db.HandoffRequests.AsNoTracking()
                join s in db.ChatSessions.AsNoTracking() on h.SessionId equals s.Id
                join biz in db.Businesses.AsNoTracking() on s.BusinessId equals biz.Id
                where filter == null || biz.Niche == filter
                orderby h.CreatedAt descending, h.Id descending
                select new { h.Id, biz.Niche, h.SessionId, h.Reason, h.Summary, h.Status, h.CreatedAt })
            .Take(MaxItems)
            .ToListAsync(ct);

        // Reason and summary are written by the model, and usually repeat what the customer said, phone included.
        var maskPhones = options.Value.MaskPhones;
        return Results.Ok(rows.Select(r => new
        {
            id = r.Id,
            niche = NicheCatalog.FindByNiche(r.Niche)!.Id,
            sessionId = r.SessionId,
            reason = maskPhones ? PhoneMasker.MaskInText(r.Reason) : r.Reason,
            summary = maskPhones ? PhoneMasker.MaskInText(r.Summary) : r.Summary,
            status = r.Status.ToString(),
            createdAt = r.CreatedAt,
        }));
    }

    /// <summary>No niche means all of them. An unknown id is a 400, not an empty list that hides a typo.</summary>
    private static bool TryResolveNiche(string? niche, out Niche? filter, out IResult? problem)
    {
        filter = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(niche))
            return true;

        var info = NicheCatalog.FindById(niche);
        if (info is null)
        {
            problem = Results.Problem(
                title: "Unknown niche",
                detail: $"niche must be one of: {string.Join(", ", NicheCatalog.All.Select(n => n.Id))}.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        filter = info.Niche;
        return true;
    }
}
