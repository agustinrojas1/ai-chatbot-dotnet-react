# Evals

Scripted conversations run against the real agent (`AgentService`, the five tools, a real PostgreSQL) and a real model. They answer "does the agent still behave?" after a prompt, tool or model change. They are run by hand, not in CI (they cost tokens and a model is not deterministic).

## Run

Against a real model (needs Docker for the throwaway PostgreSQL, unless `ConnectionStrings__Postgres` is set):

```bash
export Llm__Primary__ApiKey=<your key>
export Llm__Primary__Model=gemini-3.5-flash-lite   # a model with a ModelPricing row, or its cost shows as 0
dotnet run --project evals/Chatbot.Evals
```

`Llm__Primary__BaseUrl` defaults to Gemini's OpenAI-compatible endpoint; any other compatible endpoint works (set `Llm__Primary__BaseUrl`). `Llm__Fallback__*` is honored too. The `LLM_PRIMARY_*` names of `.env.example` also work (`set -a; source .env; set +a`).

Options (after `--`): `--filter <text>` (id contains), `--category <name>`, `--niche realestate|dental|restaurant`, `--model <name>`, `--repeat <k>` (k runs per case, because a model is not deterministic), `--min-interval-ms <n>` (see below), `--fake`, `--cases <dir>`, `--results <dir>`. Example: `dotnet run --project evals/Chatbot.Evals -- --category booking --repeat 3`.

Without a model, to check that the harness itself works:

```bash
dotnet run --project evals/Chatbot.Evals -- --fake
```

The fake model calls `search_catalog` and answers a canned sentence. Most cases fail on purpose; the report says it is a fake.

Exit code: `0` MVP targets met, `1` not met, `2` could not run (bad arguments, invalid case file, no model configured, no database), `3` targets met by the runs that were judged but some runs were not run (provider quota, see below).

## Provider rate limits

Free tiers answer `429` long before 36 conversations are over. The harness wraps the model client (below the agent pipeline; the product code is untouched) so that this does not look like a bug of the agent:

- **Spacing:** at least `--min-interval-ms` (default 2000) between the starts of two model calls. Not applied with `--fake`.
- **Retries:** a call that gets `429` before its first token waits (the provider's `Retry-After` if it sends one, otherwise 15 s, 30 s, 60 s, 60 s, 60 s) and is tried again, up to 5 times, printing one line each time. A `Retry-After` over 5 minutes (a spent daily quota) is not waited for.
- **Not run, not failed:** if the provider still refuses after the retries, the case is reported as `NOT RUN` with the reason `infrastructure: provider quota`. It is left out of the pass rates and of the MVP targets, and listed under "Not run" in the report; its tokens still count. Run those cases again later (`--filter`, `--category`) or raise `--min-interval-ms`. Any other provider error (5xx, 401...) is a normal failure.

## Output

A table in the console and `evals/results/<yyyy-MM-dd-HHmm>.md` (git-ignored) with: model and provider, the targets, pass rate by category and overall, tokens and cost, and for every failed run the transcript with the tool calls.

MVP targets (SPEC 12): 100 % of the runs with language checks pass them, 100 % of the runs pass the forbidden-phrase check, at least 90 % of the runs with tool checks pass them (tools called or not, bookings and handoffs that really exist in the database).

## Cases

`cases/*.yaml`, each file a list. A case is a fresh session with one business and the user turns in order:

```yaml
- id: order-restaurant-total-en        # unique, lowercase
  category: order                      # language | search | availability | booking | order | not_in_catalog | handoff | smallTalk | injection | gibberish
  niche: restaurant                    # realestate | dental | restaurant
  turns:
    - "I'd like 2 Margherita pizzas and 1 Tiramisu."          # a text, or a mapping with its own expectations:
    - say: "Yes, place the order."
      expectToolCalls:
        - name: create_booking
          count: 1                                            # default: at least once
          args: { customerName: Luca Rossi, items: [{ qty: 2 }] }   # partial match; text is a case/accent-insensitive substring
  expectLanguage: en                   # en | es, every reply (a turn can set its own)
  expectToolOrder: true                # the expectToolCalls happen in the listed order
  forbidToolCalls: [handoff_to_human]
  forbidPhrases: ["braces cost $"]     # case-insensitive, added to the default list
  mustMention:
    - "31"                             # a number matches by value ("$31.00" yes, "$131" no)
    - anyOf: ["pending", "pendiente"]
    - fromTool: create_booking         # a value of that tool's result in this conversation
      path: total                      # results[*].price, slots[0].label, ...
  expectBooking: true                  # exactly one booking, PendingConfirmation (forbidBooking: true: none)
  expectBookingTotal: 31               # also: an order total always has to equal the sum of catalog prices
  expectHandoff: true                  # a handoff request exists (false: none)
  rubric: free text for a future LLM judge (not used)
```

Expectations at case level cover the whole conversation; inside a turn they cover that turn (tool calls of the turn, the database after it). A typo in a key, an unknown tool or a bad value is reported with file and line before anything runs.

Every reply is also checked against `Evaluator.DefaultForbiddenPhrases`: "confirmed"/"confirmada" (a hedged mention such as "not confirmed yet" or "once it is confirmed" is allowed), promised emails, fragments of the system prompt and tool names.

## Notes on what the checks can and cannot tell

- The language check reuses the agent's `LanguageDetector` (stopword heuristic) after removing the names of the business, offerings and neighbourhoods. It fails a reply that reads as the other language; a reply with no evidence either way passes.
- The seeded catalog is in English, so "Spanish message, English catalog" is covered directly; "English message, Spanish data" is approximated by English messages that name Spanish places (Núñez).
- `mustMention`, `forbidPhrases` and the language check are text heuristics. Anything they cannot judge (tone, whether a refusal is polite) is what `rubric` is for.
- A booking case depends on the model choosing a slot from `check_availability`; dates are relative to the day of the run ("tomorrow" can be a Sunday, when the clinic has no slots).
