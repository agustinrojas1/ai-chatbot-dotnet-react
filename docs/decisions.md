# Decisions

Short ADRs: context, decision, alternatives discarded.

## ADR-001: Monorepo with Core / Infrastructure / Api split
- **Context:** The agent logic (tools, tracing, language detection) should be testable without the web host.
- **Decision:** `Chatbot.Core` (domain, agent, tools, no web deps), `Chatbot.Infrastructure` (EF Core, LLM client, seed), `Chatbot.Api` (endpoints, DI). Revisit and merge Core + Infrastructure if the split proves excessive.
- **Alternatives:** single project (simpler, but the agent would be tied to ASP.NET and EF).

## ADR-002: Slot capacity via an atomic conditional UPDATE
- **Context:** Two visitors can try to take the last seat of a slot at the same time. A read-check-write in application code (or the model deciding "there is room") would oversell it.
- **Decision:** Capacity is enforced by PostgreSQL in two layers. (1) `SlotBooking.TryReserveAsync` runs a single statement, `UPDATE availability_slots SET "Booked" = "Booked" + 1 WHERE "Id" = @id AND "Booked" < "Capacity"`, and returns `true` only if one row was updated. The row lock serializes concurrent callers, so exactly `Capacity` of them win. (2) CHECK constraints `ck_availability_slots_booked_lte_capacity`, `..._booked_non_negative` and `..._capacity_positive` make an invalid state impossible even if some code path bypasses the method. Idempotency is a unique index on `Booking.IdempotencyKey`; `TraceEvent.Type` is a string column with a CHECK over the closed enum. Verified by concurrency tests (10 parallel callers, capacity 1 and 3) against a real PostgreSQL 17 (Testcontainers).
- **Alternatives:** optimistic concurrency with a row version (needs a retry loop and surfaces `DbUpdateConcurrencyException` to the tool for a case that is just "slot full"); `SELECT ... FOR UPDATE` in a transaction (two round trips and a longer lock); an in-process lock or semaphore (breaks with more than one API instance, and the database would not be the source of truth).
- **Consequences:** `Booked` must only change through `TryReserveAsync` (and a future release method that decrements with `WHERE "Booked" > 0`). Reserving the seat and inserting the `Booking` must share one transaction so a failed insert (e.g. duplicate idempotency key) rolls the seat back.

## ADR-003: Data model conventions
- **Ids:** `Guid` everywhere, generated client-side as UUIDv7 (`Guid.CreateVersion7()`), so ids are time-ordered, index-friendly and can be assigned before `SaveChanges` (the seed can build whole graphs with fixed ids). `ModelPricing` is keyed by `Model`.
- **Time:** `DateTimeOffset` in UTC, mapped to `timestamptz`.
- **Enums:** stored as strings (readable rows, safe to reorder the enum).
- **jsonb:** `JsonElement` (nullable where optional) for `Offering.Attributes`, `Booking.Items` and `TraceEvent.Input/Output`; `List<string>` for `Business.QuickReplies`. Natively supported by Npgsql with no converters, keeps Core free of EF, and `EF.Functions.JsonContains` (backed by a GIN index on `Attributes`) covers the `search_catalog` filters.

## ADR-004: Tools, agent service, tracing and LLM client live in Infrastructure
- **Context:** ADR-001 planned the agent and its tools in `Chatbot.Core`. But every tool needs the database (`ChatbotDbContext`, the atomic `SlotBooking.TryReserveAsync`, a transaction around seat + booking), and the tracing layer persists `TraceEvent`s. Putting them in Core would force Core to reference EF Core, or hide the database behind repository interfaces that would only exist to be mocked.
- **Decision:** `Chatbot.Core` keeps only pure, dependency-free logic: the domain, language detection, phone masking, cost calculation and prompt text (`Chatbot.Core.Agent`). Tools (`AgentToolFactory`, `ToolContext`), `AgentService`, the tracing `DelegatingChatClient`s and the LLM client with its fallback (`Chatbot.Infrastructure.Agent`, `Chatbot.Infrastructure.Llm`) live in `Chatbot.Infrastructure`. The tools are tested against a real PostgreSQL (Testcontainers), not an in-memory fake, because the rules they must guarantee (capacity, idempotency, jsonb filters) are database behaviour.
- **Alternatives:** tools in Core behind `IBookingRepository`-style interfaces (more indirection, and the concurrency guarantee would be tested against a fake that cannot prove it); merging Core and Infrastructure now (ADR-001 still keeps the split because the pure logic is the part worth unit-testing without a container).
- **Consequences:** Core never references EF Core or Microsoft.Extensions.AI. The tools take the business and session from a server-built `ToolContext`, never from model arguments, and are created per request around that request's single `DbContext`.
