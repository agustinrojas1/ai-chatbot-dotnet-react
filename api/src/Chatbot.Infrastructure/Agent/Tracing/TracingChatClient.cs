using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace Chatbot.Infrastructure.Agent.Tracing;

/// <summary>
/// Records one <c>LlmCall</c> trace event per individual model call (SPEC 7.5). It sits below
/// <c>FunctionInvokingChatClient</c>, so a run with two tool rounds produces three events, not one. The event is
/// completed when the stream ends (usage arrives in the last update), or when it fails or is abandoned.
/// It records the model and provider that answered (<c>llm.model</c>/<c>llm.provider</c> set by
/// <see cref="FallbackChatClient"/>), tokens, duration, the last input messages and the output, all through
/// <see cref="TracePayload"/>.
/// </summary>
public sealed class TracingChatClient(IChatClient inner, TraceRecorder recorder) : DelegatingChatClient(inner)
{
    /// <summary>How many of the latest messages are stored as the input of the call. The leading system prompt (the business text, the same on every call) is left out.</summary>
    public const int InputMessages = 4;

    /// <summary>Per message and for the output text: keeps the structure of the payload when something is long.</summary>
    public const int MaxMessageChars = 1000;
    public const int MaxOutputChars = 2500;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var call = new Call(recorder, history);
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception ex)
        {
            call.Fail(ex);
            throw;
        }

        foreach (var message in response.Messages)
            call.Observe(message.Contents);
        call.ObserveProperties(response.AdditionalProperties, response.ModelId);
        if (response.Usage is { } usage)
            call.Usage(usage);
        call.Complete();
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var call = new Call(recorder, history);
        try
        {
            await using var updates = base.GetStreamingResponseAsync(history, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                // yield cannot sit inside a try with a catch: the failure is captured and rethrown outside.
                Exception? failure = null;
                bool hasUpdate = false;
                try
                {
                    hasUpdate = await updates.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (failure is not null)
                {
                    call.Fail(failure);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (!hasUpdate)
                    break;

                call.Observe(updates.Current);
                yield return updates.Current;
            }

            call.Complete();
        }
        finally
        {
            // The consumer stopped early (client gone, iteration limit): the call still shows up in the trace.
            call.Abandon();
        }
    }

    /// <summary>What one model call has shown so far; turns into exactly one <c>LlmCall</c> event.</summary>
    private sealed class Call(TraceRecorder recorder, IReadOnlyList<ChatMessage> input)
    {
        private readonly long _start = recorder.StartTimer();
        private readonly StringBuilder _text = new();
        private readonly List<string> _toolCalls = [];
        private string? _model, _provider, _finishReason;
        private long? _tokensIn, _tokensOut;
        private bool _recorded;

        public void Observe(ChatResponseUpdate update)
        {
            ObserveProperties(update.AdditionalProperties, update.ModelId);
            if (update.FinishReason is { } finish)
                _finishReason = finish.Value;

            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                    Usage(usage.Details);
            }

            Observe(update.Contents);
        }

        public void Observe(IList<AIContent> contents)
        {
            foreach (var content in contents)
            {
                switch (content)
                {
                    case TextContent text:
                        _text.Append(text.Text);
                        break;
                    case FunctionCallContent call:
                        _toolCalls.Add(call.Name);
                        break;
                }
            }
        }

        public void ObserveProperties(AdditionalPropertiesDictionary? properties, string? modelId)
        {
            if (properties?.GetValueOrDefault(FallbackChatClient.ProviderKey) is string provider)
                _provider = provider;
            _model = properties?.GetValueOrDefault(FallbackChatClient.ModelKey) as string ?? modelId ?? _model;
        }

        /// <summary>Providers may report usage more than once (cumulative): the last non-null value wins.</summary>
        public void Usage(UsageDetails usage)
        {
            _tokensIn = usage.InputTokenCount ?? _tokensIn;
            _tokensOut = usage.OutputTokenCount ?? _tokensOut;
        }

        public void Complete() => Record(failure: null);

        public void Fail(Exception ex) => Record(TraceRecorder.DescribeFailure(ex));

        /// <summary>Called from <c>finally</c>: records the call as cancelled if neither <see cref="Complete"/> nor <see cref="Fail"/> ran.</summary>
        public void Abandon() => Record(new Dictionary<string, object?> { ["error"] = "cancelled" });

        private void Record(IReadOnlyDictionary<string, object?>? failure)
        {
            if (_recorded)
                return;
            _recorded = true;

            // The failed call keeps provider/model (when known) next to the error, so the panel shows which call broke.
            var output = new Dictionary<string, object?> { ["provider"] = _provider, ["model"] = _model };
            if (failure is null)
            {
                output["text"] = TracePayload.MaskAndClip(_text.ToString(), MaxOutputChars);
                output["toolCalls"] = _toolCalls;
                output["finishReason"] = _finishReason;
            }
            else
            {
                foreach (var (key, value) in failure)
                    output[key] = value;
            }

            recorder.RecordLlmCall(_model, InputPayload(), output, _tokensIn, _tokensOut, recorder.Elapsed(_start));
        }

        private object InputPayload()
        {
            var rest = input.Count > 0 && input[0].Role == ChatRole.System ? input.Skip(1).ToList() : input;
            return new
            {
                messageCount = input.Count,
                messages = rest.Skip(Math.Max(0, rest.Count - InputMessages)).Select(m => new
                {
                    role = m.Role.Value,
                    text = TracePayload.MaskAndClip(Describe(m), MaxMessageChars),
                }),
            };
        }

        private static string Describe(ChatMessage message)
        {
            var text = new StringBuilder();
            foreach (var content in message.Contents)
            {
                if (text.Length > 0)
                    text.Append(' ');
                switch (content)
                {
                    case TextContent t:
                        text.Append(t.Text);
                        break;
                    case FunctionCallContent call:
                        text.Append("[tool call ").Append(call.Name).Append(']');
                        break;
                    case FunctionResultContent result:
                        text.Append("[tool result ").Append(result.Result?.ToString()).Append(']');
                        break;
                }
            }

            return text.ToString();
        }
    }
}
