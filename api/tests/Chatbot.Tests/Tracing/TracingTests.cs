using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Infrastructure.Agent.Tracing;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Tracing;

[Collection(PostgresCollection.Name)]
public class TracingTests(PostgresFixture postgres)
{
    private const string Phone = "+54 11 5555 4444";

    /// <summary>search_catalog, create_booking (full phone in the arguments), then the answer. Tokens 400/20, 600/40, 800/30.</summary>
    private static async Task<(AgentWorld World, Guid SessionId, List<AgentEvent> Events, AvailabilitySlot Slot)> RunBookingFlowAsync(PostgresFixture postgres)
    {
        var llm = new ScriptedChatClient((_, _) => throw new InvalidOperationException("script not set"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        AvailabilitySlot slot;
        await using (var db = world.NewDb())
        {
            var clinic = await db.Businesses.SingleAsync(b => b.Niche == Niche.Clinic);
            slot = await db.AvailabilitySlots
                .Where(s => s.BusinessId == clinic.Id && s.StartsAt > DateTimeOffset.UtcNow.AddHours(3) && s.Booked < s.Capacity)
                .OrderBy(s => s.StartsAt).FirstAsync();
        }

        llm.Script = (call, _) => call switch
        {
            0 => Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(400, 20),
            1 => Turn.Call(AgentToolFactory.CreateBooking, new()
            {
                ["slotId"] = slot.Id.ToString(),
                ["customerName"] = "Ana Perez",
                ["customerPhone"] = Phone,
            }).WithUsage(600, 40),
            _ => Turn.Reply("Your request is ", "registered and pending confirmation.").WithUsage(800, 30),
        };

        var events = await world.RunAsync(sessionId, $"Yes, please book it. I'm Ana Perez, {Phone}");
        return (world, sessionId, events, slot);
    }

    [Fact]
    public async Task A_booking_flow_records_model_and_tool_steps_in_order_with_contiguous_sequences()
    {
        var (world, sessionId, events, _) = await RunBookingFlowAsync(postgres);
        var run = await world.SingleRunAsync(sessionId);

        var stored = await world.TraceAsync(run.Id);

        Assert.Equal(
            [(TraceEventType.LlmCall, ScriptedChatClient.Model), (TraceEventType.ToolCall, "search_catalog"),
             (TraceEventType.LlmCall, ScriptedChatClient.Model), (TraceEventType.ToolCall, "create_booking"),
             (TraceEventType.LlmCall, ScriptedChatClient.Model)],
            stored.Select(e => (e.Type, e.Name)));
        Assert.Equal(Enumerable.Range(0, stored.Count), stored.Select(e => e.Sequence));
        Assert.All(stored, e => Assert.Equal(run.Id, e.RunId));
        Assert.All(stored.Where(e => e.Type == TraceEventType.ToolCall), e => Assert.Null(e.TokensIn));

        // Provider and model of the call live in the output; the tool call has its arguments and its result.
        var firstCall = stored[0].Output!.Value;
        Assert.Equal(ScriptedChatClient.Provider, firstCall.GetProperty("provider").GetString());
        Assert.Equal(["search_catalog"], firstCall.GetProperty("toolCalls").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("cleaning", stored[1].Input!.Value.GetProperty("query").GetString());
        Assert.True(stored[1].Output.HasValue);
        Assert.Equal("Your request is registered and pending confirmation.", stored[4].Output!.Value.GetProperty("text").GetString());
        Assert.All(stored, e => Assert.True(e.DurationMs >= 0));
        Assert.True(stored[0].DurationMs > 0);
    }

    [Fact]
    public async Task Tokens_and_cost_are_per_event_and_the_run_totals_are_their_sum()
    {
        var (world, sessionId, events, _) = await RunBookingFlowAsync(postgres);
        var run = await world.SingleRunAsync(sessionId);
        var calls = (await world.TraceAsync(run.Id)).Where(e => e.Type == TraceEventType.LlmCall).ToList();

        // gemini-3.5-flash-lite: 0.30 in / 2.50 out per million tokens.
        Assert.Equal([(400, 20, 0.00017m), (600, 40, 0.00028m), (800, 30, 0.000315m)], calls.Select(e => (e.TokensIn!.Value, e.TokensOut!.Value, e.CostUsd!.Value)));

        var completed = Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal(calls.Sum(e => e.CostUsd!.Value), completed.CostUsd);
        Assert.Equal(calls.Sum(e => e.CostUsd!.Value), run.CostUsd);
        Assert.Equal((1800, 90), (calls.Sum(e => e.TokensIn!.Value), calls.Sum(e => e.TokensOut!.Value)));
        Assert.Equal((1800L, 90L), (completed.TokensIn, completed.TokensOut));
        Assert.Equal((1800, 90), (run.TokensIn, run.TokensOut));
    }

    [Fact]
    public async Task Events_stream_as_trace_events_that_match_the_stored_ones_and_tools_come_before_the_answer()
    {
        var (world, sessionId, events, _) = await RunBookingFlowAsync(postgres);
        var run = await world.SingleRunAsync(sessionId);
        var stored = await world.TraceAsync(run.Id);

        var streamed = events.OfType<Trace>().ToList();
        Assert.Equal(stored.Select(e => e.Sequence), streamed.Select(t => t.Sequence));
        Assert.All(streamed, t => Assert.Equal(run.Id, t.RunId));
        foreach (var (s, t) in stored.Zip(streamed))
        {
            Assert.Equal((s.Type, s.Name, s.TokensIn, s.TokensOut, s.CostUsd, s.DurationMs), (t.Type, t.Name, t.TokensIn, t.TokensOut, t.CostUsd, t.DurationMs));
            Assert.True(JsonEquality.Equal(s.Input, t.Input));
            Assert.True(JsonEquality.Equal(s.Output, t.Output));
        }

        // Every step but the last model call is out before the first token of the answer.
        var firstToken = events.FindIndex(e => e is Token);
        Assert.Equal(4, events.Take(firstToken).OfType<Trace>().Count());
        Assert.Equal(["LlmCall", "ToolCall", "LlmCall", "ToolCall"], events.Take(firstToken).OfType<Trace>().Select(t => t.Type.ToString()));
        Assert.IsType<Trace>(events[^2]);
        Assert.IsType<RunCompleted>(events[^1]);
    }

    [Fact]
    public async Task Phones_are_masked_in_the_stored_and_streamed_trace_but_the_booking_keeps_the_full_number()
    {
        var (world, sessionId, events, _) = await RunBookingFlowAsync(postgres);
        var run = await world.SingleRunAsync(sessionId);
        var stored = await world.TraceAsync(run.Id);

        string[] everything =
        [
            .. stored.Select(e => e.Input?.GetRawText() + e.Output?.GetRawText()),
            .. events.OfType<Trace>().Select(t => t.Input?.GetRawText() + t.Output?.GetRawText()),
        ];
        Assert.All(everything, text => Assert.DoesNotContain("5555 4444", text));

        var arguments = stored.Single(e => e.Name == "create_booking").Input!.Value;
        Assert.Equal("+54 11 **** 4444", arguments.GetProperty("customerPhone").GetString());
        // The customer's own message is part of the input of the model call and is masked too.
        Assert.Contains("**** 4444", stored[0].Input!.Value.GetRawText());

        await using var db = world.NewDb();
        Assert.Equal(Phone, (await db.Bookings.SingleAsync()).CustomerPhone);
    }

    [Fact]
    public async Task A_full_slot_adds_a_guardrail_event_after_the_tool_call()
    {
        var llm = new ScriptedChatClient((_, _) => throw new InvalidOperationException("script not set"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);
        Guid slotId;
        await using (var db = world.NewDb())
        {
            var clinic = await db.Businesses.SingleAsync(b => b.Niche == Niche.Clinic);
            var full = new AvailabilitySlot
            {
                BusinessId = clinic.Id, StartsAt = DateTimeOffset.UtcNow.AddDays(2), EndsAt = DateTimeOffset.UtcNow.AddDays(2).AddMinutes(30), Capacity = 1, Booked = 1,
            };
            db.AvailabilitySlots.Add(full);
            await db.SaveChangesAsync();
            slotId = full.Id;
        }

        llm.Script = (call, _) => call == 0
            ? Turn.Call(AgentToolFactory.CreateBooking, new() { ["slotId"] = slotId.ToString(), ["customerName"] = "Ana Perez", ["customerPhone"] = Phone })
            : Turn.Reply("That slot is taken, pick another.");

        var events = await world.RunAsync(sessionId, $"book it, I'm Ana Perez, {Phone}");

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Ok, run.Status);
        var stored = await world.TraceAsync(run.Id);
        Assert.Equal(
            [TraceEventType.LlmCall, TraceEventType.ToolCall, TraceEventType.Guardrail, TraceEventType.LlmCall],
            stored.Select(e => e.Type));
        Assert.Equal("slot_full", stored[1].Output!.Value.GetProperty("code").GetString());
        Assert.Equal("slot_full", stored[2].Name);
        Assert.Equal("create_booking", stored[2].Output!.Value.GetProperty("tool").GetString());
        Assert.Equal(stored.Select(e => e.Sequence), Enumerable.Range(0, 4));
        Assert.Equal(1, events.OfType<Trace>().Count(t => t.Type == TraceEventType.Guardrail));
        await using var check = world.NewDb();
        Assert.Empty(await check.Bookings.ToListAsync());
    }

    [Fact]
    public async Task Invalid_arguments_returned_by_a_tool_are_guardrails_and_a_normal_result_is_not()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.CreateBooking, new() { ["customerName"] = "Ana Perez", ["customerPhone"] = "12" }),
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }),
            Turn.Reply("done"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync(Niche.Clinic);

        await world.RunAsync(sessionId, $"book it, I'm Ana Perez, {Phone}");

        var stored = await world.TraceAsync((await world.SingleRunAsync(sessionId)).Id);
        var guardrails = stored.Where(e => e.Type == TraceEventType.Guardrail).ToList();
        var guardrail = Assert.Single(guardrails);
        Assert.StartsWith("invalid_", guardrail.Name);
        Assert.Equal(TraceEventType.ToolCall, stored[stored.IndexOf(guardrail) - 1].Type);
    }

    [Fact]
    public async Task The_handoff_tool_adds_a_handoff_event_with_the_reason()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "wants a person", ["summary"] = "Asked for staff." }),
            Turn.Reply("A person will continue."));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "I want a person");

        var stored = await world.TraceAsync((await world.SingleRunAsync(sessionId)).Id);
        Assert.Equal([TraceEventType.LlmCall, TraceEventType.ToolCall, TraceEventType.Handoff, TraceEventType.LlmCall], stored.Select(e => e.Type));
        Assert.Equal("wants a person", stored[2].Output!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Payloads_are_truncated_to_4_KB()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = new string('q', 10_000) }),
            Turn.Reply(new string('a', 6000)));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "search please");

        var stored = await world.TraceAsync((await world.SingleRunAsync(sessionId)).Id);

        // A tool argument over the limit: the whole input becomes a marked preview.
        var input = stored.Single(e => e.Type == TraceEventType.ToolCall).Input!.Value;
        Assert.Equal(JsonValueKind.String, input.ValueKind);
        Assert.Equal(TracePayload.MaxChars + TracePayload.TruncatedMarker.Length, input.GetString()!.Length);

        // A long answer: the text is clipped and the rest of the output (provider, model) survives.
        var output = stored[^1].Output!.Value;
        var text = output.GetProperty("text").GetString()!;
        Assert.EndsWith(TracePayload.TruncatedMarker, text);
        Assert.Equal(TracingChatClient.MaxOutputChars + TracePayload.TruncatedMarker.Length, text.Length);
        Assert.Equal(ScriptedChatClient.Provider, output.GetProperty("provider").GetString());
        Assert.All(stored, e =>
        {
            Assert.True((e.Input?.GetRawText().Length ?? 0) <= 2 * TracePayload.MaxChars);
            Assert.True((e.Output?.GetRawText().Length ?? 0) <= 2 * TracePayload.MaxChars);
        });
    }

    [Fact]
    public async Task A_failed_model_call_is_an_LlmCall_with_the_error_followed_by_the_run_error_with_contiguous_sequences()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(100, 10),
            Turn.Fail(new HttpRequestException("boom secret", null, System.Net.HttpStatusCode.BadGateway)));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "hello there");

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        var stored = await world.TraceAsync(run.Id);
        Assert.Equal(
            [TraceEventType.LlmCall, TraceEventType.ToolCall, TraceEventType.LlmCall, TraceEventType.Error],
            stored.Select(e => e.Type));
        Assert.Equal(Enumerable.Range(0, 4), stored.Select(e => e.Sequence));
        Assert.Equal(502, stored[2].Output!.Value.GetProperty("status").GetInt32());
        Assert.DoesNotContain("secret", stored[2].Output!.Value.GetRawText());
        Assert.Null(stored[2].TokensIn);
        // The failure of the run closes the trace, after the failed call.
        Assert.Equal("agent_error", stored[3].Name);
        Assert.Equal(stored.Select(e => e.Sequence), events.OfType<Trace>().Select(t => t.Sequence));
        Assert.Equal(run.CostUsd, stored.Where(e => e.Type == TraceEventType.LlmCall).Sum(e => e.CostUsd ?? 0));
    }

    [Fact]
    public async Task Hitting_the_iteration_limit_keeps_sequences_contiguous_and_ends_with_the_guardrail()
    {
        var llm = new ScriptedChatClient((_, _) => Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "x" }));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxIterations = 1 });
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "show me everything");

        var stored = await world.TraceAsync((await world.SingleRunAsync(sessionId)).Id);
        Assert.Equal(
            [TraceEventType.LlmCall, TraceEventType.ToolCall, TraceEventType.LlmCall, TraceEventType.Guardrail],
            stored.Select(e => e.Type));
        Assert.Equal(Enumerable.Range(0, 4), stored.Select(e => e.Sequence));
        Assert.Equal("iteration_limit", stored[^1].Name);
        Assert.Equal(stored.Select(e => e.Sequence), events.OfType<Trace>().Select(t => t.Sequence));
        Assert.IsType<Error>(events[^1]);
    }
}

/// <summary>Structural JSON comparison: jsonb does not keep key order or number formatting.</summary>
internal static class JsonEquality
{
    public static bool Equal(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return Equal(a.Value, b.Value);
    }

    public static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var right = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var other) && Equal(p.Value, other));
            case JsonValueKind.Array:
                var l = a.EnumerateArray().ToList();
                var r = b.EnumerateArray().ToList();
                return l.Count == r.Count && l.Zip(r).All(p => Equal(p.First, p.Second));
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetDecimal() == b.GetDecimal();
            default:
                return true;
        }
    }
}
