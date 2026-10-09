using System.Text;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Evals.Cases;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Evals.Running;

/// <summary>
/// Plays a case against the real <see cref="AgentService"/>: a fresh session, then each user turn in order. Like a
/// request, every turn gets its own scope (and so its own <c>DbContext</c>). Nothing here judges the result; it only
/// records what happened, including the database state after every turn.
/// </summary>
internal sealed class ConversationRunner(IServiceProvider services)
{
    public const string ProviderQuota = "infrastructure: provider quota";

    private readonly Dictionary<EvalNiche, IReadOnlyList<string>> _protectedTerms = [];

    public async Task<Transcript> RunAsync(EvalCase evalCase, int iteration, CancellationToken ct)
    {
        var sessionId = await CreateSessionAsync(evalCase.Niche, ct);
        var turns = new List<TurnResult>();
        string? failure = null;

        foreach (var turn in evalCase.Turns)
        {
            if (failure is not null)
            {
                turns.Add(new TurnResult
                {
                    User = turn.Say,
                    Error = "skipped: a previous turn failed",
                    Bookings = turns[^1].Bookings,
                    Handoffs = turns[^1].Handoffs,
                });
                continue;
            }

            var result = await RunTurnAsync(sessionId, turn.Say, ct);
            turns.Add(result);
            failure = result.Error;
        }

        return new Transcript(evalCase, iteration, turns, await ProtectedTermsAsync(evalCase.Niche, ct));
    }

    private async Task<Guid> CreateSessionAsync(EvalNiche niche, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatbotDbContext>();
        var kind = BusinessNiche(niche);
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Niche == kind, ct);

        var session = new ChatSession { BusinessId = business.Id, ClientIpHash = "evals" };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    private async Task<TurnResult> RunTurnAsync(Guid sessionId, string say, CancellationToken ct)
    {
        var reply = new StringBuilder();
        var toolCalls = new List<ToolCallRecord>();
        string? error = null;
        RunCompleted? completed = null;
        var monitor = services.GetService<QuotaMonitor>();
        var exhaustedBefore = monitor?.Exhausted ?? 0;

        try
        {
            await using var scope = services.CreateAsyncScope();
            var agent = scope.ServiceProvider.GetRequiredService<AgentService>();
            await foreach (var e in agent.RunAsync(sessionId, say, ct))
            {
                switch (e)
                {
                    case Token token:
                        reply.Append(token.Text);
                        break;
                    case Trace { Type: TraceEventType.ToolCall } trace:
                        toolCalls.Add(new ToolCallRecord(trace.Name, trace.Input, trace.Output));
                        break;
                    case RunCompleted done:
                        completed = done;
                        break;
                    case Error failed:
                        error = failed.Message;
                        break;
                    case Rejected rejected:
                        error = $"message rejected ({rejected.Reason}): {rejected.Message}";
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null && completed is null)
            error = "the agent produced no result";

        var refusedByProvider = error is not null && monitor is not null && monitor.Exhausted > exhaustedBefore;

        var (bookings, handoffs) = await SessionStateAsync(sessionId, ct);
        return new TurnResult
        {
            User = say,
            InfrastructureError = refusedByProvider ? ProviderQuota : null,
            Reply = reply.ToString().Trim(),
            ToolCalls = toolCalls,
            Error = error,
            TokensIn = completed?.TokensIn ?? 0,
            TokensOut = completed?.TokensOut ?? 0,
            CostUsd = completed?.CostUsd ?? 0,
            DurationMs = completed?.DurationMs ?? 0,
            Model = completed?.Model,
            Provider = completed?.Provider,
            Bookings = bookings,
            Handoffs = handoffs,
        };
    }

    /// <summary>What the tools left in the database for this session: bookings (with the total recomputed from catalog prices) and handoffs.</summary>
    private async Task<(List<BookingSnapshot> Bookings, int Handoffs)> SessionStateAsync(Guid sessionId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatbotDbContext>();

        var bookings = await db.Bookings.AsNoTracking().Where(b => b.SessionId == sessionId).OrderBy(b => b.CreatedAt).ToListAsync(ct);
        var lines = bookings.Select(b => OrderLines(b.Items)).ToList();
        var offeringIds = lines.SelectMany(l => l).Select(l => l.OfferingId).Distinct().ToList();
        var prices = await db.Offerings.AsNoTracking().Where(o => offeringIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Price, ct);

        var snapshots = bookings.Select((b, i) => new BookingSnapshot(
            b.Status, b.Total,
            lines[i].Count == 0 ? null : lines[i].Sum(l => prices.GetValueOrDefault(l.OfferingId) * l.Qty),
            b.CustomerName)).ToList();

        var handoffs = await db.HandoffRequests.AsNoTracking().CountAsync(h => h.SessionId == sessionId, ct);
        return (snapshots, handoffs);
    }

    /// <summary>The order lines <c>[{offeringId, qty}]</c> stored in <c>Booking.Items</c>.</summary>
    private static List<(Guid OfferingId, int Qty)> OrderLines(JsonElement? items)
    {
        var lines = new List<(Guid, int)>();
        if (items is not { ValueKind: JsonValueKind.Array } array)
            return lines;

        foreach (var line in array.EnumerateArray())
        {
            if (line.TryGetProperty("offeringId", out var id) && Guid.TryParse(id.GetString(), out var offeringId)
                && line.TryGetProperty("qty", out var qty) && qty.TryGetInt32(out var quantity))
                lines.Add((offeringId, quantity));
        }

        return lines;
    }

    private async Task<IReadOnlyList<string>> ProtectedTermsAsync(EvalNiche niche, CancellationToken ct)
    {
        if (_protectedTerms.TryGetValue(niche, out var cached))
            return cached;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatbotDbContext>();
        var kind = BusinessNiche(niche);
        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Niche == kind, ct);
        var offerings = await db.Offerings.AsNoTracking().Where(o => o.BusinessId == business.Id).ToListAsync(ct);

        var terms = new List<string> { business.Name };
        foreach (var offering in offerings)
        {
            terms.Add(offering.Name);
            if (offering.Attributes.ValueKind == JsonValueKind.Object
                && offering.Attributes.TryGetProperty("neighborhood", out var neighborhood)
                && neighborhood.GetString() is { } name)
                terms.Add(name);
        }

        // Longest first, so "2-room apartment in Palermo" is removed before "Palermo" would leave "2-room apartment in".
        return _protectedTerms[niche] = terms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ToList();
    }

    private static Niche BusinessNiche(EvalNiche niche) => niche switch
    {
        EvalNiche.RealEstate => Niche.RealEstate,
        EvalNiche.Dental => Niche.Clinic,
        _ => Niche.Restaurant,
    };
}
