# Progress

## M0 — Skeleton
- Monorepo layout per SPEC section 5; .NET 10 solution and Vite app compile.
- `docker compose up`: postgres, api, web.
- GitHub Actions CI for api and web.

## M1 — Data
- 10 entities, initial migration, DB-enforced invariants (unique idempotency key, slot capacity CHECKs, atomic `TryReserveAsync`).
- Idempotent seed: 3 businesses, 28 offerings (v1 items ported + invented extras, see `docs/seed-notes.md`), rolling 14-day slots.
- Tests (Testcontainers): invariants, concurrency on last slot, seeder idempotency.
- Pending: persona prompts hold business facts until a Business column exists; seeder never updates existing rows.

## M2 — Agent without UI (real-model smoke PENDING)
- 5 tools, FallbackChatClient (429/5xx before first token), AgentService (streaming, tool rows replayed between turns, fail-closed runs, per-call cost), language detection, SSE endpoints (`/api/niches`, `/api/sessions`, `/api/sessions/{id}/messages`).
- Advisor review applied (see ADR-004 and docs/architecture.md).
- Tested with a scripted fake `IChatClient`; curl against the real endpoint returns a clean `error` event without an API key.
- Pending: acceptance smoke against a real model (needs `LLM_PRIMARY_API_KEY`/`LLM_PRIMARY_MODEL`); verify tool calling in streaming through the OpenAI-compatible endpoint (SPEC 8).

## M3 — Trace (backend)
- `TracingChatClient` (one `LlmCall` per model call, below function invocation), `ToolCallTracer` through `FunctionInvokingChatClient.FunctionInvoker` (`ToolCall`, plus `Guardrail` and `Handoff` events), one `TraceRecorder` per run for sequence numbers, per-event cost and run totals, `TracePayload` (phone masking, 4096-character limit). See ADR-005.
- SSE `trace` event interleaved with `token`; `GET /api/sessions/{id}/trace` and `/messages`; migration `TraceEventCost` adds `trace_events.CostUsd`.
- Admin endpoints `GET /api/admin/bookings|handoffs` with `X-Admin-Key` (401 wrong or missing, 503 key not configured, `Admin:MaskPhones`). The `/admin` page is M5.
- Tests: scripted booking flow (order, sequences, tokens and cost per event and per run, masking, truncation, guardrail, handoff, failure), trace/messages/admin endpoints, SSE order.
