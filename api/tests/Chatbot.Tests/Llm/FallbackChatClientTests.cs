using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatClient = OpenAI.Chat.ChatClient;

namespace Chatbot.Tests.Llm;

public class FallbackChatClientTests
{
    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    private static FallbackChatClient Build(FakeChatClient primary, FakeChatClient? fallback) =>
        new(new LlmEndpoint(primary, "gemini", "gemini-model"),
            fallback is null ? null : new LlmEndpoint(fallback, "groq", "groq-model"));

    private static FallbackChatClient Build(FakeChatClient primary, FakeChatClient? fallback, TimeSpan firstUpdateTimeout) =>
        new(new LlmEndpoint(primary, "gemini", "gemini-model"),
            fallback is null ? null : new LlmEndpoint(fallback, "groq", "groq-model"),
            firstUpdateTimeout: firstUpdateTimeout);

    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(150);

    // The OpenAI SDK is the real source of errors in production, so use its exception instead of inventing one.
    private static async Task<Exception> SdkError(HttpStatusCode status, bool stream = false)
    {
        var http = new HttpClient(new StubHandler(status));
        var chat = new ChatClient("m", new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1/"),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
        }).AsIChatClient();

        return await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (stream)
            {
                await foreach (var _ in chat.GetStreamingResponseAsync(Messages))
                {
                }
            }
            else
            {
                await chat.GetResponseAsync(Messages);
            }
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Classifies_the_exceptions_the_openai_sdk_throws(HttpStatusCode status, bool retryable)
    {
        Assert.Equal(retryable, FallbackChatClient.IsRetryable(await SdkError(status)));
        Assert.Equal(retryable, FallbackChatClient.IsRetryable(await SdkError(status, stream: true)));
    }

    [Fact]
    public void Network_failures_and_cancellation_are_not_retryable()
    {
        Assert.False(FallbackChatClient.IsRetryable(new HttpRequestException("connection refused")));
        Assert.False(FallbackChatClient.IsRetryable(new OperationCanceledException()));
        Assert.True(FallbackChatClient.IsRetryable(new HttpRequestException("x", null, HttpStatusCode.BadGateway)));
    }

    [Fact]
    public async Task Primary_success_is_tagged_and_fallback_is_not_called()
    {
        var primary = new FakeChatClient { Response = _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from primary"))) };
        var fallback = new FakeChatClient();

        var response = await Build(primary, fallback).GetResponseAsync(Messages);

        Assert.Equal("from primary", response.Text);
        Assert.Equal("gemini", response.AdditionalProperties![FallbackChatClient.ProviderKey]);
        Assert.Equal("gemini-model", response.AdditionalProperties[FallbackChatClient.ModelKey]);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Rate_limit_then_success_uses_the_fallback()
    {
        var error = await SdkError(HttpStatusCode.TooManyRequests);
        var primary = new FakeChatClient { Response = _ => throw error };
        var fallback = new FakeChatClient { Response = _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from fallback"))) };

        var response = await Build(primary, fallback).GetResponseAsync(Messages);

        Assert.Equal("from fallback", response.Text);
        Assert.Equal("groq", response.AdditionalProperties![FallbackChatClient.ProviderKey]);
        Assert.Equal("groq-model", response.AdditionalProperties[FallbackChatClient.ModelKey]);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Http_500_falls_back()
    {
        var primary = new FakeChatClient { Response = _ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError) };
        var fallback = new FakeChatClient { Response = _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };

        var response = await Build(primary, fallback).GetResponseAsync(Messages);

        Assert.Equal("groq", response.AdditionalProperties![FallbackChatClient.ProviderKey]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Non_retryable_status_does_not_fall_back(HttpStatusCode status)
    {
        var error = await SdkError(status);
        var primary = new FakeChatClient { Response = _ => throw error };
        var fallback = new FakeChatClient();

        var thrown = await Assert.ThrowsAsync<ClientResultException>(() => Build(primary, fallback).GetResponseAsync(Messages));

        Assert.Same(error, thrown);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Without_fallback_the_error_propagates()
    {
        var error = await SdkError(HttpStatusCode.TooManyRequests);
        var primary = new FakeChatClient { Response = _ => throw error };

        await Assert.ThrowsAsync<ClientResultException>(() => Build(primary, null).GetResponseAsync(Messages));
    }

    [Fact]
    public async Task Fallback_is_tried_only_once()
    {
        var primary = new FakeChatClient { Response = _ => throw new HttpRequestException("a", null, HttpStatusCode.TooManyRequests) };
        var fallback = new FakeChatClient { Response = _ => throw new HttpRequestException("b", null, HttpStatusCode.BadGateway) };

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Build(primary, fallback).GetResponseAsync(Messages));

        Assert.Equal("b", thrown.Message);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Each_provider_gets_its_own_model_id_without_mutating_the_callers_options()
    {
        var primary = new FakeChatClient { Response = _ => throw new HttpRequestException("a", null, HttpStatusCode.TooManyRequests) };
        var fallback = new FakeChatClient { Response = _ => Task.FromResult(new ChatResponse()) };
        var options = new ChatOptions { ModelId = "caller-model", MaxOutputTokens = 300 };

        await Build(primary, fallback).GetResponseAsync(Messages, options);

        Assert.Equal("gemini-model", primary.LastOptions!.ModelId);
        Assert.Equal("groq-model", fallback.LastOptions!.ModelId);
        Assert.Equal(300, fallback.LastOptions.MaxOutputTokens);
        Assert.Equal("caller-model", options.ModelId);
    }

    [Fact]
    public async Task Streaming_primary_success_tags_every_update()
    {
        var primary = new FakeChatClient { Stream = _ => Updates("Hel", "lo") };
        var fallback = new FakeChatClient();

        var updates = await Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages));

        Assert.Equal("Hello", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("gemini", u.AdditionalProperties![FallbackChatClient.ProviderKey]));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_failure_before_the_first_token_falls_back()
    {
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(new HttpRequestException("500", null, HttpStatusCode.InternalServerError)) };
        var fallback = new FakeChatClient { Stream = _ => Updates("from ", "fallback") };

        var updates = await Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages));

        Assert.Equal("from fallback", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u =>
        {
            Assert.Equal("groq", u.AdditionalProperties![FallbackChatClient.ProviderKey]);
            Assert.Equal("groq-model", u.AdditionalProperties[FallbackChatClient.ModelKey]);
        });
    }

    [Fact]
    public async Task Streaming_sdk_429_before_the_first_token_falls_back()
    {
        var error = await SdkError(HttpStatusCode.TooManyRequests, stream: true);
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(error) };
        var fallback = new FakeChatClient { Stream = _ => Updates("ok") };

        var updates = await Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages));

        Assert.Equal("ok", updates.Single().Text);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_failure_after_the_first_token_propagates_without_fallback()
    {
        var primary = new FakeChatClient { Stream = _ => UpdatesThenFail(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable), "partial") };
        var fallback = new FakeChatClient { Stream = _ => Updates("should not be used") };
        var received = new List<ChatResponseUpdate>();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in Build(primary, fallback).GetStreamingResponseAsync(Messages))
            {
                received.Add(update);
            }
        });

        Assert.Equal("partial", received.Single().Text);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Streaming_non_retryable_status_does_not_fall_back(HttpStatusCode status)
    {
        var error = await SdkError(status, stream: true);
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(error) };
        var fallback = new FakeChatClient();

        await Assert.ThrowsAsync<ClientResultException>(() => Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages)));

        Assert.Equal(0, fallback.Calls);
    }

    // The exception Gemini's "function_call_filter: MALFORMED_FUNCTION_CALL" finish reason causes in the real OpenAI SDK.
    private static async Task<Exception> UnknownFinishReasonError(bool stream)
    {
        var chat = RealClient(stream ? MalformedStream : MalformedCompletion, stream ? "text/event-stream" : "application/json");

        return await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (stream)
            {
                await foreach (var _ in chat.GetStreamingResponseAsync(Messages))
                {
                }
            }
            else
            {
                await chat.GetResponseAsync(Messages);
            }
        });
    }

    private const string MalformedReason = "function_call_filter: MALFORMED_FUNCTION_CALL";
    private const string MalformedStream = "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":\"" + MalformedReason + "\"}]}\n\ndata: [DONE]\n\n";
    private const string MalformedCompletion = "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"" + MalformedReason + "\",\"message\":{\"role\":\"assistant\",\"content\":null}}]}";
    private const string GoodStream = "data: {\"id\":\"y\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";

    private static IChatClient RealClient(string body, string contentType) => new ChatClient("m", new ApiKeyCredential("k"), new OpenAIClientOptions
    {
        Endpoint = new Uri("http://localhost/v1/"),
        Transport = new HttpClientPipelineTransport(new HttpClient(new StubHandler(HttpStatusCode.OK, body, contentType))),
        RetryPolicy = new ClientRetryPolicy(0),
    }).AsIChatClient();

    private static ChatResponse Answer(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recognizes_the_unknown_finish_reason_the_openai_sdk_throws(bool stream)
    {
        var error = await UnknownFinishReasonError(stream);

        Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.True(FallbackChatClient.IsUnknownFinishReason(error));
        Assert.False(FallbackChatClient.IsUnknownFinishReason(new ArgumentOutOfRangeException("count")));
        Assert.False(FallbackChatClient.IsUnknownFinishReason(new InvalidOperationException("Unknown ChatFinishReason value")));
    }

    [Fact]
    public async Task Unknown_finish_reason_is_retried_once_on_the_same_provider()
    {
        var error = await UnknownFinishReasonError(stream: false);
        int call = 0;
        var primary = new FakeChatClient { Response = _ => ++call == 1 ? throw error : Task.FromResult(Answer("second try")) };
        var fallback = new FakeChatClient();

        var response = await Build(primary, fallback).GetResponseAsync(Messages);

        Assert.Equal("second try", response.Text);
        Assert.Equal("gemini", response.AdditionalProperties![FallbackChatClient.ProviderKey]);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Unknown_finish_reason_twice_uses_the_fallback()
    {
        var error = await UnknownFinishReasonError(stream: false);
        var primary = new FakeChatClient { Response = _ => throw error };
        var fallback = new FakeChatClient { Response = _ => Task.FromResult(Answer("from fallback")) };

        var response = await Build(primary, fallback).GetResponseAsync(Messages);

        Assert.Equal("from fallback", response.Text);
        Assert.Equal("groq", response.AdditionalProperties![FallbackChatClient.ProviderKey]);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Unknown_finish_reason_everywhere_propagates_after_one_retry_per_provider()
    {
        var error = await UnknownFinishReasonError(stream: false);
        var primary = new FakeChatClient { Response = _ => throw error };
        var fallback = new FakeChatClient { Response = _ => throw error };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build(primary, fallback).GetResponseAsync(Messages));

        Assert.Equal(2, primary.Calls);
        Assert.Equal(2, fallback.Calls);
    }

    [Fact]
    public async Task Unknown_finish_reason_without_fallback_is_retried_once_then_propagates()
    {
        var error = await UnknownFinishReasonError(stream: false);
        var primary = new FakeChatClient { Response = _ => throw error };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build(primary, null).GetResponseAsync(Messages));

        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task Other_argument_errors_are_not_retried()
    {
        var primary = new FakeChatClient { Response = _ => throw new ArgumentOutOfRangeException("count") };
        var fallback = new FakeChatClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Build(primary, fallback).GetResponseAsync(Messages));

        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_unknown_finish_reason_before_the_first_update_is_retried_on_the_same_provider()
    {
        var error = await UnknownFinishReasonError(stream: true);
        int call = 0;
        var primary = new FakeChatClient { Stream = _ => ++call == 1 ? FailsImmediately(error) : Updates("second ", "try") };
        var fallback = new FakeChatClient();

        var updates = await Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages));

        Assert.Equal("second try", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("gemini", u.AdditionalProperties![FallbackChatClient.ProviderKey]));
        Assert.Equal(2, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_unknown_finish_reason_twice_uses_the_fallback()
    {
        var error = await UnknownFinishReasonError(stream: true);
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(error) };
        var fallback = new FakeChatClient { Stream = _ => Updates("from fallback") };

        var updates = await Collect(Build(primary, fallback).GetStreamingResponseAsync(Messages));

        Assert.Equal("from fallback", updates.Single().Text);
        Assert.Equal("groq", updates.Single().AdditionalProperties![FallbackChatClient.ProviderKey]);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_unknown_finish_reason_after_the_first_update_propagates()
    {
        var error = await UnknownFinishReasonError(stream: true);
        var primary = new FakeChatClient { Stream = _ => UpdatesThenFail(error, "partial") };
        var fallback = new FakeChatClient { Stream = _ => Updates("should not be used") };
        var received = new List<ChatResponseUpdate>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var update in Build(primary, fallback).GetStreamingResponseAsync(Messages))
            {
                received.Add(update);
            }
        });

        Assert.Equal("partial", received.Single().Text);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_through_real_sdk_clients_falls_back_after_two_malformed_responses()
    {
        var client = new FallbackChatClient(
            new LlmEndpoint(RealClient(MalformedStream, "text/event-stream"), "gemini", "g"),
            new LlmEndpoint(RealClient(GoodStream, "text/event-stream"), "groq", "q"));

        var updates = await Collect(client.GetStreamingResponseAsync(Messages));

        Assert.Equal("ok", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("groq", u.AdditionalProperties![FallbackChatClient.ProviderKey]));
    }

    [Fact]
    public async Task Streaming_without_fallback_propagates()
    {
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests)) };

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(Build(primary, null).GetStreamingResponseAsync(Messages)));
    }

    [Fact]
    public async Task Streaming_primary_that_never_produces_a_first_update_is_cancelled_and_the_fallback_answers()
    {
        CancellationToken primaryToken = default;
        var primary = new FakeChatClient { CancellableStream = ct => { primaryToken = ct; return Hangs(ct); } };
        var fallback = new FakeChatClient { Stream = _ => Updates("from ", "fallback") };

        var updates = await Collect(Build(primary, fallback, ShortLimit).GetStreamingResponseAsync(Messages));

        Assert.Equal("from fallback", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("groq", u.AdditionalProperties![FallbackChatClient.ProviderKey]));
        Assert.True(primaryToken.IsCancellationRequested);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_primary_that_is_slow_after_the_first_update_is_not_cut_off()
    {
        var primary = new FakeChatClient { CancellableStream = ct => SlowAfterFirst(TimeSpan.FromMilliseconds(500), ct, "Hel", "lo") };
        var fallback = new FakeChatClient { Stream = _ => Updates("should not be used") };

        var updates = await Collect(Build(primary, fallback, ShortLimit).GetStreamingResponseAsync(Messages));

        Assert.Equal("Hello", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("gemini", u.AdditionalProperties![FallbackChatClient.ProviderKey]));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_failure_after_a_first_update_that_beat_the_limit_still_propagates_without_fallback()
    {
        var primary = new FakeChatClient { Stream = _ => UpdatesThenFail(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable), "partial") };
        var fallback = new FakeChatClient { Stream = _ => Updates("should not be used") };

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(Build(primary, fallback, ShortLimit).GetStreamingResponseAsync(Messages)));

        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_without_a_fallback_the_primary_is_not_cut_off()
    {
        var primary = new FakeChatClient { CancellableStream = ct => SlowAfterFirst(TimeSpan.Zero, ct, "late", delayBeforeFirst: TimeSpan.FromMilliseconds(400)) };

        var updates = await Collect(Build(primary, null, ShortLimit).GetStreamingResponseAsync(Messages));

        Assert.Equal("late", updates.Single().Text);
    }

    [Fact]
    public async Task Streaming_fallback_has_no_time_limit()
    {
        var primary = new FakeChatClient { Stream = _ => FailsImmediately(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests)) };
        var fallback = new FakeChatClient { CancellableStream = ct => SlowAfterFirst(TimeSpan.Zero, ct, "slow but ok", delayBeforeFirst: TimeSpan.FromMilliseconds(400)) };

        var updates = await Collect(Build(primary, fallback, ShortLimit).GetStreamingResponseAsync(Messages));

        Assert.Equal("slow but ok", updates.Single().Text);
    }

    [Fact]
    public async Task Streaming_slow_primary_and_failing_fallback_propagates_the_fallback_error()
    {
        var primary = new FakeChatClient { CancellableStream = Hangs };
        var fallback = new FakeChatClient { Stream = _ => FailsImmediately(new HttpRequestException("b", null, HttpStatusCode.BadGateway)) };

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(Build(primary, fallback, ShortLimit).GetStreamingResponseAsync(Messages)));

        Assert.Equal("b", thrown.Message);
    }

    [Fact]
    public async Task Streaming_caller_cancellation_is_not_mistaken_for_the_time_limit()
    {
        var primary = new FakeChatClient { CancellableStream = Hangs };
        var fallback = new FakeChatClient { Stream = _ => Updates("should not be used") };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Collect(Build(primary, fallback, TimeSpan.FromSeconds(30)).GetStreamingResponseAsync(Messages, cancellationToken: cts.Token)));

        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Streaming_a_zero_timeout_means_no_limit()
    {
        var primary = new FakeChatClient { CancellableStream = ct => SlowAfterFirst(TimeSpan.Zero, ct, "ok", delayBeforeFirst: TimeSpan.FromMilliseconds(200)) };
        var fallback = new FakeChatClient();

        var updates = await Collect(Build(primary, fallback, TimeSpan.Zero).GetStreamingResponseAsync(Messages));

        Assert.Equal("ok", updates.Single().Text);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void FirstUpdateTimeoutException_is_a_reason_to_fall_back_and_names_the_provider()
    {
        var error = new FirstUpdateTimeoutException(new LlmEndpoint(new FakeChatClient(), "gemini", "gemini-model"), TimeSpan.FromSeconds(12), new OperationCanceledException());

        Assert.Contains("gemini", error.Message);
        Assert.Contains("12 s", error.Message);
        Assert.IsAssignableFrom<TimeoutException>(error);
    }

    [Fact]
    public void The_default_first_token_timeout_is_12_seconds()
    {
        Assert.Equal(12, new LlmOptions().FirstTokenTimeoutSeconds);
    }

    [Fact]
    public void GetService_returns_itself_or_asks_the_primary_and_Dispose_disposes_both()
    {
        var primary = new FakeChatClient();
        var fallback = new FakeChatClient();
        var client = Build(primary, fallback);

        Assert.Same(client, client.GetService(typeof(FallbackChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(primary, client.GetService(typeof(FakeChatClient), serviceKey: "asked-primary"));
        Assert.Null(client.GetService(typeof(string)));

        client.Dispose();

        Assert.True(primary.Disposed);
        Assert.True(fallback.Disposed);
    }

    private static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in updates)
        {
            list.Add(update);
        }

        return list;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(params string[] texts)
    {
        foreach (var text in texts)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> UpdatesThenFail(Exception error, params string[] texts)
    {
        await foreach (var update in Updates(texts))
        {
            yield return update;
        }

        throw error;
    }

    // A stream that produces nothing until it is cancelled (what a stalled HTTP request does with the SDK's token).
    private static async IAsyncEnumerable<ChatResponseUpdate> Hangs([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> SlowAfterFirst(
        TimeSpan gap, [EnumeratorCancellation] CancellationToken ct, string first, string? second = null, TimeSpan? delayBeforeFirst = null)
    {
        await Task.Delay(delayBeforeFirst ?? TimeSpan.Zero, ct);
        yield return new ChatResponseUpdate(ChatRole.Assistant, first);
        if (second is not null)
        {
            await Task.Delay(gap, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, second);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailsImmediately(Exception error)
    {
        await Task.Yield();
        throw error;
#pragma warning disable CS0162 // makes this an iterator
        yield break;
#pragma warning restore CS0162
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Func<IEnumerable<ChatMessage>, Task<ChatResponse>> Response { get; set; } = _ => throw new InvalidOperationException("unexpected call");
        public Func<IEnumerable<ChatMessage>, IAsyncEnumerable<ChatResponseUpdate>> Stream { get; set; } = _ => throw new InvalidOperationException("unexpected call");

        /// <summary>When set, used instead of <see cref="Stream"/> and given the token the client was called with.</summary>
        public Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? CancellableStream { get; set; }
        public int Calls { get; private set; }
        public ChatOptions? LastOptions { get; private set; }
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return Response(messages);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return CancellableStream is { } cancellable ? cancellable(cancellationToken) : Stream(messages);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is not null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() => Disposed = true;
    }

    private sealed class StubHandler(HttpStatusCode status, string? body = null, string contentType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? """{"error":{"message":"stub"}}""", System.Text.Encoding.UTF8, contentType),
            });
    }
}
