using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Chatbot.Evals.Running;

/// <summary>
/// How the harness treats the provider's rate limits. Free tiers answer 429 long before a 36-case run is over, and
/// that says nothing about the agent, so the harness spaces its calls out and waits when it is told to.
/// <paramref name="Delay"/> and <paramref name="Clock"/> exist so the tests do not really wait.
/// </summary>
/// <param name="MinInterval">Minimum time between the starts of two model calls.</param>
/// <param name="MaxRetries">How many times a call that got a 429 is tried again.</param>
/// <param name="Notice">One line for the person watching the console, every time the harness waits.</param>
internal sealed record ModelCallPolicy(
    TimeSpan MinInterval,
    int MaxRetries = ModelCallPolicy.DefaultMaxRetries,
    Action<string>? Notice = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    TimeProvider? Clock = null)
{
    public const int DefaultMaxRetries = 5;
    public const int DefaultMinIntervalMs = 2000;

    /// <summary>Wait before the first retry; it doubles on every retry up to <see cref="MaxBackoff"/>: 15 s, 30 s, 60 s, 60 s, 60 s.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>A <c>Retry-After</c> longer than this means the quota is spent for the day or the hour: waiting would only stall the run.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Wait before retry number <paramref name="retry"/> (0 for the first), when the provider did not say how long.</summary>
    public static TimeSpan Backoff(int retry) =>
        TimeSpan.FromSeconds(Math.Min(FirstBackoff.TotalSeconds * Math.Pow(2, retry), MaxBackoff.TotalSeconds));
}

/// <summary>
/// Counts the model calls that failed with a quota error after every retry. The runner reads it to tell "the
/// provider refused" (the case was not run) from "the agent misbehaved" (the case failed).
/// </summary>
internal sealed class QuotaMonitor
{
    private int _exhausted;

    public int Exhausted => Volatile.Read(ref _exhausted);

    public void RecordExhausted() => Interlocked.Increment(ref _exhausted);
}

/// <summary>
/// Sits right above the provider client, below the agent pipeline, so <c>AgentService</c> and its tracing are the
/// ones the product uses. Every model call (a) waits until <see cref="ModelCallPolicy.MinInterval"/> has passed since
/// the previous call started and (b) when the provider answers 429 before the first token, waits (the provider's
/// <c>Retry-After</c>, else 15 s, 30 s, 60 s...) and tries again, up to <see cref="ModelCallPolicy.MaxRetries"/> times.
/// If it still fails, the exception goes up unchanged and the <see cref="QuotaMonitor"/> notes it.
/// </summary>
internal sealed class QuotaGuardChatClient : DelegatingChatClient
{
    private readonly ModelCallPolicy _policy;
    private readonly QuotaMonitor _monitor;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private DateTimeOffset _nextCallAt = DateTimeOffset.MinValue;

    public QuotaGuardChatClient(IChatClient inner, ModelCallPolicy policy, QuotaMonitor monitor)
        : base(inner)
    {
        _policy = policy;
        _monitor = monitor;
        _clock = policy.Clock ?? TimeProvider.System;
        _delay = policy.Delay ?? ((wait, ct) => Task.Delay(wait, _clock, ct));
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Only a call that has produced nothing yet can be repeated, so the first update is pulled inside the retry loop.
        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        bool hasUpdate;
        for (var retry = 0; ; retry++)
        {
            await WaitForTurnAsync(cancellationToken);
            try
            {
                updates = base.GetStreamingResponseAsync(history, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                hasUpdate = await updates.MoveNextAsync();
                break;
            }
            catch (Exception ex) when (IsQuota(ex))
            {
                if (updates is not null)
                    await updates.DisposeAsync();
                updates = null;

                var wait = WaitBeforeRetry(ex, retry);
                if (wait is not { } pause)
                {
                    _monitor.RecordExhausted();
                    throw;
                }

                _policy.Notice?.Invoke($"Provider quota (429). Waiting {pause.TotalSeconds:0.#} s, retry {retry + 1}/{_policy.MaxRetries}...");
                await _delay(pause, cancellationToken);
            }
        }

        await using (updates)
        {
            while (hasUpdate)
            {
                yield return updates.Current;
                try
                {
                    hasUpdate = await updates.MoveNextAsync();
                }
                catch (Exception ex) when (IsQuota(ex))
                {
                    // Part of the answer is already out: it cannot be repeated. The turn is reported as not run.
                    _monitor.RecordExhausted();
                    throw;
                }
            }
        }
    }

    /// <summary>How long to wait before retry number <paramref name="retry"/>; null when the call must not be retried.</summary>
    private TimeSpan? WaitBeforeRetry(Exception ex, int retry)
    {
        if (retry >= _policy.MaxRetries)
            return null;

        if (RetryAfter(ex, _clock.GetUtcNow()) is not { } suggested)
            return ModelCallPolicy.Backoff(retry);

        return suggested > ModelCallPolicy.MaxRetryAfter ? null : suggested;
    }

    /// <summary>Blocks until the minimum interval since the previous call has passed, and reserves the next slot.</summary>
    private async Task WaitForTurnAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var start = now > _nextCallAt ? now : _nextCallAt;
            _nextCallAt = start + _policy.MinInterval;
            wait = start - now;
        }

        if (wait > TimeSpan.Zero)
            await _delay(wait, ct);
    }

    /// <summary>A 429 from the OpenAI SDK (<c>ClientResultException</c>) or from plain HTTP, possibly wrapped.</summary>
    public static bool IsQuota(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var status = e switch
            {
                ClientResultException client => client.Status,
                HttpRequestException http => (int?)http.StatusCode,
                _ => null,
            };
            if (status == (int)HttpStatusCode.TooManyRequests)
                return true;
        }

        return false;
    }

    /// <summary>The <c>Retry-After</c> of the 429, as seconds or as an HTTP date; null when there is none or it cannot be read.</summary>
    public static TimeSpan? RetryAfter(Exception ex, DateTimeOffset now)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is not ClientResultException client || client.GetRawResponse() is not { } response
                || !response.Headers.TryGetValue("Retry-After", out var value) || value is null)
                continue;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date > now ? date - now : TimeSpan.Zero;
        }

        return null;
    }
}
