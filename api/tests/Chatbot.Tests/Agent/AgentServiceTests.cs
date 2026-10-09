using System.Net;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using DbMessage = Chatbot.Core.Domain.ChatMessage;

namespace Chatbot.Tests.Agent;

[Collection(PostgresCollection.Name)]
public class AgentServiceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Streams_tokens_in_order_and_closes_the_run_with_tokens_cost_and_duration()
    {
        var llm = new ScriptedChatClient(Turn.Reply("Hola, ", "¿en qué ", "puedo ayudarte?").WithUsage(1000, 200));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "Hola, quiero un turno");

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(
            [typeof(RunStarted), typeof(Token), typeof(Token), typeof(Token), typeof(Trace), typeof(RunCompleted)],
            events.Select(e => e.GetType()));
        Assert.Equal(run.Id, Assert.IsType<RunStarted>(events[0]).RunId);
        Assert.Equal(["Hola, ", "¿en qué ", "puedo ayudarte?"], events.OfType<Token>().Select(t => t.Text));

        // 1000 in x 0.30 + 200 out x 2.50 per million tokens.
        var completed = Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal(new RunCompleted(run.Id, 1000, 200, 0.0008m, completed.DurationMs, ScriptedChatClient.Model, ScriptedChatClient.Provider), completed);

        Assert.Equal(AgentRunStatus.Ok, run.Status);
        Assert.Equal((1000, 200, 0.0008m), (run.TokensIn, run.TokensOut, run.CostUsd));
        Assert.Equal(ScriptedChatClient.Model, run.Model);
        Assert.True(run.DurationMs > 0);
        Assert.Equal(run.DurationMs, completed.DurationMs);

        // Only the user message and the final answer are stored: no tool or system messages.
        var messages = await world.MessagesAsync(sessionId);
        Assert.Equal([(MessageRole.User, "Hola, quiero un turno"), (MessageRole.Assistant, "Hola, ¿en qué puedo ayudarte?")],
            messages.Select(m => (m.Role, m.Content)));
        Assert.Equal(messages[0].Id, run.UserMessageId);
    }

    [Fact]
    public async Task Sums_usage_over_every_model_call_of_the_run()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(500, 30),
            Turn.Reply("A cleaning costs...").WithUsage(700, 50));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "How much is a cleaning?");

        var completed = Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal((1200, 80), (completed.TokensIn, completed.TokensOut));
        Assert.Equal(2, llm.Calls.Count);
    }

    [Fact]
    public async Task Language_is_saved_and_the_language_instruction_ends_the_system_prompt()
    {
        var llm = new ScriptedChatClient(Turn.Reply("a"), Turn.Reply("b"), Turn.Reply("c"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "Hola, quiero reservar un turno para mañana");
        await world.RunAsync(sessionId, "ok"); // ambiguous: keeps Spanish
        await world.RunAsync(sessionId, "Actually, can you show me the price list please?");

        await using var db = world.NewDb();
        Assert.Equal("en", (await db.ChatSessions.SingleAsync(s => s.Id == sessionId)).Language);

        string[] expected =
        [
            "Reply ONLY in Spanish. Translate catalog details if needed.",
            "Reply ONLY in Spanish. Translate catalog details if needed.",
            "Reply ONLY in English. Translate catalog details if needed.",
        ];
        // The instruction is the last paragraph of the one system message (ADR-014), after the business prompt.
        Assert.Equal(expected.Select(e => AgentService.LanguageLabel + e), llm.Calls.Select(call => call[0].Text.Split("\n\n")[^1]));
        Assert.All(llm.Calls, call =>
        {
            Assert.Equal(ChatRole.System, call[0].Role);
            Assert.Contains("Session status: Active", call[0].Text);
            Assert.NotEqual(ChatRole.System, call[^1].Role);
        });
    }

    [Fact]
    public async Task The_model_receives_exactly_one_system_message_even_when_tools_ran_in_earlier_turns()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }),
            Turn.Reply("A cleaning is available."),
            Turn.Reply("Done."));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "How much is a cleaning?");
        await world.RunAsync(sessionId, "Yes, book it");

        // Gemini drops system messages when there is more than one (ADR-014): every call has one, and it is first.
        Assert.All(llm.Calls, call =>
        {
            Assert.Equal(1, call.Count(m => m.Role == ChatRole.System));
            Assert.Equal(ChatRole.System, call[0].Role);
        });
    }

    [Fact]
    public async Task Sends_only_the_last_N_history_messages_between_the_system_messages()
    {
        var llm = new ScriptedChatClient(Turn.Reply("done"));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxHistoryMessages = 4 });
        var sessionId = await world.NewSessionAsync();

        await using (var db = world.NewDb())
        {
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            db.ChatMessages.AddRange(Enumerable.Range(1, 10).Select(i => new DbMessage
            {
                SessionId = sessionId,
                Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant,
                Content = $"old {i}",
                CreatedAt = start.AddMinutes(i),
            }));
            await db.SaveChangesAsync();
        }

        await world.RunAsync(sessionId, "latest question");

        var sent = llm.Calls.Single();
        Assert.Equal(5, sent.Count); // system prompt + 4 history messages
        Assert.Equal(
            [(ChatRole.Assistant, "old 8"), (ChatRole.User, "old 9"), (ChatRole.Assistant, "old 10"), (ChatRole.User, "latest question")],
            sent.Skip(1).Take(4).Select(m => (m.Role, m.Text)));
    }

    [Fact]
    public async Task Passes_the_tools_and_the_output_limit_to_the_model()
    {
        var llm = new ScriptedChatClient(Turn.Reply("ok"));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxOutputTokens = 321 });

        await world.RunAsync(await world.NewSessionAsync(), "hello there");

        var options = Assert.Single(llm.Options)!;
        Assert.Equal(321, options.MaxOutputTokens);
        Assert.Equal(
            ["search_catalog", "get_offering_details", "check_availability", "create_booking", "handoff_to_human"],
            options.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task A_scripted_booking_flow_creates_a_pending_booking()
    {
        // The script needs ids that only exist once the data is seeded, so it is set after the world is created.
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
                ["customerPhone"] = "+54 11 5555 4444",
            }).WithUsage(600, 40),
            _ => Turn.Reply("Your request is ", "registered and pending confirmation.").WithUsage(800, 30),
        };

        var events = await world.RunAsync(sessionId, "Yes, please book it. I'm Ana Perez, +54 11 5555 4444");

        Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal("Your request is registered and pending confirmation.", string.Concat(events.OfType<Token>().Select(t => t.Text)));

        await using var check = world.NewDb();
        var booking = await check.Bookings.SingleAsync();
        Assert.Equal(BookingStatus.PendingConfirmation, booking.Status);
        Assert.Equal((sessionId, slot.Id, "Ana Perez"), (booking.SessionId, booking.SlotId, booking.CustomerName));
        Assert.Equal(slot.Booked + 1, await check.AvailabilitySlots.Where(s => s.Id == slot.Id).Select(s => s.Booked).SingleAsync());

        // The third model call saw the tool result of the booking.
        var results = llm.Calls[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains("PENDING", JsonSerializer.Serialize(results[1].Result));

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal((AgentRunStatus.Ok, 1800, 90), (run.Status, run.TokensIn, run.TokensOut));
    }

    [Fact]
    public async Task Handoff_tool_emits_the_handoff_event_and_moves_the_session_to_AwaitingHuman()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "customer asked for a person", ["summary"] = "Wants to talk to staff." }),
            Turn.Reply("A person will continue this conversation."));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "I want to talk to a person");

        // The model call that asked for the tool, the tool and the handoff come first; the final call completes with its stream.
        Assert.Equal(
            [typeof(RunStarted), typeof(Trace), typeof(Trace), typeof(Trace), typeof(Token), typeof(Trace), typeof(Handoff), typeof(RunCompleted)],
            events.Select(e => e.GetType()));
        Assert.Equal("customer asked for a person", events.OfType<Handoff>().Single().Reason);

        await using var db = world.NewDb();
        Assert.Equal(SessionStatus.AwaitingHuman, (await db.ChatSessions.SingleAsync(s => s.Id == sessionId)).Status);
        Assert.Equal(HandoffStatus.Open, (await db.HandoffRequests.SingleAsync(h => h.SessionId == sessionId)).Status);
    }

    [Fact]
    public async Task An_unconfigured_provider_is_a_non_retryable_error_that_says_so_without_leaking_settings()
    {
        // The real client the app builds when Llm:Primary:ApiKey is blank.
        var world = await AgentWorld.CreateAsync(postgres, new ScriptedChatClient());
        var unconfigured = Chatbot.Infrastructure.Llm.LlmClientFactory.Create(new Chatbot.Infrastructure.Llm.LlmOptions());
        var sessionId = await world.NewSessionAsync();
        await using var db = world.NewDb();
        var service = new AgentService(db, unconfigured, Microsoft.Extensions.Options.Options.Create(world.Options), world.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentService>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in service.RunAsync(sessionId, "hello there"))
            events.Add(e);

        var error = Assert.IsType<Error>(events[^1]);
        Assert.False(error.Retryable);
        Assert.Equal("The AI provider is not configured on this server.", error.Message);
        Assert.DoesNotContain("Llm:", error.Message);

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        var failure = (await world.TraceAsync(run.Id)).Last();
        Assert.Equal((TraceEventType.Error, "agent_error"), (failure.Type, failure.Name));
        Assert.DoesNotContain("Llm:", failure.Output!.Value.GetRawText());
    }

    [Fact]
    public async Task Provider_error_before_any_output_emits_a_friendly_error_and_closes_the_run_in_error()
    {
        var llm = new ScriptedChatClient(Turn.Fail(new HttpRequestException("secret-key-abc exploded", null, HttpStatusCode.ServiceUnavailable)));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "hello there");

        Assert.Equal([typeof(RunStarted), typeof(Trace), typeof(Trace), typeof(Error)], events.Select(e => e.GetType()));
        var error = Assert.IsType<Error>(events[^1]);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("secret-key", error.Message);

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        Assert.True(run.DurationMs > 0);

        // The failed model call, then the failure itself.
        var traced = await world.TraceAsync(run.Id);
        Assert.Equal([TraceEventType.LlmCall, TraceEventType.Error], traced.Select(e => e.Type));
        var trace = traced[^1];
        Assert.Equal((TraceEventType.Error, "agent_error"), (trace.Type, trace.Name));
        Assert.Equal(503, trace.Output!.Value.GetProperty("status").GetInt32());
        Assert.DoesNotContain("secret-key", trace.Output.Value.GetRawText());

        // The user message is kept; no assistant message was invented.
        Assert.Equal([MessageRole.User], (await world.MessagesAsync(sessionId)).Select(m => m.Role));
    }

    [Fact]
    public async Task Unexpected_exception_is_not_retried_with_details_and_does_not_escape()
    {
        var llm = new ScriptedChatClient(Turn.Fail(new InvalidOperationException("LLM is not configured")));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "hello there");

        var error = Assert.IsType<Error>(events[^1]);
        Assert.DoesNotContain("not configured", error.Message);
        Assert.Equal(AgentRunStatus.Error, (await world.SingleRunAsync(sessionId)).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Hitting_the_iteration_limit_ends_in_a_non_retryable_error(int maxIterations)
    {
        // The model asks for a tool on every call and never answers.
        var llm = new ScriptedChatClient((_, _) => Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "x" }));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxIterations = maxIterations });
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "show me everything");

        // MEAI 10.10: the limit counts tool rounds, so N allows N rounds plus the model call that follows the last one.
        Assert.Equal(maxIterations + 1, llm.Calls.Count);
        var error = Assert.IsType<Error>(events[^1]);
        Assert.False(error.Retryable);

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        var trace = (await world.TraceAsync(run.Id)).Last();
        Assert.Equal((TraceEventType.Guardrail, "iteration_limit"), (trace.Type, trace.Name));
        Assert.Equal(maxIterations, trace.Output!.Value.GetProperty("maxIterations").GetInt32());
    }

    [Fact]
    public async Task The_run_row_reads_Error_until_the_run_completes()
    {
        var llm = new ScriptedChatClient(Turn.Reply("one", "two"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        AgentRunStatus? statusWhileStreaming = null;
        await using (var db = world.NewDb())
        {
            await foreach (var e in world.NewService(db).RunAsync(sessionId, "hello there"))
            {
                if (e is Token && statusWhileStreaming is null)
                    statusWhileStreaming = (await world.SingleRunAsync(sessionId)).Status;
            }
        }

        Assert.Equal(AgentRunStatus.Error, statusWhileStreaming);
        Assert.Equal(AgentRunStatus.Ok, (await world.SingleRunAsync(sessionId)).Status);
    }

    [Fact]
    public async Task Tool_calls_and_results_of_a_turn_are_sent_to_the_model_in_the_next_turn()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }),
            Turn.Reply("A cleaning is available."),
            Turn.Reply("Done."));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "How much is a cleaning?");
        await world.RunAsync(sessionId, "Yes, book it");

        // Third model call = the second turn. The tool exchange sits between the question and the answer it produced.
        var sent = llm.Calls[2];
        Assert.Equal(
            [
                "system:TextContent",
                "user:TextContent",
                "assistant:FunctionCallContent",
                "tool:FunctionResultContent",
                "assistant:TextContent",
                "user:TextContent",
            ],
            sent.Select(m => $"{m.Role}:{string.Join(",", m.Contents.Select(c => c.GetType().Name))}"));

        var call = Assert.IsType<FunctionCallContent>(sent[2].Contents.Single());
        var result = Assert.IsType<FunctionResultContent>(sent[3].Contents.Single());
        Assert.Equal((AgentToolFactory.SearchCatalog, call.CallId), (call.Name, result.CallId));
        Assert.Equal("cleaning", Assert.IsType<JsonElement>(call.Arguments!["query"]).GetString());
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(result.Result).ValueKind);
    }

    [Fact]
    public async Task Old_tool_exchanges_fall_out_of_the_history_window_and_oversized_payloads_are_truncated()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = new string('x', 5000) }),
            Turn.Reply("nothing found"),
            Turn.Reply("ok"));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxHistoryMessages = 2 });
        var sessionId = await world.NewSessionAsync();

        await world.RunAsync(sessionId, "first question");

        await using (var db = world.NewDb())
        {
            var row = await db.ChatMessages.SingleAsync(m => m.SessionId == sessionId && m.Role == MessageRole.Tool);
            var query = JsonDocument.Parse(row.Content).RootElement.GetProperty("arguments").GetString();
            Assert.EndsWith("…[truncated]", query);
            Assert.True(row.Content.Length < 4500);
        }

        await world.RunAsync(sessionId, "second question");

        // Window of 2 text messages = the assistant answer and the new question. The tool exchange is older than both.
        Assert.Equal([ChatRole.Assistant, ChatRole.User], llm.Calls[2].Skip(1).Select(m => m.Role));
    }

    [Fact]
    public async Task A_handoff_made_by_a_tool_is_reported_even_if_the_run_then_fails()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "wants a person", ["summary"] = "Asked for staff." }),
            Turn.Reply()); // the model says nothing after the tool
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "I want a person");

        Assert.Equal([typeof(RunStarted), typeof(Handoff), typeof(Error)], events.Where(e => e is not Trace).Select(e => e.GetType()));
        Assert.Equal("wants a person", events.OfType<Handoff>().Single().Reason);

        await using var db = world.NewDb();
        Assert.Equal(SessionStatus.AwaitingHuman, (await db.ChatSessions.SingleAsync(s => s.Id == sessionId)).Status);
        // The handoff exchange is kept, so the next turn knows it happened.
        Assert.Equal(1, await db.ChatMessages.CountAsync(m => m.SessionId == sessionId && m.Role == MessageRole.Tool));
    }

    [Fact]
    public async Task Each_model_call_is_priced_with_the_model_that_answered_it()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(1000, 100),
            Turn.Reply("Here you go.").WithUsage(500, 50).AnsweredBy("fallback-model", "groq"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        await using (var db = world.NewDb())
        {
            db.ModelPricings.Add(new ModelPricing { Model = "fallback-model", InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m });
            await db.SaveChangesAsync();
        }

        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "How much is a cleaning?");

        // gemini: 1000 x 0.30 + 100 x 2.50 = 550; fallback: 500 x 1 + 50 x 2 = 600 (per million).
        var completed = Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal(0.00115m, completed.CostUsd);
        Assert.Equal("gemini-3.5-flash-lite, fallback-model", completed.Model);
        Assert.Equal("gemini, groq", completed.Provider);

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal((0.00115m, "gemini-3.5-flash-lite, fallback-model"), (run.CostUsd, run.Model));
    }

    [Fact]
    public async Task Tool_arguments_that_do_not_fit_are_reported_to_the_model_so_it_can_correct_them()
    {
        var llm = new ScriptedChatClient((call, _) => call switch
        {
            0 => Turn.Call(AgentToolFactory.CreateBooking, new() { ["items"] = "not a list", ["customerName"] = "Ana" }),
            _ => Turn.Reply("Sorry, let me ask again."),
        });
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "book it please");

        // The run survives: the second model call received the failure as the tool result.
        Assert.IsType<RunCompleted>(events[^1]);
        var result = llm.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        var text = Assert.IsType<string>(result.Result);
        Assert.NotEqual("Error: Function failed.", text);
        Assert.Contains("cannot be converted", text);

        // The user stream never carries exception text.
        Assert.DoesNotContain("converted", string.Concat(events.OfType<Token>().Select(t => t.Text)));
        await using var check = world.NewDb();
        Assert.Equal(0, await check.Bookings.CountAsync());
    }

    [Fact]
    public async Task Repeated_malformed_tool_calls_end_the_run_with_a_generic_error()
    {
        var llm = new ScriptedChatClient((_, _) => Turn.Call(AgentToolFactory.CreateBooking, new() { ["items"] = "not a list" }));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxIterations = 10 });
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "book it please");

        var error = Assert.IsType<Error>(events[^1]);
        Assert.DoesNotContain("converted", error.Message);
        Assert.Equal(AgentRunStatus.Error, (await world.SingleRunAsync(sessionId)).Status);
    }

    [Fact]
    public async Task An_empty_model_answer_is_an_error_not_an_empty_message()
    {
        var llm = new ScriptedChatClient(Turn.Reply().WithUsage(10, 0));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "hello there");

        Assert.IsType<Error>(events[^1]);
        Assert.Single(llm.Calls); // no tool ran, so there is nothing to nudge about
        Assert.Equal([MessageRole.User], (await world.MessagesAsync(sessionId)).Select(m => m.Role));
    }

    private const string Nudge = "(Reply to the customer now, based on the tool results above.)";

    [Fact]
    public async Task Empty_text_after_a_tool_call_gets_one_nudged_call_that_is_traced_priced_and_not_stored()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(500, 20),
            Turn.Reply().WithUsage(600, 0),
            Turn.Reply("A cleaning costs ", "USD 60.").WithUsage(700, 30));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "How much is a cleaning?");

        Assert.IsType<RunCompleted>(events[^1]);
        Assert.Equal("A cleaning costs USD 60.", string.Concat(events.OfType<Token>().Select(t => t.Text)));

        // Third call: same single system message, the tool exchange, and the nudge as the last (user) message.
        var nudged = llm.Calls[2];
        Assert.Equal(1, nudged.Count(m => m.Role == ChatRole.System));
        Assert.Equal((ChatRole.User, Nudge), (nudged[^1].Role, nudged[^1].Text));
        Assert.Contains(nudged, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(nudged, m => m.Contents.OfType<FunctionResultContent>().Any());

        // Usage and trace of every call, the extra one included.
        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal((AgentRunStatus.Ok, 1800, 50), (run.Status, run.TokensIn, run.TokensOut));
        var trace = await world.TraceAsync(run.Id);
        Assert.Equal(3, trace.Count(e => e.Type == TraceEventType.LlmCall));
        Assert.Equal(run.CostUsd, trace.Sum(e => e.CostUsd ?? 0));

        // The nudge is in no stored message; the answer is.
        var messages = await world.MessagesAsync(sessionId);
        Assert.DoesNotContain(messages, m => m.Content.Contains("Reply to the customer now"));
        Assert.Equal("A cleaning costs USD 60.", messages.Single(m => m.Role == MessageRole.Assistant).Content);
    }

    [Theory]
    [InlineData("I want to talk to a person please", "A person from the team will continue this conversation.")]
    [InlineData("Quiero hablar con una persona, por favor", "Una persona del equipo va a continuar con esta conversación.")]
    public async Task Empty_twice_after_a_handoff_sends_a_canned_message_in_the_customer_language(string text, string canned)
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "wants a person", ["summary"] = "Asked for staff." }).WithUsage(400, 20),
            Turn.Reply().WithUsage(450, 0),
            Turn.Reply().WithUsage(460, 0));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, text);

        Assert.Equal([typeof(RunStarted), typeof(Token), typeof(Handoff), typeof(RunCompleted)],
            events.Where(e => e is not Trace).Select(e => e.GetType()));
        Assert.Equal(canned, events.OfType<Token>().Single().Text);
        Assert.Equal(3, llm.Calls.Count);

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal((AgentRunStatus.Ok, 1310, 20), (run.Status, run.TokensIn, run.TokensOut));
        var messages = await world.MessagesAsync(sessionId);
        Assert.Equal(canned, messages.Single(m => m.Role == MessageRole.Assistant).Content);
        Assert.DoesNotContain(messages, m => m.Content.Contains("Reply to the customer now"));
    }

    [Fact]
    public async Task Empty_twice_after_a_tool_without_handoff_is_still_an_error_and_keeps_the_usage()
    {
        var llm = new ScriptedChatClient(
            Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "cleaning" }).WithUsage(500, 20),
            Turn.Reply().WithUsage(600, 0),
            Turn.Reply().WithUsage(700, 0));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, "How much is a cleaning?");

        var error = Assert.IsType<Error>(events[^1]);
        Assert.True(error.Retryable);
        Assert.Equal(3, llm.Calls.Count);

        // What was spent before the failure is not lost on the error path.
        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal((AgentRunStatus.Error, 1800, 20), (run.Status, run.TokensIn, run.TokensOut));
        Assert.True(run.CostUsd > 0);
        Assert.Equal([MessageRole.User, MessageRole.Tool], (await world.MessagesAsync(sessionId)).Select(m => m.Role));
    }

    [Fact]
    public async Task Input_longer_than_the_limit_is_rejected_without_creating_a_run_or_a_message()
    {
        var llm = new ScriptedChatClient(Turn.Reply("never"));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxInputChars = 50 });
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, new string('x', 51));

        var rejected = Assert.IsType<Rejected>(Assert.Single(events));
        Assert.Equal(RejectionReason.TextTooLong, rejected.Reason);
        await AssertNothingStoredAsync(world, sessionId, llm);

        // Exactly at the limit is fine.
        Assert.IsType<RunCompleted>((await world.RunAsync(sessionId, new string('x', 50)))[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData(null)]
    public async Task Empty_input_is_rejected(string? text)
    {
        var llm = new ScriptedChatClient(Turn.Reply("never"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        var events = await world.RunAsync(sessionId, text);

        Assert.Equal(RejectionReason.EmptyText, Assert.IsType<Rejected>(Assert.Single(events)).Reason);
        await AssertNothingStoredAsync(world, sessionId, llm);
    }

    [Fact]
    public async Task Closed_session_is_rejected()
    {
        var llm = new ScriptedChatClient(Turn.Reply("never"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync(status: SessionStatus.Closed);

        var events = await world.RunAsync(sessionId, "hello there");

        Assert.Equal(RejectionReason.SessionClosed, Assert.IsType<Rejected>(Assert.Single(events)).Reason);
        await AssertNothingStoredAsync(world, sessionId, llm);
    }

    [Fact]
    public async Task Unknown_session_is_rejected()
    {
        var llm = new ScriptedChatClient(Turn.Reply("never"));
        var world = await AgentWorld.CreateAsync(postgres, llm);

        var events = await world.RunAsync(Guid.NewGuid(), "hello there");

        Assert.Equal(RejectionReason.SessionNotFound, Assert.IsType<Rejected>(Assert.Single(events)).Reason);
    }

    [Fact]
    public async Task A_session_waiting_for_a_human_still_gets_an_answer_with_its_status_in_the_prompt()
    {
        var llm = new ScriptedChatClient(Turn.Reply("A person will continue."));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync(status: SessionStatus.AwaitingHuman);

        var events = await world.RunAsync(sessionId, "hello?");

        Assert.IsType<RunCompleted>(events[^1]);
        Assert.Contains("Session status: AwaitingHuman", llm.Calls.Single()[0].Text);
    }

    [Fact]
    public async Task Cancelling_mid_stream_closes_the_run_in_error_without_throwing()
    {
        var llm = new ScriptedChatClient(Turn.Reply("partial answer")) { HangAfterFirstUpdate = true };
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        using var cts = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        await using (var db = world.NewDb())
        {
            await foreach (var e in world.NewService(db).RunAsync(sessionId, "hello there", cts.Token))
            {
                events.Add(e);
                if (e is Token)
                    await cts.CancelAsync();
            }
        }

        Assert.Equal([typeof(RunStarted), typeof(Token)], events.Select(e => e.GetType()));
        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        Assert.Equal(["LlmCall", "agent_cancelled"], (await world.TraceAsync(run.Id)).Select(e => e.Type == TraceEventType.LlmCall ? "LlmCall" : e.Name));
        Assert.Equal([MessageRole.User], (await world.MessagesAsync(sessionId)).Select(m => m.Role));
    }

    [Fact]
    public async Task Stopping_the_enumeration_early_closes_the_run_in_error()
    {
        var llm = new ScriptedChatClient(Turn.Reply("one", "two", "three"));
        var world = await AgentWorld.CreateAsync(postgres, llm);
        var sessionId = await world.NewSessionAsync();

        await using (var db = world.NewDb())
        {
            await foreach (var e in world.NewService(db).RunAsync(sessionId, "hello there"))
            {
                if (e is Token)
                    break; // the SSE writer failed: the endpoint disposes the enumerator
            }
        }

        var run = await world.SingleRunAsync(sessionId);
        Assert.Equal(AgentRunStatus.Error, run.Status);
        Assert.Equal(["LlmCall", "agent_cancelled"], (await world.TraceAsync(run.Id)).Select(e => e.Type == TraceEventType.LlmCall ? "LlmCall" : e.Name));
    }

    private static async Task AssertNothingStoredAsync(AgentWorld world, Guid sessionId, ScriptedChatClient llm)
    {
        await using var db = world.NewDb();
        Assert.Empty(llm.Calls);
        Assert.Empty(await db.AgentRuns.Where(r => r.SessionId == sessionId).ToListAsync());
        Assert.Empty(await db.ChatMessages.Where(m => m.SessionId == sessionId).ToListAsync());
    }
}
