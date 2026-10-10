// Wire types of the API contract (SPEC section 10, docs/architecture.md).

export type NicheId = 'realestate' | 'dental' | 'restaurant'

export interface Niche {
  id: string
  name: string
  persona: string
  headerName: string
  avatar: { emoji: string; letter: string; colors: [string, string] }
  greeting: string
  quickReplies: string[]
}

export interface SessionCreated {
  sessionId: string
  greeting: string
  quickReplies: string[]
}

export type SessionStatus = 'Active' | 'AwaitingHuman' | 'Closed'
export type Language = 'en' | 'es'

export interface StoredMessage {
  id: string
  role: 'user' | 'assistant'
  text: string
  createdAt: string
}

export interface MessagesResponse {
  messages: StoredMessage[]
  sessionStatus: SessionStatus
  language: Language | null
}

export type TraceEventType = 'LlmCall' | 'ToolCall' | 'Retrieval' | 'Guardrail' | 'Handoff' | 'Error'

/** One passage of a `Retrieval` event's output. Scores are null when that ranker did not return the passage. */
export interface RetrievalPassage {
  sourceId: string
  title: string
  heading: string
  text: string
  semanticScore: number | null
  lexicalScore: number | null
  finalScore: number
  aboveThreshold: boolean
}

/** `input` of a `Retrieval` event (`name` is `search_knowledge_base`). */
export interface RetrievalInput {
  query: string
}

/** `output` of a `Retrieval` event; `tokensIn` of the event are the query embedding tokens. */
export interface RetrievalOutput {
  thresholdPassed: boolean
  threshold: number
  passages: RetrievalPassage[]
}

/** One step of an agent run. Same shape in the SSE `trace` event and in `/trace`. */
export interface TraceEvent {
  runId: string
  sequence: number
  type: TraceEventType
  name: string
  input: unknown
  output: unknown
  tokensIn: number | null
  tokensOut: number | null
  durationMs: number
  costUsd: number | null
}

export interface TraceRun {
  id: string
  userMessage: string
  status: 'Ok' | 'Error'
  model: string | null
  provider: string | null
  tokensIn: number
  tokensOut: number
  costUsd: number
  durationMs: number
  startedAt: string
  events: TraceEvent[]
}

export interface TraceResponse {
  sessionId: string
  runs: TraceRun[]
}

/** Totals of a finished run (`run_completed`, or the summary fields of `/trace`). */
export interface RunInfo {
  tokensIn: number
  tokensOut: number
  costUsd: number
  durationMs: number
  model: string | null
  provider: string | null
}

export interface RunCompletedData extends RunInfo {
  runId: string
}

export type StreamEvent =
  | { event: 'run_started'; data: { runId: string } }
  | { event: 'token'; data: { text: string } }
  | { event: 'trace'; data: TraceEvent }
  | { event: 'handoff'; data: { reason: string } }
  | { event: 'run_completed'; data: RunCompletedData }
  | { event: 'error'; data: { message: string; retryable: boolean } }

export type RateLimitCode = 'ip_rate_limit' | 'session_limit' | 'daily_cap'

export type BookingStatus = 'PendingConfirmation' | 'Confirmed' | 'Cancelled' | (string & {})

/** One row of `GET /api/admin/bookings` (docs/architecture.md, Admin). */
export interface AdminBooking {
  id: string
  niche: NicheId
  sessionId: string
  status: BookingStatus
  customerName: string
  /** Masked by the server unless `Admin:MaskPhones` is off. */
  customerPhone: string
  slotStartsAt: string | null
  offeringName: string | null
  /** Order lines (restaurant): `[{ offeringId, name, qty, unitPrice, options }]`. Shape not enforced by the API. */
  items: unknown
  total: number | null
  notes: string | null
  createdAt: string
}

/** One row of `GET /api/admin/handoffs`. */
export interface AdminHandoff {
  id: string
  niche: NicheId
  sessionId: string
  reason: string
  summary: string | null
  status: string
  createdAt: string
}
