import type { RunInfo, TraceEvent, TraceEventType } from '../api/types'
import { runTotals, type RunState } from '../state/chatReducer'

/** What the trace panel shows, derived from the raw events so the component stays presentational. */

export type StepTone = 'model' | 'tool' | 'retrieval' | 'guardrail' | 'handoff' | 'error'

export interface TraceStep {
  /** Stable React key: events of a run have unique sequences. */
  key: string
  /** 1-based position in the run, for the numbered timeline. */
  number: number
  kind: TraceEventType
  tone: StepTone
  /** Friendly name: "Model call", "Tool: search_catalog", "Guardrail", ... */
  label: string
  /** One readable sentence about what happened. */
  summary: string
  /** Short quote of what the model wrote, for model calls that answered the customer. */
  excerpt: string | null
  /** Small facts shown as chips: model, provider, tokens, duration. */
  chips: string[]
  /** The step did not go as planned (failed model call, tool error). */
  failed: boolean
  /** Raw payloads for the collapsible JSON viewers; null/undefined payloads are left out. */
  sections: { label: string; value: unknown }[]
  /** Parsed knowledge-base search, only for `Retrieval` steps. */
  retrieval: RetrievalView | null
}

export interface RetrievalPassageView {
  key: string
  sourceId: string
  title: string
  heading: string | null
  text: string
  /** Cosine similarity; null when the passage only came from the keyword search. */
  semanticScore: number | null
  /** Keyword (full-text) rank; null when the passage only came from the semantic search. */
  lexicalScore: number | null
  /** Reciprocal Rank Fusion score of the two rankings. */
  finalScore: number | null
  aboveThreshold: boolean
  /** 0..1 widths for the score bars (semantic is absolute, the others are relative to the best passage). */
  semanticBar: number | null
  lexicalBar: number | null
  finalBar: number | null
}

export interface RetrievalView {
  query: string | null
  thresholdPassed: boolean
  threshold: number | null
  passages: RetrievalPassageView[]
  aboveCount: number
  /** "3 passages, 2 above threshold" or the below-threshold message. */
  result: string
}

export type RunStatusLabel = 'Running' | 'Completed' | 'Failed'

export interface RunView {
  runId: string
  title: string
  status: RunState['status']
  statusLabel: RunStatusLabel
  steps: TraceStep[]
  /** Totals of a finished run, or up to the failure for a failed one with any spend; null while running. */
  totals: RunTotalsView | null
}

export interface RunTotalsView {
  tokensIn: number
  tokensOut: number
  costUsd: number
  durationMs: number
  /** "gemini-3.5-flash-lite via gemini", or whatever is known. */
  model: string | null
  /** The run failed: these are the totals up to the failure. */
  failed: boolean
}

export interface SessionTotals {
  runs: number
  tokensIn: number
  tokensOut: number
  costUsd: number
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

function str(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null
}

export function formatTokens(count: number): string {
  return Math.round(count).toLocaleString('en-US')
}

/** USD with 4 decimals, as the SPEC asks: `$0.0003`. */
export function formatCost(usd: number): string {
  return `$${usd.toFixed(4)}`
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`
}

export function runStatusLabel(status: RunState['status']): RunStatusLabel {
  switch (status) {
    case 'running':
      return 'Running'
    case 'ok':
      return 'Completed'
    case 'error':
      return 'Failed'
  }
}

const GUARDRAIL_MESSAGES: Record<string, string> = {
  slot_full: 'that slot is already full',
  slot_in_past: 'that time has already passed',
  session_awaiting_human: 'a person is already taking over this conversation',
  session_closed: 'this conversation is closed',
  range_too_long: 'that date range is too long',
  missing_target: 'the booking did not say what it is for',
}

/** Readable reason of a guardrail event, without the "Guardrail:" prefix. */
export function guardrailMessage(name: string, output: unknown): string {
  const known = GUARDRAIL_MESSAGES[name]
  if (known) return known
  const record = asRecord(output)
  if (name === 'iteration_limit') {
    const max = record?.maxIterations
    return typeof max === 'number'
      ? `the agent reached its limit of ${max} tool rounds and was stopped`
      : 'the agent reached its limit of tool rounds and was stopped'
  }
  if (name.startsWith('invalid_')) {
    const field = name.slice('invalid_'.length).replaceAll('_', ' ')
    return `a tool rejected the ${field} it was given`
  }
  return str(record?.message) ?? `the rule "${name}" stopped this step`
}

function errorMessage(name: string, output: unknown): string {
  if (name === 'agent_cancelled') return 'the run was cancelled before it finished (the client disconnected)'
  if (name === 'agent_error') return 'the run failed unexpectedly'
  return str(asRecord(output)?.error) ?? `the run stopped (${name})`
}

const TOOL_SUMMARIES: Record<string, string> = {
  search_catalog: 'Looked through the business catalog.',
  get_offering_details: 'Fetched the details of one catalog item.',
  check_availability: 'Checked which time slots are free.',
  create_booking: 'Registered a booking request.',
  handoff_to_human: 'Asked a person on the team to take over.',
}

function toolSummary(name: string, output: unknown): { text: string; failed: boolean } {
  const record = asRecord(output)
  const error = str(record?.error)
  if (error !== null) {
    // `{ error: message, code }` is a tool refusing the request; `{ error: "ExceptionType" }` is a crash.
    return record?.code !== undefined
      ? { text: `The tool refused the request: ${error}`, failed: true }
      : { text: `The tool failed (${error}).`, failed: true }
  }
  return { text: TOOL_SUMMARIES[name] ?? 'Ran a tool and sent its result back to the model.', failed: false }
}

function clip(text: string, max: number): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length > max ? `${flat.slice(0, max - 1)}…` : flat
}

function num(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

/** Scores are small decimals (cosine, RRF): 3 decimals, or a dash when a ranker did not return the passage. */
export function formatScore(score: number | null): string {
  return score === null ? '–' : score.toFixed(3)
}

function bar(score: number | null, max: number): number | null {
  if (score === null) return null
  if (max <= 0) return 0
  return Math.min(1, Math.max(0, score / max))
}

/** Reads the output of a `Retrieval` event tolerantly: missing or extra fields never break the card. */
export function parseRetrieval(input: unknown, output: unknown): RetrievalView {
  const out = asRecord(output)
  const rawPassages = Array.isArray(out?.passages) ? out.passages : []
  const base = rawPassages.flatMap((raw, index) => {
    const p = asRecord(raw)
    if (!p) return []
    const sourceId = str(p.sourceId) ?? `passage#${index + 1}`
    return [
      {
        key: `${sourceId}-${index}`,
        sourceId,
        title: str(p.title) ?? sourceId,
        heading: str(p.heading),
        text: typeof p.text === 'string' ? p.text : '',
        semanticScore: num(p.semanticScore),
        lexicalScore: num(p.lexicalScore),
        finalScore: num(p.finalScore),
        aboveThreshold: p.aboveThreshold === true,
      },
    ]
  })
  const maxLexical = Math.max(0, ...base.map((p) => p.lexicalScore ?? 0))
  const maxFinal = Math.max(0, ...base.map((p) => p.finalScore ?? 0))
  const passages: RetrievalPassageView[] = base.map((p) => ({
    ...p,
    semanticBar: bar(p.semanticScore, 1),
    lexicalBar: bar(p.lexicalScore, maxLexical),
    finalBar: bar(p.finalScore, maxFinal),
  }))
  const aboveCount = passages.filter((p) => p.aboveThreshold).length
  const thresholdPassed = typeof out?.thresholdPassed === 'boolean' ? out.thresholdPassed : aboveCount > 0
  const noun = (n: number) => `${n} passage${n === 1 ? '' : 's'}`
  let result: string
  if (!thresholdPassed) result = 'No relevant information found (below threshold)'
  else if (passages.length === 0) result = 'No passages returned'
  else result = `${noun(passages.length)}, ${aboveCount} above threshold`
  return {
    query: str(asRecord(input)?.query),
    thresholdPassed,
    threshold: num(out?.threshold),
    passages,
    aboveCount,
    result,
  }
}

function retrievalStep(event: TraceEvent, base: Pick<TraceStep, 'key' | 'number' | 'kind' | 'sections'>): TraceStep {
  const chips = [formatDuration(event.durationMs)]
  if (event.tokensIn !== null) chips.push(`${formatTokens(event.tokensIn)} embedding tokens`)
  if (event.costUsd !== null) chips.push(formatCost(event.costUsd))
  return {
    ...base,
    tone: 'retrieval',
    label: 'Knowledge search',
    summary: 'Searched the business knowledge base: hybrid semantic + keyword search.',
    excerpt: null,
    chips,
    failed: false,
    retrieval: parseRetrieval(event.input, event.output),
  }
}

function stepSections(event: TraceEvent): TraceStep['sections'] {
  const pick = (label: string, value: unknown) => (value === null || value === undefined ? [] : [{ label, value }])
  if (event.type === 'ToolCall') return [...pick('Arguments', event.input), ...pick('Result', event.output)]
  return [...pick('Input', event.input), ...pick('Output', event.output)]
}

function llmStep(event: TraceEvent, base: Pick<TraceStep, 'key' | 'number' | 'kind' | 'sections' | 'retrieval'>): TraceStep {
  const output = asRecord(event.output)
  // The server writes `unknown` when a call failed before any update told it the model.
  const model = [str(output?.model), event.name].find((name) => name !== null && name !== 'unknown') ?? null
  const provider = str(output?.provider)
  const error = str(output?.error)
  const toolCalls = Array.isArray(output?.toolCalls) ? output.toolCalls.filter((t) => typeof t === 'string') : []
  const text = str(output?.text)

  const chips: string[] = []
  if (model) chips.push(model)
  if (provider) chips.push(provider)
  if (event.tokensIn !== null || event.tokensOut !== null) {
    chips.push(`${formatTokens(event.tokensIn ?? 0)} in / ${formatTokens(event.tokensOut ?? 0)} out tokens`)
  }
  chips.push(formatDuration(event.durationMs))

  let summary: string
  if (error) summary = `The model call failed: ${clip(error, 160)}`
  else if (toolCalls.length > 0) summary = `The model decided to use ${toolCalls.length === 1 ? 'a tool' : 'tools'}: ${toolCalls.join(', ')}.`
  else summary = 'The model wrote the reply to the customer.'

  return {
    ...base,
    tone: error ? 'error' : 'model',
    label: 'Model call',
    summary,
    excerpt: !error && toolCalls.length === 0 && text ? clip(text, 160) : null,
    chips,
    failed: Boolean(error),
  }
}

/** Turns one raw trace event into the step the timeline shows. */
export function describeStep(event: TraceEvent, number: number): TraceStep {
  const base = {
    key: `${event.runId}-${event.sequence}`,
    number,
    kind: event.type,
    sections: stepSections(event),
    retrieval: null,
  }
  const duration = formatDuration(event.durationMs)

  switch (event.type) {
    case 'LlmCall':
      return llmStep(event, base)
    case 'Retrieval':
      return retrievalStep(event, base)
    case 'ToolCall': {
      const { text, failed } = toolSummary(event.name, event.output)
      return { ...base, tone: 'tool', label: `Tool: ${event.name}`, summary: text, excerpt: null, chips: [duration], failed }
    }
    case 'Guardrail':
      return {
        ...base,
        tone: 'guardrail',
        label: 'Guardrail',
        summary: `Guardrail: ${guardrailMessage(event.name, event.output)}`,
        excerpt: null,
        chips: event.durationMs > 0 ? [duration] : [],
        failed: false,
      }
    case 'Handoff': {
      const output = asRecord(event.output)
      const reason = str(output?.reason)
      const already = output?.alreadyRequested === true
      return {
        ...base,
        tone: 'handoff',
        label: 'Handoff to a human',
        summary: `${already ? 'A person had already been asked to take over' : 'A person will take over this conversation'}${reason ? `: ${reason}` : '.'}`,
        excerpt: null,
        chips: [],
        failed: false,
      }
    }
    case 'Error':
      return {
        ...base,
        tone: 'error',
        label: 'Error',
        summary: `Error: ${errorMessage(event.name, event.output)}`,
        excerpt: null,
        chips: [],
        failed: true,
      }
  }
}

function modelLabel(info: RunInfo | null): string | null {
  if (!info) return null
  if (info.model && info.provider) return `${info.model} via ${info.provider}`
  return info.model ?? info.provider
}

function runTotalsView(run: RunState, totals: ReturnType<typeof runTotals>): RunTotalsView | null {
  if (run.status === 'running') return null
  const failed = run.status === 'error'
  const spent = totals.tokensIn > 0 || totals.tokensOut > 0 || totals.costUsd > 0 || totals.durationMs > 0
  if (failed && !spent) return null
  return { ...totals, model: modelLabel(run.info), failed }
}

/** Groups a run for display: title, ordered numbered steps and, once finished, the totals. */
export function buildRunView(run: RunState): RunView {
  const totals = runTotals(run)
  return {
    runId: run.runId,
    title: run.userMessage,
    status: run.status,
    statusLabel: runStatusLabel(run.status),
    steps: run.events.map((event, index) => describeStep(event, index + 1)),
    totals: runTotalsView(run, totals),
  }
}

/** Runs, tokens and cost of the whole session, counting what in-flight and failed runs already spent. */
export function sessionTotals(runs: RunState[]): SessionTotals {
  return runs.reduce<SessionTotals>(
    (sum, run) => {
      const t = runTotals(run)
      return {
        runs: sum.runs + 1,
        tokensIn: sum.tokensIn + t.tokensIn,
        tokensOut: sum.tokensOut + t.tokensOut,
        costUsd: sum.costUsd + t.costUsd,
      }
    },
    { runs: 0, tokensIn: 0, tokensOut: 0, costUsd: 0 },
  )
}
