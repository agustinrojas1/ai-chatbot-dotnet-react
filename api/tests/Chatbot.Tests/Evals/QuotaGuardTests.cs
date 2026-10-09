using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Chatbot.Evals.Running;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatClient = OpenAI.Chat.ChatClient;

namespace Chatbot.Tests.Evals;

public class QuotaGuardTests
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    /// <summary>A clock that only moves when the guard "waits": the delay it is given adds to it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Waits { get; } = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            Waits.Add(wait);
            _now += wait;
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers each call with the next behaviour: an exception thrown before the first update, or an answer.</summary>
    private sealed class ScriptedProvider(params Func<IAsyncEnumerable<ChatResponseUpdate>>[] calls) : IChatClient
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            calls[Calls++]();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public static Func<IAsyncEnumerable<ChatResponseUpdate>> Fail(Exception ex) => () => Throw(ex);

        public static Func<IAsyncEnumerable<ChatResponseUpdate>> Answer(string text) => () => Updates(text);

        public static Func<IAsyncEnumerable<ChatResponseUpdate>> FailAfterFirstUpdate(Exception ex) => () => PartThenThrow(ex);

        private static async IAsyncEnumerable<ChatResponseUpdate> Throw(Exception ex, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw ex;
#pragma warning disable CS0162 // makes this an iterator
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Updates(string text)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> PartThenThrow(Exception ex)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "part");
            throw ex;
        }
    }

    private static HttpRequestException TooManyRequests() => new("quota", null, HttpStatusCode.TooManyRequests);

    private static (QuotaGuardChatClient Guard, ManualClock Clock, QuotaMonitor Monitor, List<string> Notices) Build(
        ScriptedProvider provider, TimeSpan? minInterval = null, int maxRetries = ModelCallPolicy.DefaultMaxRetries)
    {
        var clock = new ManualClock();
        var notices = new List<string>();
        var monitor = new QuotaMonitor();
        var policy = new ModelCallPolicy(minInterval ?? TimeSpan.Zero, maxRetries, notices.Add, clock.Delay, clock);
        return (new QuotaGuardChatClient(provider, policy, monitor), clock, monitor, notices);
    }

    private static async Task<string> ReadAsync(IChatClient client)
    {
        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync(Messages))
            text += update.Text;
        return text;
    }

    /// <summary>The exception the OpenAI SDK throws for a 429, built by the SDK itself, with the headers the provider sent.</summary>
    private static async Task<Exception> SdkQuotaError(string? retryAfter)
    {
        var handler = new StubHandler(retryAfter);
        var chat = new ChatClient("m", new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0),
        }).AsIChatClient();

        return await Assert.ThrowsAnyAsync<Exception>(async () => await chat.GetResponseAsync(Messages));
    }

    private sealed class StubHandler(string? retryAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"message":"quota"}}""", System.Text.Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void Backoff_doubles_from_15_seconds_up_to_a_minute()
    {
        Assert.Equal([15, 30, 60, 60, 60], Enumerable.Range(0, 5).Select(i => ModelCallPolicy.Backoff(i).TotalSeconds));
    }

    [Fact]
    public async Task A_429_is_waited_out_with_exponential_backoff_and_the_call_then_succeeds()
    {
        var provider = new ScriptedProvider(
            ScriptedProvider.Fail(TooManyRequests()), ScriptedProvider.Fail(TooManyRequests()), ScriptedProvider.Answer("hello"));
        var (guard, clock, monitor, notices) = Build(provider);

        var text = await ReadAsync(guard);

        Assert.Equal("hello", text);
        Assert.Equal([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)], clock.Waits);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(0, monitor.Exhausted);
        Assert.Equal(2, notices.Count);
        Assert.Contains("Waiting 15 s, retry 1/5", notices[0]);
        Assert.Contains("Waiting 30 s, retry 2/5", notices[1]);
    }

    [Fact]
    public async Task After_five_retries_the_failure_goes_up_and_is_recorded_as_exhausted()
    {
        var provider = new ScriptedProvider([.. Enumerable.Range(0, 6).Select(_ => ScriptedProvider.Fail(TooManyRequests()))]);
        var (guard, clock, monitor, notices) = Build(provider);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(guard));

        Assert.Equal(HttpStatusCode.TooManyRequests, thrown.StatusCode);
        Assert.Equal(6, provider.Calls); // the call and 5 retries
        Assert.Equal([15, 30, 60, 60, 60], clock.Waits.Select(w => w.TotalSeconds));
        Assert.Equal(1, monitor.Exhausted);
        Assert.Equal(5, notices.Count);
    }

    [Fact]
    public async Task Retry_After_from_the_provider_is_used_instead_of_the_backoff()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Fail(await SdkQuotaError("7")), ScriptedProvider.Answer("ok"));
        var (guard, clock, _, notices) = Build(provider);

        await ReadAsync(guard);

        Assert.Equal([TimeSpan.FromSeconds(7)], clock.Waits);
        Assert.Contains("Waiting 7 s", notices.Single());
    }

    [Fact]
    public async Task Retry_After_can_be_an_http_date()
    {
        var (_, clock, _, _) = Build(new ScriptedProvider());
        var error = await SdkQuotaError(clock.GetUtcNow().AddSeconds(20).ToString("R"));

        var wait = QuotaGuardChatClient.RetryAfter(error, clock.GetUtcNow());

        Assert.Equal(20, wait!.Value.TotalSeconds, 1);
    }

    [Fact]
    public async Task A_429_without_retry_after_or_with_junk_uses_the_backoff()
    {
        var plain = await SdkQuotaError(null);
        var junk = await SdkQuotaError("soon");
        var provider = new ScriptedProvider(ScriptedProvider.Fail(plain), ScriptedProvider.Fail(junk), ScriptedProvider.Answer("ok"));
        var (guard, clock, _, _) = Build(provider);

        await ReadAsync(guard);

        Assert.Equal([15, 30], clock.Waits.Select(w => w.TotalSeconds));
    }

    [Fact]
    public async Task A_retry_after_of_hours_means_the_quota_is_spent_so_it_does_not_wait()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Fail(await SdkQuotaError("3600")));
        var (guard, clock, monitor, _) = Build(provider);

        await Assert.ThrowsAsync<ClientResultException>(() => ReadAsync(guard));

        Assert.Empty(clock.Waits);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, monitor.Exhausted);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Errors_other_than_429_are_not_retried_and_are_not_quota(HttpStatusCode status)
    {
        var provider = new ScriptedProvider(ScriptedProvider.Fail(new HttpRequestException("x", null, status)));
        var (guard, clock, monitor, _) = Build(provider);

        await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(guard));

        Assert.Equal(1, provider.Calls);
        Assert.Empty(clock.Waits);
        Assert.Equal(0, monitor.Exhausted);
    }

    [Fact]
    public async Task A_429_in_the_middle_of_an_answer_cannot_be_repeated_and_counts_as_quota()
    {
        var provider = new ScriptedProvider(ScriptedProvider.FailAfterFirstUpdate(TooManyRequests()));
        var (guard, clock, monitor, _) = Build(provider);
        var received = new List<string>();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in guard.GetStreamingResponseAsync(Messages))
                received.Add(update.Text);
        });

        Assert.Equal(["part"], received);
        Assert.Equal(1, provider.Calls);
        Assert.Empty(clock.Waits);
        Assert.Equal(1, monitor.Exhausted);
    }

    [Fact]
    public async Task Calls_are_spaced_by_the_minimum_interval()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Answer("a"), ScriptedProvider.Answer("b"), ScriptedProvider.Answer("c"));
        var (guard, clock, _, _) = Build(provider, minInterval: TimeSpan.FromSeconds(2));

        await ReadAsync(guard);
        await ReadAsync(guard);
        await ReadAsync(guard);

        // The first call goes at once; the others wait until 2 s after the previous one started.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], clock.Waits);
    }

    [Fact]
    public async Task Time_spent_elsewhere_counts_towards_the_interval()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Answer("a"), ScriptedProvider.Answer("b"));
        var (guard, clock, _, _) = Build(provider, minInterval: TimeSpan.FromSeconds(2));

        await ReadAsync(guard);
        await clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None); // a slow answer and the tools
        clock.Waits.Clear();
        await ReadAsync(guard);

        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task A_retry_also_waits_for_its_turn()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Fail(TooManyRequests()), ScriptedProvider.Answer("ok"));
        var (guard, clock, _, _) = Build(provider, minInterval: TimeSpan.FromSeconds(2));

        await ReadAsync(guard);

        // Backoff of 15 s already passed the interval, so only the backoff is waited.
        Assert.Equal([TimeSpan.FromSeconds(15)], clock.Waits);
    }

    [Fact]
    public async Task Cancelling_while_waiting_stops_the_retries()
    {
        var provider = new ScriptedProvider(ScriptedProvider.Fail(TooManyRequests()), ScriptedProvider.Answer("never"));
        var policy = new ModelCallPolicy(TimeSpan.Zero, Delay: (_, ct) => Task.FromCanceled(new CancellationToken(true)));
        var guard = new QuotaGuardChatClient(provider, policy, new QuotaMonitor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(guard));

        Assert.Equal(1, provider.Calls);
    }
}
