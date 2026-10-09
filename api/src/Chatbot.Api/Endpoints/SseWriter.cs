using System.Text.Json;
using Chatbot.Infrastructure.Agent;

namespace Chatbot.Api.Endpoints;

/// <summary>Writes <see cref="AgentEvent"/>s as Server-Sent Events: <c>event: name</c> and <c>data: json</c> (SPEC 10).</summary>
internal static class SseWriter
{
    // Web defaults: camelCase property names, which are the field names of the contract.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Sends the headers and flushes them, so the client sees the stream open before the first token.</summary>
    public static Task StartAsync(HttpResponse response, CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // nginx: do not buffer the stream
        return response.Body.FlushAsync(ct);
    }

    public static async Task WriteAsync(HttpResponse response, AgentEvent agentEvent, CancellationToken ct)
    {
        var name = agentEvent switch
        {
            RunStarted => "run_started",
            Token => "token",
            Handoff => "handoff",
            RunCompleted => "run_completed",
            Error => "error",
            _ => throw new InvalidOperationException($"{agentEvent.GetType().Name} is not an SSE event."),
        };

        // The record itself is the data payload: its property names are the contract.
        var data = JsonSerializer.Serialize(agentEvent, agentEvent.GetType(), Json);
        await response.WriteAsync($"event: {name}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
