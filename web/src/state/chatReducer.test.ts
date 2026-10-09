import { describe, expect, it } from 'vitest'
import type { Niche, TraceEvent, TraceResponse } from '../api/types'
import {
  canSend,
  canType,
  chatReducer,
  HANDOFF_TEXT,
  initialChatState,
  runTotals,
  type ChatAction,
  type ChatState,
} from './chatReducer'

const AT = '2026-01-01T10:00:00.000Z'
const niche: Niche = {
  id: 'dental',
  name: 'Dental Clinic',
  persona: 'Alex',
  headerName: 'Dr. Smile Assistant',
  avatar: { emoji: '🦷', letter: 'D', colors: ['#0ea5e9', '#22d3ee'] },
  greeting: 'Hi',
  quickReplies: ['Price?'],
}

function trace(runId: string, sequence: number, extra: Partial<TraceEvent> = {}): TraceEvent {
  return {
    runId,
    sequence,
    type: 'LlmCall',
    name: 'model-x',
    input: null,
    output: null,
    tokensIn: 100,
    tokensOut: 10,
    durationMs: 50,
    costUsd: 0.001,
    ...extra,
  }
}

function run(actions: ChatAction[], from: ChatState = initialChatState): ChatState {
  return actions.reduce(chatReducer, from)
}

const ready = run([
  { type: 'niches_loaded', niches: [niche] },
  { type: 'session_started', sessionId: 's1', nicheId: 'dental' },
])

const sent = (text: string, id = 'u1'): ChatAction => ({ type: 'user_message', id, text, at: AT })

describe('chatReducer', () => {
  it('starts a session and keeps the loaded niches', () => {
    expect(ready.phase).toBe('ready')
    expect(ready.sessionId).toBe('s1')
    expect(ready.niches).toEqual([niche])
    expect(canSend(ready)).toBe(true)
  })

  it('adds the user message and marks the chat pending', () => {
    const state = run([sent('hello')], ready)
    expect(state.pending).toBe(true)
    expect(state.messages).toHaveLength(1)
    expect(state.messages[0]).toMatchObject({ role: 'user', text: 'hello' })
    expect(canSend(state)).toBe(false)
  })

  it('appends tokens in arrival order to one streaming assistant message', () => {
    const state = run(
      [
        sent('hello'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'token', text: 'Sure! ', at: AT },
        { type: 'token', text: 'A cleaning ', at: AT },
        { type: 'token', text: 'costs *USD 60*.', at: AT },
      ],
      ready,
    )
    expect(state.messages).toHaveLength(2)
    expect(state.messages[1]).toMatchObject({
      role: 'assistant',
      text: 'Sure! A cleaning costs *USD 60*.',
      streaming: true,
      runId: 'r1',
    })
    // The user message is linked to the same run.
    expect(state.messages[0]).toMatchObject({ runId: 'r1' })
  })

  it('groups trace events by run, ordered by sequence, ignoring duplicates', () => {
    const state = run(
      [
        sent('first', 'u1'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'trace', event: trace('r1', 1, { type: 'ToolCall', name: 'search' }), at: AT },
        { type: 'trace', event: trace('r1', 0), at: AT },
        { type: 'trace', event: trace('r1', 0), at: AT },
        { type: 'run_completed', data: { runId: 'r1', tokensIn: 200, tokensOut: 20, costUsd: 0.002, durationMs: 800, model: 'm', provider: 'p' } },
        sent('second', 'u2'),
        { type: 'run_started', runId: 'r2', at: AT },
        { type: 'trace', event: trace('r2', 0), at: AT },
      ],
      ready,
    )
    expect(state.runs.map((r) => r.runId)).toEqual(['r1', 'r2'])
    expect(state.runs[0].events.map((e) => e.sequence)).toEqual([0, 1])
    expect(state.runs[1].events).toHaveLength(1)
    expect(state.runs[0].userMessage).toBe('first')
    expect(state.runs[1].userMessage).toBe('second')
    expect(state.runs[0].info).toMatchObject({ tokensIn: 200, model: 'm', provider: 'p' })
  })

  it('computes live run totals from events until run_completed arrives', () => {
    const state = run(
      [
        sent('x'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'trace', event: trace('r1', 0), at: AT },
        { type: 'trace', event: trace('r1', 1, { tokensIn: 50, tokensOut: 5, costUsd: 0.0005 }), at: AT },
      ],
      ready,
    )
    expect(runTotals(state.runs[0])).toEqual({ tokensIn: 150, tokensOut: 15, costUsd: 0.0015, durationMs: 100 })
  })

  it('completes a run: stops pending and streaming, stores the totals', () => {
    const state = run(
      [
        sent('x'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'token', text: 'Done', at: AT },
        { type: 'run_completed', data: { runId: 'r1', tokensIn: 1, tokensOut: 2, costUsd: 0.5, durationMs: 3, model: 'm', provider: null } },
      ],
      ready,
    )
    expect(state.pending).toBe(false)
    expect(state.activeRunId).toBeNull()
    expect(state.messages[1]).toMatchObject({ streaming: false })
    expect(state.runs[0].status).toBe('ok')
    expect(canSend(state)).toBe(true)
  })

  it('drops the empty assistant bubble when a run ends without text', () => {
    const state = run(
      [
        sent('x'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'run_completed', data: { runId: 'r1', tokensIn: 0, tokensOut: 0, costUsd: 0, durationMs: 1, model: null, provider: null } },
      ],
      ready,
    )
    expect(state.messages).toHaveLength(1)
  })

  it('handoff adds one system notice and locks the input', () => {
    const state = run(
      [
        sent('talk to a person'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'token', text: 'Sure.', at: AT },
        { type: 'handoff', id: 'h1', at: AT },
        { type: 'handoff', id: 'h2', at: AT },
        { type: 'run_completed', data: { runId: 'r1', tokensIn: 1, tokensOut: 1, costUsd: 0, durationMs: 1, model: null, provider: null } },
      ],
      ready,
    )
    const systems = state.messages.filter((m) => m.role === 'system')
    expect(systems).toHaveLength(1)
    expect(systems[0].text).toBe(HANDOFF_TEXT)
    expect(state.sessionStatus).toBe('AwaitingHuman')
    expect(canSend(state)).toBe(false)
    // The notice comes after the assistant message.
    expect(state.messages.at(-1)?.role).toBe('system')
  })

  it('a stream error keeps partial text, marks the run failed and offers a retry', () => {
    const state = run(
      [
        sent('x'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'token', text: 'Partial', at: AT },
        { type: 'run_failed', message: 'capacity', retryable: true },
      ],
      ready,
    )
    expect(state.pending).toBe(false)
    expect(state.banner).toEqual({ kind: 'error', message: 'capacity', retryable: true })
    expect(state.messages[1]).toMatchObject({ text: 'Partial', streaming: false })
    expect(state.runs[0].status).toBe('error')
    expect(state.lastUserText).toBe('x')
  })

  it('retry_started removes the failed turn so it can be sent again', () => {
    const failed = run(
      [
        sent('first', 'u1'),
        { type: 'run_started', runId: 'r1', at: AT },
        { type: 'token', text: 'Answer', at: AT },
        { type: 'run_completed', data: { runId: 'r1', tokensIn: 0, tokensOut: 0, costUsd: 0, durationMs: 1, model: null, provider: null } },
        sent('second', 'u2'),
        { type: 'run_started', runId: 'r2', at: AT },
        { type: 'token', text: 'Partial', at: AT },
        { type: 'run_failed', message: 'x', retryable: true },
      ],
      ready,
    )
    const state = chatReducer(failed, { type: 'retry_started' })
    expect(state.banner).toBeNull()
    expect(state.messages.map((m) => m.id)).toEqual(['u1', 'assistant-r1'])
    expect(state.lastUserText).toBe('second')
  })

  it('session_limit locks the input until the demo is restarted', () => {
    const state = run(
      [
        sent('x'),
        { type: 'rate_limited', code: 'session_limit', message: 'Limit reached', retryAfterSeconds: null },
      ],
      ready,
    )
    expect(state.pending).toBe(false)
    expect(state.banner).toMatchObject({ kind: 'rate_limit', code: 'session_limit' })
    expect(canSend(state)).toBe(false)
  })

  it('other rate limits keep the input usable', () => {
    const state = run(
      [sent('x'), { type: 'rate_limited', code: 'ip_rate_limit', message: 'Slow down', retryAfterSeconds: 30 }],
      ready,
    )
    expect(state.banner).toMatchObject({ code: 'ip_rate_limit', retryAfterSeconds: 30 })
    expect(canSend(state)).toBe(true)
  })

  it('a session that no longer exists locks the input', () => {
    const state = run(
      [sent('x'), { type: 'request_failed', message: 'gone', retryable: false, sessionEnded: true }],
      ready,
    )
    expect(state.banner).toEqual({ kind: 'session_ended', message: 'gone' })
    expect(canSend(state)).toBe(false)
  })

  it('reset clears the conversation but keeps the niches', () => {
    const busy = run([sent('x'), { type: 'run_started', runId: 'r1', at: AT }], ready)
    const state = chatReducer(busy, { type: 'reset', nicheId: 'dental' })
    expect(state).toMatchObject({
      phase: 'loading',
      messages: [],
      runs: [],
      pending: false,
      banner: null,
      sessionId: null,
      nicheId: 'dental',
    })
    expect(state.niches).toEqual([niche])
  })

  it('restores messages, links them to runs by text, and shows a handoff notice', () => {
    const traceResponse: TraceResponse = {
      sessionId: 's1',
      runs: [
        {
          id: 'r1',
          userMessage: 'hello',
          status: 'Ok',
          model: 'm',
          provider: 'p',
          tokensIn: 10,
          tokensOut: 5,
          costUsd: 0.01,
          durationMs: 100,
          startedAt: AT,
          events: [trace('r1', 1), trace('r1', 0)],
        },
      ],
    }
    const state = run(
      [
        {
          type: 'session_restored',
          sessionId: 's1',
          nicheId: 'dental',
          stored: {
            messages: [
              { id: 'a', role: 'user', text: 'hello', createdAt: AT },
              { id: 'b', role: 'assistant', text: 'Hi there', createdAt: AT },
            ],
            sessionStatus: 'AwaitingHuman',
            language: 'en',
          },
          trace: traceResponse,
        },
      ],
      ready,
    )
    expect(state.messages.map((m) => m.role)).toEqual(['user', 'assistant', 'system'])
    expect(state.messages[0]).toMatchObject({ runId: 'r1' })
    expect(state.messages[1]).toMatchObject({ runId: 'r1' })
    expect(state.runs[0].events.map((e) => e.sequence)).toEqual([0, 1])
    expect(state.runs[0].info).toMatchObject({ model: 'm', costUsd: 0.01 })
    expect(state.language).toBe('en')
    expect(state.lastUserText).toBe('hello')
    expect(canSend(state)).toBe(false)
  })
})

function traceRun(id: string, status: 'Ok' | 'Error', events: TraceEvent[]): TraceResponse['runs'][number] {
  return {
    id,
    userMessage: `message ${id}`,
    status,
    model: 'm',
    provider: 'p',
    tokensIn: 10,
    tokensOut: 5,
    costUsd: 0.01,
    durationMs: 100,
    startedAt: AT,
    events,
  }
}

describe('trace hydration', () => {
  it('trace_hydrated adds the runs the page lacks, oldest first, and maps run status', () => {
    const live = run(
      [sent('live'), { type: 'run_started', runId: 'live-run', at: AT }, { type: 'trace', event: trace('live-run', 0), at: AT }],
      ready,
    )
    const state = chatReducer(live, {
      type: 'trace_hydrated',
      trace: {
        sessionId: 's1',
        runs: [
          traceRun('old-ok', 'Ok', [trace('old-ok', 1), trace('old-ok', 0)]),
          traceRun('old-bad', 'Error', [trace('old-bad', 0, { type: 'Error', name: 'agent_error' })]),
          traceRun('live-run', 'Error', []),
        ],
      },
    })
    expect(state.runs.map((r) => r.runId)).toEqual(['old-ok', 'old-bad', 'live-run'])
    expect(state.runs[0]).toMatchObject({ status: 'ok', info: { costUsd: 0.01, model: 'm' } })
    expect(state.runs[0].events.map((e) => e.sequence)).toEqual([0, 1])
    expect(state.runs[1]).toMatchObject({ status: 'error', info: null })
    // The in-flight run is local truth: the server still reads it as Error until it completes.
    expect(state.runs[2]).toMatchObject({ status: 'running' })
    expect(state.runs[2].events).toHaveLength(1)
  })

  it('trace_hydrated with nothing new returns the same state', () => {
    const state = run([{ type: 'trace_hydrated', trace: { sessionId: 's1', runs: [] } }], ready)
    expect(state).toBe(ready)
  })

  it('a restored past session is read-only and may lack a niche', () => {
    const state = run(
      [
        {
          type: 'session_restored',
          sessionId: 'past',
          nicheId: null,
          stored: { messages: [], sessionStatus: 'Active', language: null },
          trace: { sessionId: 'past', runs: [traceRun('r9', 'Ok', [trace('r9', 0)])] },
          readOnly: true,
        },
      ],
      initialChatState,
    )
    expect(state.readOnly).toBe(true)
    expect(state.nicheId).toBeNull()
    expect(state.runs).toHaveLength(1)
    expect(canSend(state)).toBe(false)
    expect(canType(state)).toBe(false)
  })
})
