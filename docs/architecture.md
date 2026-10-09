# Architecture

See SPEC.md section 5 for the diagram. This file documents what the code does where the SPEC leaves room: the message pipeline and the SSE contract.

## Message pipeline

`POST /api/sessions/{id}/messages` calls `AgentService.RunAsync`, which yields `AgentEvent` records; the endpoint maps each one to an SSE event (`Endpoints/SseWriter.cs`). The model client is built per request, outermost first:

```
FunctionInvokingChatClient   runs the tools, at most MaxIterations tool rounds
  -> (TracingChatClient)     M3: sees every individual model call, so it sits below function invocation
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

The `trace` event of SPEC 10 is added with the tracing milestone; clients must ignore event names they do not know.

## Session creation

`POST /api/sessions` with `{"niche":"dental"}` returns `201` and `{"sessionId":"...","greeting":"...","quickReplies":["..."]}`. Niche ids are `realestate`, `dental` and `restaurant`. The greeting is not stored as a chat message. `ChatSession.ClientIpHash` is the first 32 hex characters of `SHA-256("{Security:IpHashSalt}:{remote IP}")`, so the IP itself is never stored.
