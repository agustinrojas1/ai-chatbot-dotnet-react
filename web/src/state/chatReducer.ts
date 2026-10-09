import type {
  Language,
  MessagesResponse,
  Niche,
  RateLimitCode,
  RunCompletedData,
  RunInfo,
  SessionStatus,
  TraceEvent,
  TraceResponse,
  TraceRun,
} from '../api/types'

export const MAX_INPUT_CHARS = 1000

export const HANDOFF_TEXT = 'A human will continue this conversation'

export type ChatMessage =
  | {
      id: string
      role: 'user' | 'assistant'
      text: string
      createdAt: string
      /** The agent run this message belongs to (the user turn and its answer share it). */
      runId: string | null
      streaming: boolean
    }
  | { id: string; role: 'system'; kind: 'handoff'; text: string; createdAt: string }

export interface RunState {
  runId: string
  userMessage: string
  status: 'running' | 'ok' | 'error'
  startedAt: string
  /** Trace events ordered by `sequence`, one per step of the run. */
  events: TraceEvent[]
  /** Totals reported by `run_completed` (or `/trace`); null while the run is in flight or if it failed. */
  info: RunInfo | null
}

/** A notice above the input that may offer an action. Cleared when the user acts. */
export type Banner =
  | { kind: 'rate_limit'; code: RateLimitCode | null; message: string; retryAfterSeconds: number | null }
  | { kind: 'error'; message: string; retryable: boolean }
  /** The session no longer accepts messages (unknown or closed): the only way forward is a new one. */
  | { kind: 'session_ended'; message: string }

export type Phase = 'loading' | 'ready' | 'failed'

export interface ChatState {
  phase: Phase
  /** Why `phase` is `failed`. */
  failure: string | null
  niches: Niche[]
  nicheId: string | null
  sessionId: string | null
  sessionStatus: SessionStatus
  language: Language | null
  messages: ChatMessage[]
  /** Runs in the order they started; the trace panel reads these. */
  runs: RunState[]
  activeRunId: string | null
  /** True from sending a message until its run completes or fails. */
  pending: boolean
  banner: Banner | null
  /** Text of the last message sent, for Retry. */
  lastUserText: string | null
  /** A past session opened from the admin page: nothing can be sent. */
  readOnly: boolean
}

export const initialChatState: ChatState = {
  phase: 'loading',
  failure: null,
  niches: [],
  nicheId: null,
  sessionId: null,
  sessionStatus: 'Active',
  language: null,
  messages: [],
  runs: [],
  activeRunId: null,
  pending: false,
  banner: null,
  lastUserText: null,
  readOnly: false,
}

export type ChatAction =
  | { type: 'niches_loaded'; niches: Niche[] }
  /** Leaves the current session behind and shows the loading state for `nicheId`. */
  | { type: 'reset'; nicheId: string | null }
  | { type: 'session_started'; sessionId: string; nicheId: string }
  | {
      type: 'session_restored'
      sessionId: string
      /** Null when a past session is opened without knowing its niche. */
      nicheId: string | null
      stored: MessagesResponse
      trace: TraceResponse | null
      readOnly?: boolean
    }
  /** Adds the runs the server knows and this page does not (after a reload whose `/trace` failed). Local runs win. */
  | { type: 'trace_hydrated'; trace: TraceResponse }
  | { type: 'session_failed'; message: string }
  | { type: 'user_message'; id: string; text: string; at: string }
  /** Drops the failed turn (user message and partial answer) so it can be sent again. */
  | { type: 'retry_started' }
  | { type: 'run_started'; runId: string; at: string }
  | { type: 'token'; text: string; at: string }
  | { type: 'trace'; event: TraceEvent; at: string }
  | { type: 'handoff'; id: string; at: string }
  | { type: 'run_completed'; data: RunCompletedData }
  /** The stream reported a failure, or ended without finishing. */
  | { type: 'run_failed'; message: string; retryable: boolean }
  /** The request was rejected before any stream (429). */
  | { type: 'rate_limited'; code: RateLimitCode | null; message: string; retryAfterSeconds: number | null }
  /** The request was rejected before any stream (400/404/409/network). */
  | { type: 'request_failed'; message: string; retryable: boolean; sessionEnded: boolean }

function updateRun(runs: RunState[], runId: string, update: (run: RunState) => RunState): RunState[] {
  return runs.map((run) => (run.runId === runId ? update(run) : run))
}

function insertEvent(events: TraceEvent[], event: TraceEvent): TraceEvent[] {
  if (events.some((e) => e.sequence === event.sequence)) return events
  return [...events, event].sort((a, b) => a.sequence - b.sequence)
}

function runFromTrace(run: TraceRun): RunState {
  return {
    runId: run.id,
    userMessage: run.userMessage,
    status: run.status === 'Ok' ? 'ok' : 'error',
    startedAt: run.startedAt,
    events: [...run.events].sort((a, b) => a.sequence - b.sequence),
    info:
      run.status === 'Ok'
        ? {
            tokensIn: run.tokensIn,
            tokensOut: run.tokensOut,
            costUsd: run.costUsd,
            durationMs: run.durationMs,
            model: run.model,
            provider: run.provider,
          }
        : null,
  }
}

function lastUserMessageText(messages: ChatMessage[]): string {
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i]
    if (m.role === 'user') return m.text
  }
  return ''
}

/** Closes the in-flight assistant bubble: drops it if nothing was written, otherwise stops streaming. */
function finishStreaming(messages: ChatMessage[], runId: string | null): ChatMessage[] {
  return messages.flatMap((m): ChatMessage[] => {
    if (m.role === 'system' || !m.streaming || (runId !== null && m.runId !== runId)) return [m]
    return m.text === '' ? [] : [{ ...m, streaming: false }]
  })
}

function failActiveRun(state: ChatState, banner: Banner): ChatState {
  const runId = state.activeRunId
  return {
    ...state,
    pending: false,
    activeRunId: null,
    messages: finishStreaming(state.messages, null),
    runs:
      runId === null
        ? state.runs
        : updateRun(state.runs, runId, (run) => ({ ...run, status: 'error' })),
    banner,
  }
}

/**
 * Restored messages carry no run id: pair each user message with the next run that has the same
 * text (runs are ordered by start time), and give the assistant answer that follows it the same id.
 */
function attachRuns(stored: MessagesResponse['messages'], trace: TraceResponse | null): ChatMessage[] {
  const runs = trace?.runs ?? []
  let next = 0
  let currentRunId: string | null = null
  return stored.map((m): ChatMessage => {
    if (m.role === 'user') {
      currentRunId = null
      const idx = runs.findIndex((r, i) => i >= next && r.userMessage === m.text)
      if (idx !== -1) {
        currentRunId = runs[idx].id
        next = idx + 1
      }
    }
    return { id: m.id, role: m.role, text: m.text, createdAt: m.createdAt, runId: currentRunId, streaming: false }
  })
}

export function chatReducer(state: ChatState, action: ChatAction): ChatState {
  switch (action.type) {
    case 'niches_loaded':
      return { ...state, niches: action.niches }

    case 'reset':
      return {
        ...initialChatState,
        niches: state.niches,
        nicheId: action.nicheId,
      }

    case 'session_started':
      return {
        ...initialChatState,
        phase: 'ready',
        niches: state.niches,
        nicheId: action.nicheId,
        sessionId: action.sessionId,
      }

    case 'session_restored': {
      const { stored, trace } = action
      const messages = attachRuns(stored.messages, trace)
      const last = messages.at(-1)
      if (stored.sessionStatus === 'AwaitingHuman') {
        messages.push({
          id: 'handoff-restored',
          role: 'system',
          kind: 'handoff',
          text: HANDOFF_TEXT,
          createdAt: last?.createdAt ?? new Date().toISOString(),
        })
      }
      const runs = (trace?.runs ?? []).map(runFromTrace)
      return {
        ...initialChatState,
        phase: 'ready',
        niches: state.niches,
        nicheId: action.nicheId,
        sessionId: action.sessionId,
        sessionStatus: stored.sessionStatus,
        language: stored.language,
        messages,
        runs,
        lastUserText: lastUserMessageText(messages) || null,
        readOnly: action.readOnly ?? false,
      }
    }

    case 'trace_hydrated': {
      const known = new Set(state.runs.map((r) => r.runId))
      const missing = action.trace.runs.filter((r) => !known.has(r.id)).map(runFromTrace)
      return missing.length === 0 ? state : { ...state, runs: [...missing, ...state.runs] }
    }

    case 'session_failed':
      return { ...state, phase: 'failed', failure: action.message }

    case 'user_message':
      return {
        ...state,
        pending: true,
        banner: null,
        lastUserText: action.text,
        messages: [
          ...state.messages,
          { id: action.id, role: 'user', text: action.text, createdAt: action.at, runId: null, streaming: false },
        ],
      }

    case 'retry_started': {
      let cut = state.messages.length
      for (let i = state.messages.length - 1; i >= 0; i--) {
        if (state.messages[i].role === 'user') {
          cut = i
          break
        }
      }
      return { ...state, messages: state.messages.slice(0, cut), banner: null }
    }

    case 'run_started': {
      // The user message that triggered this run is the last one without a run.
      const messages = [...state.messages]
      for (let i = messages.length - 1; i >= 0; i--) {
        const m = messages[i]
        if (m.role === 'user') {
          if (m.runId === null) messages[i] = { ...m, runId: action.runId }
          break
        }
      }
      messages.push({
        id: `assistant-${action.runId}`,
        role: 'assistant',
        text: '',
        createdAt: action.at,
        runId: action.runId,
        streaming: true,
      })
      return {
        ...state,
        messages,
        activeRunId: action.runId,
        runs: [
          ...state.runs.filter((r) => r.runId !== action.runId),
          {
            runId: action.runId,
            userMessage: lastUserMessageText(state.messages),
            status: 'running',
            startedAt: action.at,
            events: [],
            info: null,
          },
        ],
      }
    }

    case 'token': {
      const index = state.messages.findLastIndex((m) => m.role === 'assistant' && m.streaming)
      if (index === -1) {
        // Tokens before run_started: show them anyway rather than losing the answer.
        return {
          ...state,
          messages: [
            ...state.messages,
            {
              id: `assistant-${state.messages.length}`,
              role: 'assistant',
              text: action.text,
              createdAt: action.at,
              runId: state.activeRunId,
              streaming: true,
            },
          ],
        }
      }
      return {
        ...state,
        messages: state.messages.map((m, i) =>
          i === index && m.role !== 'system' ? { ...m, text: m.text + action.text } : m,
        ),
      }
    }

    case 'trace': {
      const { event } = action
      const known = state.runs.some((r) => r.runId === event.runId)
      const runs = known
        ? updateRun(state.runs, event.runId, (run) => ({ ...run, events: insertEvent(run.events, event) }))
        : [
            ...state.runs,
            {
              runId: event.runId,
              userMessage: lastUserMessageText(state.messages),
              status: 'running' as const,
              startedAt: action.at,
              events: [event],
              info: null,
            },
          ]
      return { ...state, runs }
    }

    case 'handoff': {
      const already = state.messages.some((m) => m.role === 'system' && m.kind === 'handoff')
      return {
        ...state,
        sessionStatus: 'AwaitingHuman',
        messages: already
          ? state.messages
          : [
              ...state.messages,
              { id: action.id, role: 'system', kind: 'handoff', text: HANDOFF_TEXT, createdAt: action.at },
            ],
      }
    }

    case 'run_completed': {
      const { runId, ...info } = action.data
      return {
        ...state,
        pending: false,
        activeRunId: null,
        messages: finishStreaming(state.messages, null),
        runs: updateRun(state.runs, runId, (run) => ({ ...run, status: 'ok', info })),
      }
    }

    case 'run_failed':
      return failActiveRun(state, { kind: 'error', message: action.message, retryable: action.retryable })

    case 'rate_limited':
      return failActiveRun(state, {
        kind: 'rate_limit',
        code: action.code,
        message: action.message,
        retryAfterSeconds: action.retryAfterSeconds,
      })

    case 'request_failed':
      return failActiveRun(
        state,
        action.sessionEnded
          ? { kind: 'session_ended', message: action.message }
          : { kind: 'error', message: action.message, retryable: action.retryable },
      )
  }
}

/** The text box accepts input: not after a handoff, when the session is over, or while loading. */
export function canType(state: ChatState): boolean {
  return (
    state.phase === 'ready' &&
    !state.readOnly &&
    state.sessionId !== null &&
    state.sessionStatus === 'Active' &&
    state.banner?.kind !== 'session_ended' &&
    !(state.banner?.kind === 'rate_limit' && state.banner.code === 'session_limit')
  )
}

/** A message can be sent now: the box accepts input and no answer is on its way. */
export function canSend(state: ChatState): boolean {
  return canType(state) && !state.pending
}

/** Sum of the LLM calls of a run, for totals while it is still streaming. */
export function runTotals(run: RunState): { tokensIn: number; tokensOut: number; costUsd: number; durationMs: number } {
  if (run.info) {
    return {
      tokensIn: run.info.tokensIn,
      tokensOut: run.info.tokensOut,
      costUsd: run.info.costUsd,
      durationMs: run.info.durationMs,
    }
  }
  return run.events.reduce(
    (sum, e) => ({
      tokensIn: sum.tokensIn + (e.tokensIn ?? 0),
      tokensOut: sum.tokensOut + (e.tokensOut ?? 0),
      costUsd: sum.costUsd + (e.costUsd ?? 0),
      durationMs: sum.durationMs + e.durationMs,
    }),
    { tokensIn: 0, tokensOut: 0, costUsd: 0, durationMs: 0 },
  )
}
