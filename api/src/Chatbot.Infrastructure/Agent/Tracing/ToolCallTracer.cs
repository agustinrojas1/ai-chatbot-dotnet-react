using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Microsoft.Extensions.AI;

namespace Chatbot.Infrastructure.Agent.Tracing;

/// <summary>
/// Records every tool invocation as a <c>ToolCall</c> event (arguments, result, duration) through the
/// <c>FunctionInvokingChatClient.FunctionInvoker</c> hook of Microsoft.Extensions.AI 10.10, which replaces the default
/// <c>function.InvokeAsync(arguments)</c> and therefore sees exactly the calls that run. A tool that answers with an
/// error whose <c>code</c> is a rejected rule adds a <c>Guardrail</c> event right after, and a successful
/// <c>handoff_to_human</c> adds a <c>Handoff</c> event. A <c>search_knowledge_base</c> call that searched is recorded as ONE
/// <c>Retrieval</c> event instead of a <c>ToolCall</c> (its input, passages with scores, embedding cost): the tool leaves the full
/// search outcome in <see cref="ToolContext.PendingRetrieval"/>. A call rejected before searching (empty query, knowledge base
/// unavailable) is an ordinary <c>ToolCall</c>.
/// </summary>
public sealed class ToolCallTracer(TraceRecorder recorder, ToolContext toolContext)
{
    /// <summary>Tool error codes that mean "a rule held the line", besides every <c>invalid_*</c> (bad arguments).</summary>
    private static readonly HashSet<string> GuardrailCodes =
    [
        "slot_full", "slot_in_past", "session_awaiting_human", "session_closed", "range_too_long", "missing_target", "customer_data_not_provided",
    ];

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        var name = context.Function.Name;
        var arguments = context.Arguments.ToDictionary(a => a.Key, a => a.Value);
        var start = recorder.StartTimer();

        object? result;
        try
        {
            result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // the client left; the run is closed as cancelled
        }
        catch (Exception ex)
        {
            // The text goes back to the model (IncludeDetailedErrors); the trace keeps only the type.
            recorder.Record(TraceEventType.ToolCall, name, arguments, TraceRecorder.DescribeFailure(ex), durationMs: recorder.Elapsed(start));
            throw;
        }

        var duration = recorder.Elapsed(start);
        JsonElement? view = result switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(result),
        };
        if (name == AgentToolFactory.SearchKnowledgeBase && toolContext.TakeRetrieval() is { } retrieval)
            recorder.RecordRetrieval(retrieval, duration);
        else
            recorder.Record(TraceEventType.ToolCall, name, arguments, view, durationMs: duration);

        var code = ErrorCode(view);
        if (code is not null && IsGuardrail(code))
        {
            recorder.Record(TraceEventType.Guardrail, code, null,
                new { tool = name, code, message = Prop(view, "error")?.GetString() });
        }
        else if (code is null && name == AgentToolFactory.HandoffToHuman)
        {
            recorder.Record(TraceEventType.Handoff, name, null, new
            {
                reason = arguments.GetValueOrDefault("reason") ?? Prop(view, "reason"),
                handoffId = Prop(view, "handoffId"),
                alreadyRequested = Prop(view, "alreadyRequested"),
            });
        }

        return result;
    }

    private static bool IsGuardrail(string code) => GuardrailCodes.Contains(code) || code.StartsWith("invalid_", StringComparison.Ordinal);

    /// <summary>The <c>code</c> of a <see cref="ToolSupport.Error"/> result (<c>{ error, code }</c>), or null for a normal result.</summary>
    private static string? ErrorCode(JsonElement? result) =>
        Prop(result, "error") is { ValueKind: JsonValueKind.String } && Prop(result, "code") is { ValueKind: JsonValueKind.String } code
            ? code.GetString()
            : null;

    private static JsonElement? Prop(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;
}
