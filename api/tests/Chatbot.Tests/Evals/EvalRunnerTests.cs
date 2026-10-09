using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Evals;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Reporting;
using Chatbot.Evals.Running;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Evals;

/// <summary>The harness against the real <c>AgentService</c> and a real database, with a scripted model instead of a real one.</summary>
[Collection(PostgresCollection.Name)]
public class EvalRunnerTests(PostgresFixture postgres)
{
    private const string OrderCase = """
        - id: order-flow
          category: order
          niche: restaurant
          turns:
            - say: "I'd like 2 Margherita pizzas and 1 Tiramisu."
              forbidToolCalls: [create_booking]
              mustMention: ["31"]
            - say: "Yes, place the order. I'm Luca Rossi, +1 555 222 3344."
              expectToolCalls:
                - name: create_booking
                  count: 1
                  args: { customerName: Luca Rossi, items: [{ qty: 2 }] }
          expectLanguage: en
          expectBooking: true
          expectBookingTotal: 31
          mustMention:
            - fromTool: create_booking
              path: total
            - "pending"
        """;

    /// <summary>A migrated and seeded database with the harness services on top; <paramref name="model"/> gets its connection string.</summary>
    private async Task<ServiceProvider> HostAsync(Func<string, IChatClient> model, ModelCallPolicy? policy = null)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        var services = EvalHost.Build(EvalConfiguration.Build(connectionString), model(connectionString), policy);
        await EvalHost.PrepareDatabaseAsync(services, CancellationToken.None);
        return services;
    }

    /// <summary>
    /// A model that never books on its own: it quotes the total, and calls <c>create_booking</c> for 2 Margherita and
    /// a Tiramisu only when the user says "Yes". The ids are looked up the first time they are needed (after the seed).
    /// </summary>
    private static ScriptedChatClient OrderingModel(string connectionString)
    {
        var items = new Lazy<JsonElement>(() =>
        {
            using var db = PostgresFixture.CreateContext(connectionString);
            var ids = db.Offerings.AsNoTracking().Where(o => o.Name == "Margherita" || o.Name == "Tiramisu").ToDictionary(o => o.Name, o => o.Id);
            return JsonSerializer.SerializeToElement(new[]
            {
                new { offeringId = ids["Margherita"], qty = 2 },
                new { offeringId = ids["Tiramisu"], qty = 1 },
            });
        });

        return new ScriptedChatClient((_, messages) =>
        {
            var last = messages.Last(m => m.Role != ChatRole.System);
            if (last.Contents.OfType<FunctionResultContent>().Any())
                return Turn.Reply("Your order is registered and pending confirmation by the business. Total: $31.").WithUsage(300, 40);
            if (last.Text.StartsWith("Yes", StringComparison.Ordinal))
            {
                return Turn.Call(AgentToolFactory.CreateBooking,
                    new() { ["customerName"] = "Luca Rossi", ["customerPhone"] = "+1 555 222 3344", ["items"] = items.Value }).WithUsage(200, 30);
            }

            return Turn.Reply("That is $31 in total. Shall I place it? I need your name and phone.").WithUsage(100, 20);
        });
    }

    [Fact]
    public async Task A_correct_order_conversation_passes_and_every_run_gets_a_fresh_session()
    {
        await using var services = await HostAsync(OrderingModel);
        var evalCase = CaseLoader.Parse("test.yaml", OrderCase).Single();

        var results = await EvalRunner.RunAsync(services, [evalCase], repeat: 2, onResult: null, CancellationToken.None);

        // Two runs of the same case both end with exactly one booking: they do not share a session.
        Assert.Equal([1, 2], results.Select(r => r.Transcript.Iteration));
        foreach (var result in results)
        {
            Assert.True(result.Passed, string.Join("\n", result.Failures));
            var turns = result.Transcript.Turns;
            Assert.Equal(["create_booking"], turns.SelectMany(t => t.ToolCalls).Select(c => c.Name));
            Assert.Empty(turns[0].Bookings);
            Assert.Equal(new BookingSnapshot(BookingStatus.PendingConfirmation, 31m, 31m, "Luca Rossi"), Assert.Single(turns[1].Bookings));
            // Usage of the run: 100/20 for the first message, 200/30 + 300/40 for the second.
            Assert.Equal((600, 90), (result.TokensIn, result.TokensOut));
            Assert.True(result.CostUsd > 0);
            Assert.Equal(ScriptedChatClient.Model, turns[1].Model);
        }
    }

    [Fact]
    public async Task The_same_conversation_fails_a_case_that_forbids_the_booking()
    {
        await using var services = await HostAsync(OrderingModel);
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: wrong-expectation
              category: order
              niche: restaurant
              turns:
                - "I'd like 2 Margherita pizzas and 1 Tiramisu."
                - "Yes, place the order. I'm Luca Rossi, +1 555 222 3344."
              forbidToolCalls: [create_booking]
              forbidBooking: true
              forbidPhrases: [pending]
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.False(result.Passed);
        Assert.Equal(
            ["no forbidden phrase", "does not call create_booking", "no booking created"],
            result.Failures.Select(f => f.Name));
    }

    [Fact]
    public async Task A_provider_error_fails_the_run_and_skips_the_rest_of_the_conversation()
    {
        await using var services = await HostAsync(_ => new ScriptedChatClient((_, _) => Turn.Fail(new InvalidOperationException("provider is down"))));
        var evalCase = CaseLoader.Parse("test.yaml", OrderCase).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.False(result.Passed);
        var execution = Assert.Single(result.Checks, c => c.Dimension == CheckDimension.Execution);
        Assert.Contains("turn 1: Something went wrong", execution.Detail);
        Assert.Contains("turn 2: skipped", execution.Detail);
        Assert.Contains(result.Failures, f => f.Name.Contains("calls create_booking"));
        Assert.Equal("", result.Transcript.Turns[0].Reply);
    }

    [Fact]
    public async Task A_handoff_is_read_from_the_database()
    {
        await using var services = await HostAsync(_ => new ScriptedChatClient((_, messages) =>
            messages.Last(m => m.Role != ChatRole.System).Contents.OfType<FunctionResultContent>().Any()
                ? Turn.Reply("A person from the team will continue this conversation with you.")
                : Turn.Call(AgentToolFactory.HandoffToHuman, new() { ["reason"] = "customer asked for a person", ["summary"] = "wants to talk to someone" })));
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: handoff-flow
              category: handoff
              niche: dental
              turns: ["I want to talk to a person."]
              expectLanguage: en
              expectToolCalls: [{ name: handoff_to_human }]
              expectHandoff: true
              forbidBooking: true
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        Assert.Equal(1, result.Transcript.Turns[0].Handoffs);
    }

    [Fact]
    public async Task The_fake_model_runs_the_harness_end_to_end_and_the_reports_render()
    {
        await using var services = await HostAsync(_ => new FakeChatClient());
        var cases = CaseLoader.LoadDirectory(EvalTestSupport.ShippedCasesDirectory());
        var selected = EvalOptions.Parse(["--category", "language"]).Select(cases);
        var progress = new List<string>();

        var results = await EvalRunner.RunAsync(services, selected, 1, (r, done, total) => progress.Add($"{done}/{total} {r.Transcript.Case.Id}"), CancellationToken.None);

        Assert.Equal(selected.Count, results.Count);
        Assert.Equal($"{selected.Count}/{selected.Count} {selected[^1].Id}", progress[^1]);
        var first = results.Single(r => r.Transcript.Case.Id == "lang-en-dental-price");
        Assert.Equal(["search_catalog"], first.Transcript.Turns[0].ToolCalls.Select(c => c.Name));
        Assert.Equal(FakeChatClient.Model, first.Transcript.Turns[0].Model);
        Assert.True(first.TokensIn > 0);
        // The dumb model answers in English but never says the price: only the content check fails.
        Assert.Equal(["mentions \"80\""], first.Failures.Select(f => f.Name));

        var summary = EvalSummary.Create(results);
        Assert.True(summary.Passed < summary.Runs); // the dumb model never says the prices, so it cannot pass them all
        var info = new RunInfo(DateTimeOffset.Now, TimeSpan.FromSeconds(1), Fake: true, Repeat: 1, null, "language", null);
        var markdown = MarkdownReport.Render(summary, info);
        Assert.Contains("fake-model", markdown);
        Assert.Contains("| language |", markdown);
        Assert.Contains("### lang-en-dental-price", markdown);
        Assert.Contains("Tool call: `search_catalog(", markdown);
    }

    [Fact]
    public async Task Names_of_the_catalog_of_the_niche_are_recorded_for_the_language_check()
    {
        await using var services = await HostAsync(_ => new FakeChatClient());
        var evalCase = CaseLoader.Parse("test.yaml", """
            - id: realestate-terms
              category: search
              niche: realestate
              turns: ["Do you have anything in Núñez?"]
              forbidBooking: true
            """).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        var terms = result.Transcript.ProtectedTerms;
        Assert.Contains("PrimeHaven Realty", terms);
        Assert.Contains("Núñez", terms);
        Assert.Contains("2-room apartment in Palermo", terms);
        Assert.DoesNotContain("Margherita", terms); // another business
        Assert.True(terms.Select(t => t.Length).SequenceEqual(terms.Select(t => t.Length).OrderDescending()));
    }

    private static readonly ModelCallPolicy NoWaiting = new(TimeSpan.Zero, MaxRetries: 2, Delay: (_, _) => Task.CompletedTask);

    private static HttpRequestException TooManyRequests() => new("quota", null, System.Net.HttpStatusCode.TooManyRequests);

    [Fact]
    public async Task A_provider_quota_that_persists_after_the_retries_makes_the_case_not_run_instead_of_failed()
    {
        var model = new ScriptedChatClient((_, _) => Turn.Fail(TooManyRequests()));
        await using var services = await HostAsync(_ => model, NoWaiting);
        var evalCase = CaseLoader.Parse("test.yaml", OrderCase).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.True(result.NotRun);
        Assert.False(result.Passed);
        Assert.False(result.Failed);
        Assert.Equal(3, model.Calls.Count); // the call and 2 retries, and the second turn is never sent
        Assert.Equal(ConversationRunner.ProviderQuota, result.Transcript.NotRunReason);
        var check = Assert.Single(result.Checks);
        Assert.Equal(ConversationRunner.ProviderQuota, check.Name);
        Assert.Contains("turn 1:", check.Detail);
        Assert.Equal("skipped: a previous turn failed", result.Transcript.Turns[1].Error);
        Assert.Null(result.Transcript.Turns[1].InfrastructureError);

        var summary = EvalSummary.Create([result]);
        Assert.Equal((0, 0, 1), (summary.Runs, summary.Passed, summary.NotRun.Count));
    }

    [Fact]
    public async Task A_429_that_clears_within_the_retries_is_judged_like_any_other_run()
    {
        var calls = 0;
        await using var services = await HostAsync(connection =>
        {
            var inner = OrderingModel(connection);
            // The first model call of the conversation is refused once; everything after that is the normal script.
            return new ScriptedChatClient((_, messages) => Interlocked.Increment(ref calls) == 1 ? Turn.Fail(TooManyRequests()) : inner.Script(0, messages));
        }, NoWaiting);
        var evalCase = CaseLoader.Parse("test.yaml", OrderCase).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.False(result.NotRun);
        Assert.True(result.Passed, string.Join("\n", result.Failures));
    }

    [Fact]
    public async Task A_provider_error_that_is_not_a_quota_still_fails_the_case()
    {
        await using var services = await HostAsync(
            _ => new ScriptedChatClient((_, _) => Turn.Fail(new HttpRequestException("down", null, System.Net.HttpStatusCode.BadGateway))), NoWaiting);
        var evalCase = CaseLoader.Parse("test.yaml", OrderCase).Single();

        var result = Assert.Single(await EvalRunner.RunAsync(services, [evalCase], 1, null, CancellationToken.None));

        Assert.False(result.NotRun);
        Assert.True(result.Failed);
    }
}
