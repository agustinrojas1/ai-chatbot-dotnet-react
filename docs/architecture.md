# Architecture

See SPEC.md section 5 for the diagram. This file documents what the code does where the SPEC leaves room: the message pipeline and the SSE contract.

## Message pipeline

`POST /api/sessions/{id}/messages` calls `AgentService.RunAsync`, which yields `AgentEvent` records; the endpoint maps each one to an SSE event (`Endpoints/SseWriter.cs`). The model client is built per request, outermost first:

```
FunctionInvokingChatClient   runs the tools, at most MaxIterations tool rounds; its FunctionInvoker hook records every tool call
  -> TracingChatClient       sees every individual model call, so it sits below function invocation
    -> keyed singleton "llm" (FallbackChatClient: primary provider, one retry on the fallback before the first token)
```

Messages sent to the model, in order: the business system prompt (persona, common rules, current time, session status), the history, and **last** a system message with the language instruction detected for this turn. The history is the last `MaxHistoryMessages` user/assistant messages plus the tool exchanges stored since the oldest of them, so an id a tool returned in one turn (a `slotId` offered, then confirmed with "yes" two turns later) is still in the model's context.

What is stored per run, in `chat_messages`: the user message (`User`), one row per executed tool call (`Tool`: JSON `{callId, name, arguments, result}` with arguments and result truncated to 2000 characters), and the final assistant text (`Assistant`). The rows of one run are timestamped one microsecond apart so they replay in order. A `Tool` row replays as an assistant tool call followed by its result, so a call is never sent without its result. `Tool` rows are model context only: the chat UI and any message listing must skip them. They are also kept when the run fails after a tool already acted (a booking, a handoff), so the next turn knows.

Limits come from the `Agent` configuration section: `MaxInputChars` (1000), `MaxHistoryMessages` (20), `MaxIterations` (5) and `MaxOutputTokens` (800).

Failure handling: a message is rejected *before* streaming (JSON problem details, no run is created) or the stream is opened and every later problem is an `error` event. The `AgentRun` row is created as `Error` and only becomes `Ok` when the answer is stored, so a run that dies half way (process killed, database down) never looks successful.

| Cause | Run | Trace event | `error.retryable` |
|---|---|---|---|
| Provider failure, empty answer, unexpected exception | `Error` | `Error` `agent_error` | yes |
| `MaxIterations` exhausted | `Error` | `Guardrail` `iteration_limit` | no |
| Client disconnected | `Error` | `Error` `agent_cancelled` | (nobody to tell) |

If a tool handed the conversation to a person before the failure, `handoff` is still sent, right before `error`.

A tool call whose arguments do not fit the tool's parameters (wrong JSON shape) does not end the run: the exception text goes back to the model as the tool result (`IncludeDetailedErrors`), so it can correct the call, for up to 3 failing rounds in a row, then the run ends with the generic error. Exception text never reaches the user stream.

Cost: every model call is priced with the `ModelPricing` row of the model that answered it (`llm.model` on each update), because the fallback provider can answer one call of a run and the primary another. `AgentRun.Model` and the `model`/`provider` of `run_completed` list the models that answered in order of first use, comma separated (`gemini-3.5-flash-lite, llama-3.3-70b-versatile`); with no fallback it is one name.

## HTTP errors (before streaming)

| Status | When |
|---|---|
| 400 | Empty text, text longer than `MaxInputChars` (validation problem on `text`), unknown niche |
| 404 | Unknown session |
| 409 | Session is `Closed` |

All of them are `application/problem+json`. A session in `AwaitingHuman` still accepts messages: the agent tells the customer that a person will continue.

## SSE contract

The response of `POST /api/sessions/{id}/messages` is `text/event-stream` with `Cache-Control: no-cache` and `X-Accel-Buffering: no`. Each event is `event: <name>` and `data: <one line of camelCase JSON>`, followed by a blank line, and is flushed as soon as it is written. Use `fetch` with a `ReadableStream`: `EventSource` cannot POST.

A normal run always starts with `run_started` and ends with `run_completed` or `error`:

```
event: run_started
data: {"runId":"019ce9a4-3c1e-7d52-8d0b-2f6a1c0e9b11"}

event: token
data: {"text":"Sure! "}

event: token
data: {"text":"A cleaning costs *USD 60*."}

event: run_completed
data: {"runId":"019ce9a4-3c1e-7d52-8d0b-2f6a1c0e9b11","tokensIn":1840,"tokensOut":96,"costUsd":0.000792,"durationMs":2310,"model":"gemini-3.5-flash-lite","provider":"gemini"}
```

`token.text` pieces are concatenated by the client in arrival order. They include any text the model wrote before a tool call. Tokens and cost add up over every model call of the run.

When the model hands the conversation to a person, `handoff` arrives after the last token and before `run_completed`. The session is then `AwaitingHuman`:

```
event: handoff
data: {"reason":"customer asked for a person"}
```

When the run fails, the stream ends with `error` instead of `run_completed`. `message` is safe to show; `retryable` says whether sending the same message again can help (provider rate limit or outage: yes; iteration limit: no):

```
event: run_started
data: {"runId":"019ce9a4-3c1e-7d52-8d0b-2f6a1c0e9b11"}

event: error
data: {"message":"The assistant is at capacity right now (demo token quota). Please try again in a few minutes.","retryable":true}
```

When a step of the run completes, the server sends a `trace` event (next section). Clients must ignore event names they do not know.

## Trace

Every step of a run is a trace event. One `TraceRecorder` per run (`Agent/Tracing`) allocates their `sequence` (0, 1, 2... in the order the steps complete, no gaps), sanitizes their payloads and keeps the run totals. Events are sent over SSE as soon as the step completes, and stored with the run when it closes (so `/trace` shows a run once it ended; a process killed mid-run leaves its row as `Error` with no events).

| `type` | `name` | `input` | `output` |
|---|---|---|---|
| `LlmCall` | model that answered (`unknown` if the call failed before any update) | `{ messageCount, messages: [{ role, text }] }`: the last 4 messages sent, without the leading business system prompt (`messageCount` counts everything), each text clipped to 1000 characters | `{ provider, model, text, toolCalls: ["tool name"], finishReason }` (`text` clipped to 2500 characters), or `{ provider, model, error, status? }` when the call failed or was abandoned |
| `ToolCall` | tool name | the arguments the model sent | the result the model got, or `{ error: "ExceptionType" }` if the tool threw |
| `Guardrail` | the rule: `slot_full`, `slot_in_past`, `session_awaiting_human`, `session_closed`, `range_too_long`, `missing_target`, any `invalid_*` (tool rejections, right after their `ToolCall`), or `iteration_limit` (closes a run that hit `MaxIterations`) | `null` (`{ textLength }` for `iteration_limit`) | `{ tool, code, message }` (`{ code, maxIterations }` for `iteration_limit`) |
| `Handoff` | `handoff_to_human` | `null` | `{ reason, handoffId, alreadyRequested }`, right after the successful `ToolCall` |
| `Error` | `agent_error` or `agent_cancelled`, the last event of a failed run | `{ textLength }` | `{ error, status? }` or `{ code }` |

An event is `{ runId, sequence, type, name, input, output, tokensIn, tokensOut, durationMs, costUsd }` (camelCase, nulls kept). `tokensIn`, `tokensOut` and `costUsd` are set on `LlmCall` events when the provider reported usage and are `null` on the rest. `costUsd` is `tokens x ModelPricing` of the model that answered that call (0 if the model has no pricing row), rounded to 8 decimals. The provider of a call is `output.provider` (the fallback provider can answer one call and the primary another).

```
event: trace
data: {"runId":"019ce9a4-3c1e-7d52-8d0b-2f6a1c0e9b11","sequence":1,"type":"ToolCall","name":"search_catalog","input":{"query":"cleaning"},"output":{"offerings":[...]},"tokensIn":null,"tokensOut":null,"durationMs":14,"costUsd":null}

event: trace
data: {"runId":"019ce9a4-3c1e-7d52-8d0b-2f6a1c0e9b11","sequence":2,"type":"LlmCall","name":"gemini-3.5-flash-lite","input":{"messageCount":3,"messages":[...]},"output":{"provider":"gemini","model":"gemini-3.5-flash-lite","text":"A cleaning costs...","toolCalls":[],"finishReason":"stop"},"tokensIn":500,"tokensOut":30,"durationMs":820,"costUsd":0.000225}
```

Order on the stream: a model call is complete when its stream ends, so a run with one tool is `run_started`, `trace` (LlmCall that asked for the tool), `trace` (ToolCall), `token`..., `trace` (final LlmCall), `run_completed`. Steps that finished before a token was produced are always sent before that token; the last LlmCall comes after the last token. A failed run sends its failed `LlmCall` and the closing `Error`/`Guardrail` event before `error`.

Run totals are the sum of the LlmCall events (`run_completed.costUsd` = sum of their `costUsd`).

**Privacy.** What is stored and sent in a trace is sanitized: phone numbers in any string are masked (`+54 11 5555-4444` becomes `+54 11 ****-4444`) and a payload longer than 4096 characters of JSON is replaced by its first 4096 characters as a JSON string ending in `…[truncated]`. The `Booking` table keeps the full phone. See ADR-005.

## Restoring a session

`GET /api/sessions/{id}/messages` (404 for an unknown session) returns what the chat needs after a reload. `Tool` rows are never listed. The text is the stored text (not masked: it is the visitor's own conversation):

```json
{
  "messages": [{ "id": "...", "role": "user", "text": "Hi", "createdAt": "2026-10-09T20:15:00+00:00" },
               { "id": "...", "role": "assistant", "text": "Hello!", "createdAt": "..." }],
  "sessionStatus": "Active",
  "language": "en"
}
```

`sessionStatus` is `Active`, `AwaitingHuman` or `Closed`; `language` is `en`, `es` or `null` before the first message.

`GET /api/sessions/{id}/trace` (404 for an unknown session) returns the runs of the session by start time, each with its events in `sequence` order (the same JSON as the SSE `trace` event). `provider` lists the providers of the LlmCall events in order of first use; `userMessage` is the message that started the run, with phones masked. A run that is still streaming reads `Error` until it completes.

```json
{
  "sessionId": "...",
  "runs": [{
    "id": "...", "userMessage": "How much is a cleaning?", "status": "Ok",
    "model": "gemini-3.5-flash-lite", "provider": "gemini",
    "tokensIn": 900, "tokensOut": 50, "costUsd": 0.000345, "durationMs": 1630,
    "startedAt": "2026-10-09T20:15:01+00:00",
    "events": [{ "runId": "...", "sequence": 0, "type": "LlmCall", "name": "gemini-3.5-flash-lite", "input": {}, "output": {}, "tokensIn": 400, "tokensOut": 20, "durationMs": 700, "costUsd": 0.00017 }]
  }]
}
```

## Admin

`GET /api/admin/bookings?niche=` and `GET /api/admin/handoffs?niche=` need the header `X-Admin-Key` (a demo-grade shared key, compared in constant time). `niche` is `realestate`, `dental` or `restaurant`; missing means all, an unknown id is a 400. Lists are newest first, at most 200 items.

| Status | When |
|---|---|
| 200 | Correct key |
| 401 | Header missing or wrong (problem details) |
| 503 | `Admin:Key` (`ADMIN_KEY`) is blank: the endpoints are disabled and say so, they never open |

`Admin:MaskPhones` (`ADMIN_MASK_PHONES`, default `true`) masks `customerPhone` in the bookings list; with `false` and the correct key the full number is returned.

```
curl -H 'X-Admin-Key: $ADMIN_KEY' 'http://localhost:8080/api/admin/bookings?niche=dental'
[{ "id": "...", "niche": "dental", "sessionId": "...", "status": "PendingConfirmation",
   "customerName": "Ana Perez", "customerPhone": "+54 11 ****-4444",
   "slotStartsAt": "2026-10-12T14:00:00+00:00", "offeringName": "Teeth cleaning",
   "items": null, "total": null, "notes": "First visit", "createdAt": "2026-10-09T20:16:00+00:00" }]

curl -H 'X-Admin-Key: $ADMIN_KEY' 'http://localhost:8080/api/admin/handoffs'
[{ "id": "...", "niche": "dental", "sessionId": "...", "reason": "customer asked for a person",
   "summary": "Wants to talk to staff.", "status": "Open", "createdAt": "2026-10-09T20:20:00+00:00" }]
```

## Session creation

`POST /api/sessions` with `{"niche":"dental"}` returns `201` and `{"sessionId":"...","greeting":"...","quickReplies":["..."]}`. Niche ids are `realestate`, `dental` and `restaurant`. The greeting is not stored as a chat message. `ChatSession.ClientIpHash` is the first 32 hex characters of `SHA-256("{Security:IpHashSalt}:{remote IP}")`, so the IP itself is never stored.
