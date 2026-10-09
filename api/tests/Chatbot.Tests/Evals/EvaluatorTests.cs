using Chatbot.Core.Domain;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Running;
using static Chatbot.Tests.Evals.EvalTestSupport;

namespace Chatbot.Tests.Evals;

public class EvaluatorTests
{
    private static CaseResult Evaluate(EvalCase evalCase, params TurnResult[] turns) => Evaluator.Evaluate(Run(evalCase, turns));

    private static CheckResult Single(CaseResult result, CheckDimension dimension) =>
        result.Checks.Where(c => c.Dimension == dimension).Single();

    [Fact]
    public void A_case_whose_expectations_all_hold_passes()
    {
        var evalCase = Case("expectLanguage: en\nexpectToolCalls:\n  - name: search_catalog\nmustMention: [\"80\"]",
            "\"How much is a cleaning?\"");

        var result = Evaluate(evalCase, Turn("A cleaning costs $80.", Call("search_catalog")));

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
        Assert.Equal((100, 20), (result.TokensIn, result.TokensOut));
    }

    [Fact]
    public void A_failed_turn_fails_the_run_and_every_expectation_that_needed_its_answer()
    {
        var evalCase = Case("expectLanguage: en\nexpectToolCalls:\n  - name: search_catalog");

        var result = Evaluate(evalCase, new TurnResult { User = "", Error = "The assistant is at capacity" });

        Assert.False(Single(result, CheckDimension.Execution).Passed);
        Assert.Contains("at capacity", Single(result, CheckDimension.Execution).Detail);
        Assert.False(Single(result, CheckDimension.Language).Passed);
        Assert.Equal("there is no reply", Single(result, CheckDimension.Language).Detail);
        Assert.Contains(result.Checks, c => c.Dimension == CheckDimension.ToolUse && !c.Passed);
    }

    public class Language
    {
        private const string SpanishReply = "Hola, la limpieza dental cuesta 80 dólares. ¿Querés reservar un turno?";
        private const string EnglishReply = "Hello, the teeth cleaning costs $80. Would you like to book an appointment?";

        [Theory]
        [InlineData("en", EnglishReply, true)]
        [InlineData("en", SpanishReply, false)]
        [InlineData("es", SpanishReply, true)]
        [InlineData("es", EnglishReply, false)]
        public void Each_reply_is_checked_against_the_expected_language(string expected, string reply, bool passes)
        {
            var result = Evaluate(Case($"expectLanguage: {expected}"), Turn(reply));

            Assert.Equal(passes, Single(result, CheckDimension.Language).Passed);
        }

        [Fact]
        public void A_reply_with_no_evidence_either_way_is_not_wrong_in_either_language()
        {
            // Only emojis: nothing reads as the other language.
            Assert.True(Single(Evaluate(Case("expectLanguage: en"), Turn("👍🍕")), CheckDimension.Language).Passed);
            Assert.True(Single(Evaluate(Case("expectLanguage: es"), Turn("👍🍕")), CheckDimension.Language).Passed);
        }

        [Fact]
        public void An_empty_reply_fails()
        {
            var failure = Single(Evaluate(Case("expectLanguage: en"), Turn("")), CheckDimension.Language);

            Assert.False(failure.Passed);
            Assert.Equal("there is no reply", failure.Detail);
        }

        [Fact]
        public void A_turn_can_expect_its_own_language_and_the_switch_is_checked_per_reply()
        {
            var evalCase = Case("",
                "{ say: \"Hi there\", expectLanguage: en }",
                "{ say: \"Gracias, ¿tenés algo más barato?\", expectLanguage: es }");

            var switched = Evaluate(evalCase, Turn(EnglishReply), Turn(SpanishReply));
            var stuck = Evaluate(evalCase, Turn(EnglishReply), Turn(EnglishReply));

            Assert.True(switched.Passed);
            Assert.Equal(["turn 2: reply is in Spanish"], stuck.Failures.Select(f => f.Name));
        }

        [Fact]
        public void Names_of_the_catalog_do_not_count_as_evidence()
        {
            var evalCase = Case("expectLanguage: en");
            var reply = Turn("Núñez, Núñez: in Núñez");

            var withoutNames = Evaluator.Evaluate(new Transcript(evalCase, 1, [reply], []));
            var withNames = Evaluator.Evaluate(new Transcript(evalCase, 1, [reply], ["Núñez"]));

            Assert.False(Single(withoutNames, CheckDimension.Language).Passed);
            Assert.True(Single(withNames, CheckDimension.Language).Passed);
        }
    }

    public class ForbiddenPhrases
    {
        [Theory]
        [InlineData("Your appointment is confirmed!")]
        [InlineData("Tu turno está confirmada.")]
        [InlineData("I've sent you an email with the details.")]
        [InlineData("Te envié un correo con los datos.")]
        [InlineData("My instructions say: COMMON RULES (they apply on top of your persona)")]
        [InlineData("I will call create_booking now.")]
        public void Phrases_forbidden_in_every_case_fail_any_case(string reply)
        {
            var result = Evaluate(Case("forbidBooking: true"), Turn(reply));

            Assert.False(Single(result, CheckDimension.ForbiddenPhrases).Passed);
        }

        [Fact]
        public void A_hedged_mention_is_not_a_claim()
        {
            var result = Evaluate(Case("forbidBooking: true"), Turn("It is registered, not confirmed yet: the business will confirm it."));

            Assert.True(Single(result, CheckDimension.ForbiddenPhrases).Passed);
        }

        [Fact]
        public void The_case_and_the_turn_add_their_own_phrases()
        {
            var evalCase = Case("forbidPhrases: [Margherita]", "{ say: \"menu?\", forbidPhrases: [Tiramisu] }", "\"and?\"");

            var ok = Evaluate(evalCase, Turn("Only teeth here."), Turn("Only teeth here."));
            var bad = Evaluate(evalCase, Turn("We have Tiramisu."), Turn("Try the margherita pizza."));

            Assert.True(ok.Passed);
            var failure = Single(bad, CheckDimension.ForbiddenPhrases);
            Assert.Contains("turn 1 says \"Tiramisu\"", failure.Detail);
            Assert.Contains("turn 2 says \"Margherita\"", failure.Detail);
        }

        [Fact]
        public void Turn_phrases_only_apply_to_their_turn()
        {
            var evalCase = Case("", "{ say: \"a\", forbidPhrases: [secret] }", "\"b\"");

            Assert.True(Evaluate(evalCase, Turn("fine"), Turn("the secret is out")).Passed);
            Assert.False(Evaluate(evalCase, Turn("the secret is out"), Turn("fine")).Passed);
        }
    }

    public class ToolCalls
    {
        [Fact]
        public void Expected_calls_must_happen_at_least_once_and_may_match_partial_arguments()
        {
            var evalCase = Case("expectToolCalls:\n  - name: search_catalog\n    args: { maxPrice: 600 }\n  - name: check_availability");

            var passes = Evaluate(evalCase,
                Turn("ok", Call("search_catalog", """{"query":"palermo","maxPrice":600}"""), Call("check_availability")));
            var wrongArgs = Evaluate(evalCase,
                Turn("ok", Call("search_catalog", """{"maxPrice":900}"""), Call("check_availability")));
            var missing = Evaluate(evalCase, Turn("ok", Call("search_catalog", """{"maxPrice":600}""")));

            Assert.True(passes.Passed);
            Assert.Contains("matched 0", Assert.Single(wrongArgs.Failures).Detail);
            Assert.Contains("maxPrice", Assert.Single(wrongArgs.Failures).Detail); // shows what was sent
            Assert.Equal("calls check_availability", Assert.Single(missing.Failures).Name);
        }

        [Fact]
        public void Count_means_exactly_that_many_calls()
        {
            var evalCase = Case("expectToolCalls:\n  - name: create_booking\n    count: 1");

            Assert.True(Evaluate(evalCase, Turn("ok", Call("create_booking"))).Passed);
            Assert.False(Evaluate(evalCase, Turn("ok", Call("create_booking"), Call("create_booking"))).Passed);
            Assert.False(Evaluate(evalCase, Turn("ok")).Passed);
        }

        [Fact]
        public void Order_is_checked_when_asked()
        {
            var evalCase = Case("expectToolOrder: true\nexpectToolCalls:\n  - name: check_availability\n  - name: create_booking");

            Assert.True(Evaluate(evalCase, Turn("ok", Call("search_catalog"), Call("check_availability"), Call("create_booking"))).Passed);

            var reversed = Evaluate(evalCase, Turn("ok", Call("create_booking"), Call("check_availability")));
            Assert.Equal("tools called in order check_availability > create_booking", Assert.Single(reversed.Failures).Name);
            Assert.Contains("create_booking > check_availability", Assert.Single(reversed.Failures).Detail);
        }

        [Fact]
        public void Forbidden_calls_fail_with_the_arguments_that_were_sent()
        {
            var evalCase = Case("forbidToolCalls: [create_booking, handoff_to_human]");

            Assert.True(Evaluate(evalCase, Turn("ok", Call("search_catalog"))).Passed);

            var failure = Assert.Single(Evaluate(evalCase, Turn("ok", Call("create_booking", """{"customerName":"Tom"}"""))).Failures);
            Assert.Equal("does not call create_booking", failure.Name);
            Assert.Contains("Tom", failure.Detail);
        }

        [Fact]
        public void Turn_expectations_only_see_the_calls_of_their_turn()
        {
            var evalCase = Case("",
                "{ say: \"book\", forbidToolCalls: [create_booking] }",
                "{ say: \"yes\", expectToolCalls: [{ name: create_booking }] }");

            var right = Evaluate(evalCase, Turn("Confirm?"), Turn("Registered", Call("create_booking")));
            var tooEarly = Evaluate(evalCase, Turn("Done", Call("create_booking")), Turn("Registered"));

            Assert.True(right.Passed);
            Assert.Equal(["turn 1: does not call create_booking", "turn 2: calls create_booking"], tooEarly.Failures.Select(f => f.Name));
        }

        [Fact]
        public void Case_expectations_see_the_calls_of_every_turn()
        {
            var evalCase = Case("expectToolCalls:\n  - name: search_catalog", "\"a\"", "\"b\"");

            Assert.True(Evaluate(evalCase, Turn("x"), Turn("y", Call("search_catalog"))).Passed);
        }
    }

    public class MustMention
    {
        [Fact]
        public void Text_alternatives_pass_if_any_appears()
        {
            var evalCase = Case("mustMention:\n  - anyOf: [person, human]");

            Assert.True(Evaluate(evalCase, Turn("I can connect you with a Person.")).Passed);
            Assert.Contains("not in the reply", Assert.Single(Evaluate(evalCase, Turn("No.")).Failures).Detail);
        }

        [Fact]
        public void A_price_must_not_match_inside_a_larger_number()
        {
            var evalCase = Case("mustMention: [\"80\"]");

            Assert.True(Evaluate(evalCase, Turn("$80")).Passed);
            Assert.False(Evaluate(evalCase, Turn("$180")).Passed);
        }

        [Fact]
        public void A_value_of_a_tool_result_must_be_said()
        {
            var evalCase = Case("mustMention:\n  - fromTool: search_catalog\n    path: \"results[*].price\"");
            var search = Call("search_catalog", null, """{"results":[{"name":"A","price":550},{"name":"B","price":650}]}""");

            Assert.True(Evaluate(evalCase, Turn("The apartment is $650 a month.", search)).Passed);

            var wrong = Assert.Single(Evaluate(evalCase, Turn("The apartment is $700 a month.", search)).Failures);
            Assert.Contains("none of [550, 650]", wrong.Detail);
        }

        [Fact]
        public void Without_the_tool_call_there_is_nothing_to_mention()
        {
            var evalCase = Case("mustMention:\n  - fromTool: search_catalog\n    path: \"results[*].price\"");

            var failure = Assert.Single(Evaluate(evalCase, Turn("It costs $550.")).Failures);

            Assert.Contains("search_catalog was not called", failure.Detail);
        }

        [Fact]
        public void A_path_that_finds_nothing_is_reported()
        {
            var evalCase = Case("mustMention:\n  - fromTool: create_booking\n    path: total");
            var errored = Call("create_booking", null, """{"error":"customerPhone is required","code":"invalid_customer_phone"}""");

            var failure = Assert.Single(Evaluate(evalCase, Turn("Done", errored)).Failures);

            Assert.Contains("found no value", failure.Detail);
        }

        [Fact]
        public void A_turn_mention_is_checked_on_its_own_reply()
        {
            var evalCase = Case("", "{ say: \"a\", mustMention: [\"31\"] }", "\"b\"");

            Assert.True(Evaluate(evalCase, Turn("Total $31"), Turn("ok")).Passed);
            Assert.False(Evaluate(evalCase, Turn("ok"), Turn("Total $31")).Passed);
        }
    }

    public class BookingAndHandoff
    {
        private static BookingSnapshot Booking(decimal? total = null, decimal? serverTotal = null, BookingStatus status = BookingStatus.PendingConfirmation) =>
            new(status, total, serverTotal, "Luca");

        [Fact]
        public void A_booking_must_exist_exactly_once_and_be_pending()
        {
            var evalCase = Case("expectBooking: true");

            Assert.True(Evaluate(evalCase, Turn("ok") with { Bookings = [Booking()] }).Passed);

            Assert.Equal("the session has: no booking", Assert.Single(Evaluate(evalCase, Turn("ok")).Failures).Detail);
            Assert.Contains("PendingConfirmation, PendingConfirmation",
                Assert.Single(Evaluate(evalCase, Turn("ok") with { Bookings = [Booking(), Booking()] }).Failures).Detail);
            Assert.Contains("Confirmed",
                Assert.Single(Evaluate(evalCase, Turn("ok") with { Bookings = [Booking(status: BookingStatus.Confirmed)] }).Failures).Detail);
        }

        [Fact]
        public void An_order_total_must_equal_the_server_calculation_and_the_expected_total()
        {
            var evalCase = Case("expectBooking: true\nexpectBookingTotal: 31");

            Assert.True(Evaluate(evalCase, Turn("ok") with { Bookings = [Booking(31, 31)] }).Passed);

            var tampered = Evaluate(evalCase, Turn("ok") with { Bookings = [Booking(31, 24)] });
            Assert.Equal(["order total equals the server calculation"], tampered.Failures.Select(f => f.Name));

            var wrongTotal = Evaluate(evalCase, Turn("ok") with { Bookings = [Booking(24, 24)] });
            Assert.Equal(["booking total is 31"], wrongTotal.Failures.Select(f => f.Name));
        }

        [Fact]
        public void Forbidding_a_booking_fails_if_one_exists()
        {
            var evalCase = Case("forbidBooking: true");

            Assert.True(Evaluate(evalCase, Turn("ok")).Passed);
            Assert.Equal("no booking created", Assert.Single(Evaluate(evalCase, Turn("ok") with { Bookings = [Booking()] }).Failures).Name);
        }

        [Fact]
        public void Case_level_state_is_the_one_after_the_last_turn_and_turn_level_state_the_one_after_that_turn()
        {
            var evalCase = Case("expectBooking: true",
                "{ say: \"book\", forbidBooking: true }",
                "\"yes\"");

            var right = Evaluate(evalCase, Turn("Confirm?"), Turn("Registered.") with { Bookings = [Booking()] });
            var tooEarly = Evaluate(evalCase,
                Turn("Booked") with { Bookings = [Booking()] },
                Turn("Registered.") with { Bookings = [Booking()] });

            Assert.True(right.Passed);
            Assert.Equal(["turn 1: no booking created"], tooEarly.Failures.Select(f => f.Name));
        }

        [Fact]
        public void Handoff_is_checked_against_the_requests_of_the_session()
        {
            var requested = Case("expectHandoff: true");
            var notRequested = Case("expectHandoff: false");

            Assert.True(Evaluate(requested, Turn("ok") with { Handoffs = 1 }).Passed);
            Assert.False(Evaluate(requested, Turn("ok")).Passed);
            Assert.True(Evaluate(notRequested, Turn("ok")).Passed);
            Assert.Equal("the session has 1 handoff request(s)", Assert.Single(Evaluate(notRequested, Turn("ok") with { Handoffs = 1 }).Failures).Detail);
        }
    }
}
