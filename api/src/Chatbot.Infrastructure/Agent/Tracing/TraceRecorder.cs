using System.Text.Json;
using Chatbot.Core.Agent;
using Chatbot.Core.Domain;

namespace Chatbot.Infrastructure.Agent.Tracing;

/// <summary>
/// Collects the <see cref="TraceEvent"/>s of one run. It is the only place that allocates <c>Sequence</c> numbers
/// (0, 1, 2... in the order events complete), sanitizes payloads (<see cref="TracePayload"/>), prices every
/// <c>LlmCall</c> and keeps the run totals, so the run cost is by construction the sum of its LlmCall costs.
/// Events are kept in memory while the run streams and persisted with the run when it closes.
/// </summary>
public sealed class TraceRecorder(Guid runId, IReadOnlyDictionary<string, ModelPricing> pricing, TimeProvider clock)
{
    public const string UnknownModel = "unknown";
    private const int NameColumnLength = 200; // trace_events.Name

    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = [];
    private int _taken;

    public Guid RunId { get; } = runId;

    public long TokensIn { get; private set; }
    public long TokensOut { get; private set; }

    /// <summary>Sum of the cost of every recorded <c>LlmCall</c>.</summary>
    public decimal CostUsd { get; private set; }

    /// <summary>Every event recorded so far, in sequence order.</summary>
    public IReadOnlyList<TraceEvent> Events
    {
        get
        {
            lock (_gate)
                return [.. _events];
        }
    }

    /// <summary>The events recorded since the previous call, so the caller can stream each one exactly once.</summary>
    public IReadOnlyList<TraceEvent> TakeNew()
    {
        lock (_gate)
        {
            var fresh = _events.GetRange(_taken, _events.Count - _taken);
            _taken = _events.Count;
            return fresh;
        }
    }

    public long StartTimer() => clock.GetTimestamp();

    public int Elapsed(long startTimestamp) => (int)clock.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>Records one model call. <paramref name="tokensIn"/>/<paramref name="tokensOut"/> are null when the provider reported no usage.</summary>
    public TraceEvent RecordLlmCall(string? model, object? input, object? output, long? tokensIn, long? tokensOut, int durationMs)
    {
        model = string.IsNullOrWhiteSpace(model) ? UnknownModel : model;
        var cost = Math.Round(
            CostCalculator.Compute(pricing.GetValueOrDefault(model), tokensIn ?? 0, tokensOut ?? 0), 8, MidpointRounding.AwayFromZero);

        lock (_gate)
        {
            TokensIn += tokensIn ?? 0;
            TokensOut += tokensOut ?? 0;
            CostUsd += cost;
        }

        return Record(TraceEventType.LlmCall, model, input, output, tokensIn, tokensOut, cost, durationMs);
    }

    public TraceEvent Record(
        TraceEventType type, string name, object? input, object? output,
        long? tokensIn = null, long? tokensOut = null, decimal? costUsd = null, int durationMs = 0)
    {
        // Sanitize outside the lock: it is the expensive part.
        var traceEvent = new TraceEvent
        {
            RunId = RunId,
            Type = type,
            Name = name.Length <= NameColumnLength ? name : name[..NameColumnLength],
            Input = TracePayload.Sanitize(input),
            Output = TracePayload.Sanitize(output),
            TokensIn = ToInt(tokensIn),
            TokensOut = ToInt(tokensOut),
            CostUsd = costUsd,
            DurationMs = Math.Max(0, durationMs),
            CreatedAt = clock.GetUtcNow(),
        };

        lock (_gate)
        {
            traceEvent.Sequence = _events.Count;
            _events.Add(traceEvent);
        }

        return traceEvent;
    }

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    /// <summary>What the trace stores about an exception: the type and the HTTP status, never the message (it may echo request data).</summary>
    public static Dictionary<string, object?> DescribeFailure(Exception ex) => ex switch
    {
        System.ClientModel.ClientResultException e => new() { ["error"] = e.GetType().Name, ["status"] = e.Status },
        HttpRequestException e => new() { ["error"] = e.GetType().Name, ["status"] = (int?)e.StatusCode },
        OperationCanceledException => new() { ["error"] = "cancelled" },
        _ => new() { ["error"] = ex.GetType().Name },
    };
}
