import { describe, expect, it } from 'vitest'
import type { TraceEvent } from '../api/types'
import type { RunState } from '../state/chatReducer'
import {
  buildRunView,
  describeStep,
  formatCost,
  formatDuration,
  guardrailMessage,
  runStatusLabel,
  sessionTotals,
} from './trace'

function event(sequence: number, extra: Partial<TraceEvent> = {}): TraceEvent {
  return {
    runId: 'r1',
    sequence,
    type: 'LlmCall',
    name: 'gemini-3.5-flash-lite',
    input: null,
    output: null,
    tokensIn: null,
    tokensOut: null,
    durationMs: 0,
    costUsd: null,
    ...extra,
  }
}

function makeRun(overrides: Partial<RunState> = {}): RunState {
  return { runId: 'r1', userMessage: 'Book me a cleaning', status: 'running', startedAt: '2026-01-01T10:00:00Z', events: [], info: null, ...overrides }
}

describe('formatting', () => {
  it('prints cost with 4 decimals and durations in ms or seconds', () => {
    expect(formatCost(0.000792)).toBe('$0.0008')
    expect(formatCost(0)).toBe('$0.0000')
    expect(formatCost(1.5)).toBe('$1.5000')
    expect(formatDuration(640)).toBe('640 ms')
    expect(formatDuration(2310)).toBe('2.31 s')
    expect(formatDuration(15_400)).toBe('15.4 s')
  })

  it('labels run statuses for people', () => {
    expect(runStatusLabel('running')).toBe('Running')
    expect(runStatusLabel('ok')).toBe('Completed')
    expect(runStatusLabel('error')).toBe('Failed')
  })
})

describe('guardrailMessage', () => {
  it('explains the known rules in plain words', () => {
    expect(guardrailMessage('slot_full', null)).toBe('that slot is already full')
    expect(guardrailMessage('slot_in_past', null)).toBe('that time has already passed')
    expect(guardrailMessage('session_awaiting_human', null)).toMatch(/person/)
    expect(guardrailMessage('session_closed', null)).toMatch(/closed/)
    expect(guardrailMessage('range_too_long', null)).toMatch(/too long/)
    expect(guardrailMessage('missing_target', null)).toBeTruthy()
  })

  it('describes iteration_limit with the number of rounds, invalid_* by field, and falls back to the message', () => {
    expect(guardrailMessage('iteration_limit', { code: 'iteration_limit', maxIterations: 5 })).toContain('5 tool rounds')
    expect(guardrailMessage('iteration_limit', null)).toMatch(/limit/)
    expect(guardrailMessage('invalid_customer_phone', null)).toBe('a tool rejected the customer phone it was given')
    expect(guardrailMessage('something_new', { message: 'Custom text' })).toBe('Custom text')
    expect(guardrailMessage('something_new', null)).toContain('something_new')
  })
})

describe('describeStep', () => {
  it('model call: model, provider, tokens and duration, and what it decided', () => {
    const toolCalling = describeStep(
      event(0, {
        tokensIn: 812,
        tokensOut: 24,
        durationMs: 640,
        output: { provider: 'gemini', model: 'gemini-3.5-flash-lite', text: '', toolCalls: ['search_catalog'], finishReason: 'tool_calls' },
      }),
      1,
    )
    expect(toolCalling).toMatchObject({ number: 1, kind: 'LlmCall', tone: 'model', label: 'Model call', failed: false })
    expect(toolCalling.chips).toEqual(['gemini-3.5-flash-lite', 'gemini', '812 in / 24 out tokens', '640 ms'])
    expect(toolCalling.summary).toContain('search_catalog')
    expect(toolCalling.excerpt).toBeNull()

    const answering = describeStep(event(1, { output: { provider: 'groq', model: 'llama', text: 'A cleaning costs *USD 60*.', toolCalls: [] } }), 2)
    expect(answering.summary).toBe('The model wrote the reply to the customer.')
    expect(answering.excerpt).toBe('A cleaning costs *USD 60*.')
    expect(answering.chips).toContain('groq')
  })

  it('a failed model call is an error step with the reason', () => {
    const step = describeStep(event(0, { name: 'unknown', output: { provider: 'gemini', model: 'unknown', error: 'HttpRequestException', status: 429 } }), 1)
    expect(step).toMatchObject({ tone: 'error', failed: true })
    expect(step.summary).toContain('HttpRequestException')
    expect(step.chips).not.toContain('unknown')
  })

  it('tool call: name in the label, arguments and result as sections, errors flagged', () => {
    const ok = describeStep(
      event(1, { type: 'ToolCall', name: 'check_availability', input: { fromDate: '2026-01-02' }, output: { slots: [] }, durationMs: 14 }),
      2,
    )
    expect(ok).toMatchObject({ tone: 'tool', label: 'Tool: check_availability', failed: false, chips: ['14 ms'] })
    expect(ok.summary).toBe('Checked which time slots are free.')
    expect(ok.sections.map((s) => s.label)).toEqual(['Arguments', 'Result'])

    const refused = describeStep(
      event(2, { type: 'ToolCall', name: 'create_booking', input: { slotId: 'x' }, output: { error: 'That slot is full.', code: 'slot_full' } }),
      3,
    )
    expect(refused.failed).toBe(true)
    expect(refused.summary).toContain('That slot is full.')

    const crashed = describeStep(event(3, { type: 'ToolCall', name: 'search_catalog', output: { error: 'TimeoutException' } }), 4)
    expect(crashed.summary).toBe('The tool failed (TimeoutException).')
    expect(describeStep(event(4, { type: 'ToolCall', name: 'brand_new_tool' }), 5).summary).toMatch(/Ran a tool/)
  })

  it('guardrail, handoff and error events read as one line', () => {
    const guardrail = describeStep(event(3, { type: 'Guardrail', name: 'slot_full', output: { tool: 'create_booking', code: 'slot_full', message: 'x' } }), 4)
    expect(guardrail).toMatchObject({ tone: 'guardrail', summary: 'Guardrail: that slot is already full' })

    const handoff = describeStep(event(4, { type: 'Handoff', name: 'handoff_to_human', output: { reason: 'customer asked for a person', handoffId: 'h', alreadyRequested: false } }), 5)
    expect(handoff.tone).toBe('handoff')
    expect(handoff.summary).toBe('A person will take over this conversation: customer asked for a person')
    expect(describeStep(event(4, { type: 'Handoff', name: 'handoff_to_human', output: { alreadyRequested: true } }), 5).summary).toMatch(/already been asked/)

    expect(describeStep(event(5, { type: 'Error', name: 'agent_error', output: { error: 'X' } }), 6)).toMatchObject({
      tone: 'error',
      failed: true,
      summary: 'Error: the run failed unexpectedly',
    })
    expect(describeStep(event(5, { type: 'Error', name: 'agent_cancelled', output: { code: 'x' } }), 6).summary).toMatch(/cancelled/)
  })
})

describe('buildRunView', () => {
  it('numbers steps in order and has no totals while the run is in flight', () => {
    const view = buildRunView(makeRun({ events: [event(0), event(1, { type: 'ToolCall', name: 'search_catalog' })] }))
    expect(view.title).toBe('Book me a cleaning')
    expect(view.statusLabel).toBe('Running')
    expect(view.steps.map((s) => [s.number, s.kind])).toEqual([[1, 'LlmCall'], [2, 'ToolCall']])
    expect(view.totals).toBeNull()
  })

  it('completed runs expose the totals reported by run_completed, with model and provider', () => {
    const view = buildRunView(
      makeRun({
        status: 'ok',
        events: [event(0, { tokensIn: 1, tokensOut: 1 })],
        info: { tokensIn: 1912, tokensOut: 114, costUsd: 0.00032, durationMs: 1800, model: 'gemini-3.5-flash-lite', provider: 'gemini' },
      }),
    )
    expect(view.statusLabel).toBe('Completed')
    expect(view.totals).toEqual({ tokensIn: 1912, tokensOut: 114, costUsd: 0.00032, durationMs: 1800, model: 'gemini-3.5-flash-lite via gemini', failed: false })
  })

  it('failed runs keep their steps; with no spend there are no totals', () => {
    const view = buildRunView(makeRun({ status: 'error', events: [event(0, { type: 'Error', name: 'agent_error' })] }))
    expect(view.statusLabel).toBe('Failed')
    expect(view.totals).toBeNull()
    expect(view.steps[0].tone).toBe('error')
  })

  it('failed live runs show the totals of the model calls that did finish, flagged as failed', () => {
    const view = buildRunView(
      makeRun({
        status: 'error',
        events: [event(0, { tokensIn: 900, tokensOut: 20, costUsd: 0.0004, durationMs: 600 }), event(1, { type: 'Error', name: 'agent_error', durationMs: 5 })],
      }),
    )
    expect(view.totals).toEqual({ tokensIn: 900, tokensOut: 20, costUsd: 0.0004, durationMs: 605, model: null, failed: true })
  })

  it('failed restored runs show the server totals', () => {
    const view = buildRunView(
      makeRun({ status: 'error', info: { tokensIn: 1500, tokensOut: 0, costUsd: 0.0002, durationMs: 1200, model: 'm', provider: null } }),
    )
    expect(view.totals).toMatchObject({ tokensIn: 1500, costUsd: 0.0002, durationMs: 1200, model: 'm', failed: true })
  })
})

describe('sessionTotals', () => {
  it('sums every run: finished ones by their totals, others by what their model calls spent', () => {
    const done = makeRun({ runId: 'a', status: 'ok', info: { tokensIn: 1000, tokensOut: 100, costUsd: 0.001, durationMs: 1, model: null, provider: null } })
    const live = makeRun({ runId: 'b', events: [event(0, { tokensIn: 500, tokensOut: 50, costUsd: 0.0005 }), event(1, { type: 'ToolCall' })] })
    expect(sessionTotals([done, live])).toEqual({ runs: 2, tokensIn: 1500, tokensOut: 150, costUsd: 0.0015 })
    expect(sessionTotals([])).toEqual({ runs: 0, tokensIn: 0, tokensOut: 0, costUsd: 0 })
  })
})

describe('Retrieval steps', () => {
  const passage = (n: number, extra: Record<string, unknown> = {}) => ({
    sourceId: `doc#${n}`,
    title: 'Pet policy',
    heading: 'Dogs',
    text: `Text ${n}`,
    semanticScore: 0.8,
    lexicalScore: 0.2,
    finalScore: 0.03,
    aboveThreshold: true,
    ...extra,
  })
  const retrieval = (output: unknown, extra: Partial<TraceEvent> = {}) =>
    event(2, { type: 'Retrieval', name: 'search_knowledge_base', input: { query: 'pets?' }, output, tokensIn: 7, costUsd: 0.0000012, durationMs: 120, ...extra })

  it('describes the search, its tokens and cost', () => {
    const step = describeStep(retrieval({ thresholdPassed: true, threshold: 0.6, passages: [passage(1), passage(2, { aboveThreshold: false })] }), 2)
    expect(step.label).toBe('Knowledge search')
    expect(step.tone).toBe('retrieval')
    expect(step.summary).toContain('hybrid semantic + keyword')
    expect(step.chips).toEqual(['120 ms', '7 embedding tokens', '$0.0000'])
    expect(step.failed).toBe(false)
    expect(step.retrieval?.query).toBe('pets?')
    expect(step.retrieval?.result).toBe('2 passages, 1 above threshold')
    expect(step.retrieval?.passages[0].semanticBar).toBe(0.8)
  })

  it('says nothing relevant was found when the threshold is not passed', () => {
    const view = describeStep(retrieval({ thresholdPassed: false, threshold: 0.6, passages: [passage(1, { aboveThreshold: false })] }), 1).retrieval
    expect(view?.thresholdPassed).toBe(false)
    expect(view?.result).toBe('No relevant information found (below threshold)')
  })

  it('keeps null scores as null and scales bars relative to the best passage', () => {
    const view = describeStep(
      retrieval({
        thresholdPassed: true,
        threshold: 0.6,
        passages: [passage(1, { semanticScore: null, lexicalScore: 0.4, finalScore: 0.02 }), passage(2, { lexicalScore: null, finalScore: 0.01 })],
      }),
      1,
    ).retrieval
    expect(view?.passages[0].semanticScore).toBeNull()
    expect(view?.passages[0].semanticBar).toBeNull()
    expect(view?.passages[0].lexicalBar).toBe(1)
    expect(view?.passages[1].lexicalScore).toBeNull()
    expect(view?.passages[1].finalBar).toBeCloseTo(0.5)
  })

  it('survives missing or malformed fields', () => {
    const view = describeStep(retrieval({ passages: [null, 'x', { text: 5 }], extra: true }, { input: null, tokensIn: null, costUsd: null }), 1)
    expect(view.chips).toEqual(['120 ms'])
    expect(view.retrieval?.query).toBeNull()
    expect(view.retrieval?.passages).toHaveLength(1)
    expect(view.retrieval?.passages[0]).toMatchObject({ title: 'passage#3', text: '', finalScore: null, aboveThreshold: false })
    expect(view.retrieval?.thresholdPassed).toBe(false)
    expect(describeStep(retrieval(null), 1).retrieval?.passages).toEqual([])
  })
})
