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

## M2 — Agent without UI (real-model smoke done 2026-10-09, Gemini only)
- 5 tools, FallbackChatClient (429/5xx before first token), AgentService (streaming, tool rows replayed between turns, fail-closed runs, per-call cost), language detection, SSE endpoints (`/api/niches`, `/api/sessions`, `/api/sessions/{id}/messages`).
- Advisor review applied (see ADR-004 and docs/architecture.md).
- Tested with a scripted fake `IChatClient` and, on 2026-10-09, against `gemini-3.5-flash-lite`: streaming tool calling, a full Spanish booking (row in `bookings`, PendingConfirmation) and a mid-conversation language switch work. The real run found and fixed: Gemini thought signatures (ADR-013), single system message (ADR-014), malformed-function-call finish reason (ADR-010), empty answer after a tool round, fabricated customer data (code guard in `create_booking`).
- Pending: acceptance smoke against a real model (needs `LLM_PRIMARY_API_KEY`/`LLM_PRIMARY_MODEL`); verify tool calling in streaming through the OpenAI-compatible endpoint (SPEC 8).

## M3 — Trace (backend)
- `TracingChatClient` (one `LlmCall` per model call, below function invocation), `ToolCallTracer` through `FunctionInvokingChatClient.FunctionInvoker` (`ToolCall`, plus `Guardrail` and `Handoff` events), one `TraceRecorder` per run for sequence numbers, per-event cost and run totals, `TracePayload` (phone masking, 4096-character limit). See ADR-005.
- SSE `trace` event interleaved with `token`; `GET /api/sessions/{id}/trace` and `/messages`; migration `TraceEventCost` adds `trace_events.CostUsd`.
- Admin endpoints `GET /api/admin/bookings|handoffs` with `X-Admin-Key` (401 wrong or missing, 503 key not configured, `Admin:MaskPhones`). The `/admin` page is M5.
- Tests: scripted booking flow (order, sequences, tokens and cost per event and per run, masking, truncation, guardrail, handoff, failure), trace/messages/admin endpoints, SSE order.

## M6 — Security and limits (backend)
- Server-side limits on `POST /api/sessions/{id}/messages`: per client IP (30 per 10 min, sliding window), per session (25 user messages, counted from `chat_messages`) and a global daily cap (500 runs per UTC day, counted from `agent_runs`, new index `IX_agent_runs_StartedAt`); `POST /api/sessions` has its own per-IP limit (20 per 10 min). 429 body `{message, code}` with `Retry-After`; configurable with `RATE_LIMIT_*`. See ADR-006 and "Limits and abuse protection" in docs/architecture.md.
- Client IP behind a proxy: `ForwardedHeaders` with explicit `Security:TrustedProxyCidr` (default loopback and private ranges); `ClientIpHash` uses the resolved IP.
- CORS: explicit origin list, `GET`/`POST`, `Content-Type`/`Accept`/`X-Admin-Key`, no wildcard. Zero or negative limits and malformed CORS or proxy settings stop the app at startup. Startup warnings when `Admin:Key` or `Security:IpHashSalt` is blank outside Development.
- `.env.example` and `docker-compose.yml` carry every new key.
- Tests (WebApplicationFactory + Testcontainers + fake model): per-IP limit at N+1 with `Retry-After`, session limit surviving a new host, daily cap with a manual clock across midnight and restart, `X-Forwarded-For` honored only from a trusted proxy, CORS preflight allowed vs foreign origin, GET and health not limited, cost caps on every model call, prompt-injection tool calls with another business's ids.
- Advisor fixes: `Admin:MaskPhones` also masks phones inside booking notes/items and handoff reason/summary; `/api/admin/*` is rate limited per IP (`RATE_LIMIT_ADMIN_REQUESTS`, default 60 per window); an unconfigured LLM gives a non-retryable "The AI provider is not configured on this server." error.
- Real-model evals: 34/36 pass, all MVP targets met (language 100 %, forbidden phrases 100 %, tool use 100 %); see README.


## M5 — Trace panel and admin UI
- Done and covered by Vitest (94 tests in 9 files pass; verified by running `npm test`): the "Show agent trace" panel (desktop drawer, mobile bottom sheet) updated live from the SSE `trace` events and restored from `GET /api/sessions/{id}/trace`; the `/admin` page (key in `sessionStorage`, bookings and handoffs filterable by niche, link to the session trace, 401/503 states); the "Restart demo" banner on a `session_limit` 429.
- Pending: not checked by hand in a browser against a real model on desktop and mobile; no screen recording (GIF) yet.

## M6 — Evals (the limits half is above)
- Harness `evals/Chatbot.Evals` and 36 cases in 10 categories (language 7, injection 6, search 4, booking 4, availability 3, order 3, not_in_catalog 3, handoff 2, smallTalk 2, gibberish 2). Checks: tool calls and order, bookings and handoffs that really exist in the database, language, forbidden phrases, `mustMention`, order totals. Console table and `evals/results/<date>.md`. The harness itself is tested offline (`api/tests/Chatbot.Tests/Evals`) and `--fake` runs it without a model.
- Pending: **the evals have never run against a real model**, so there are no results and the MVP targets (100 % language and forbidden phrases, >= 90 % tool use) are unverified. The README has a placeholder table for them.

## M7 — README and ADRs (deploy not done)
- Done: README in SPEC 14 order, MIT `LICENSE`, ADR-007 to ADR-012 (no RAG, rules in code, SSE over fetch, provider fallback, per-call cost, tracing below function invocation), so `docs/decisions.md` has 12 ADRs.
- Pending, owner input needed: demo URL and GIF; hosting provider (undecided: SPEC 17 asks the owner); confirmation of the license holder and year (MIT, "Agustín Rojas", 2026, written without confirmation); a live Groq fallback run (only stub-tested); a decision on whether the public `/admin` stays protected; CI green on GitHub (workflow exists, no run observed from here).
