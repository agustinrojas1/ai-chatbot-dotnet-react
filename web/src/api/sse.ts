import type { RunCompletedData, StreamEvent, TraceEvent, TraceEventType } from './types'

/** One dispatched SSE message, before the data is interpreted. */
export interface RawSseEvent {
  event: string
  data: string
}

/**
 * Incremental parser for the `text/event-stream` format (WHATWG HTML, section 9.2).
 * Feed it decoded text in whatever chunks the network delivers: events and lines may be
 * split anywhere, including between the CR and LF of a CRLF terminator.
 */
export class SseParser {
  private buffer = ''
  private eventName = ''
  private dataLines: string[] = []

  /** Returns the events completed by this chunk. */
  feed(chunk: string): RawSseEvent[] {
    this.buffer += chunk
    const events: RawSseEvent[] = []

    let start = 0
    for (let i = 0; i < this.buffer.length; i++) {
      const ch = this.buffer[i]
      if (ch !== '\n' && ch !== '\r') continue

      // A trailing CR may be the first half of CRLF: wait for the next chunk to know.
      if (ch === '\r' && i === this.buffer.length - 1) break

      const line = this.buffer.slice(start, i)
      if (ch === '\r' && this.buffer[i + 1] === '\n') i++
      start = i + 1

      const event = this.processLine(line)
      if (event) events.push(event)
    }

    this.buffer = this.buffer.slice(start)
    return events
  }

  private processLine(line: string): RawSseEvent | null {
    if (line === '') return this.dispatch()
    if (line.startsWith(':')) return null // comment / keep-alive

    const colon = line.indexOf(':')
    const field = colon === -1 ? line : line.slice(0, colon)
    let value = colon === -1 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)

    if (field === 'event') this.eventName = value
    else if (field === 'data') this.dataLines.push(value)
    // `id` and `retry` are irrelevant to this client.
    return null
  }

  private dispatch(): RawSseEvent | null {
    const hadData = this.dataLines.length > 0
    const event = { event: this.eventName || 'message', data: this.dataLines.join('\n') }
    this.eventName = ''
    this.dataLines = []
    return hadData ? event : null
  }
}

const TRACE_TYPES: readonly string[] = ['LlmCall', 'ToolCall', 'Guardrail', 'Handoff', 'Error']

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function numberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function numberOr0(value: unknown): number {
  return numberOrNull(value) ?? 0
}

function stringOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null
}

export function toTraceEvent(data: unknown): TraceEvent | null {
  if (!isRecord(data)) return null
  if (typeof data.runId !== 'string' || typeof data.sequence !== 'number') return null
  if (typeof data.type !== 'string' || !TRACE_TYPES.includes(data.type)) return null
  return {
    runId: data.runId,
    sequence: data.sequence,
    type: data.type as TraceEventType,
    name: typeof data.name === 'string' ? data.name : '',
    input: data.input ?? null,
    output: data.output ?? null,
    tokensIn: numberOrNull(data.tokensIn),
    tokensOut: numberOrNull(data.tokensOut),
    durationMs: numberOr0(data.durationMs),
    costUsd: numberOrNull(data.costUsd),
  }
}

/**
 * Interprets a raw SSE message. Unknown event names return null (the contract says clients
 * must ignore them), and so does data that is not valid JSON or has the wrong shape: one bad
 * event must not kill the whole answer.
 */
export function decodeStreamEvent(raw: RawSseEvent): StreamEvent | null {
  let data: unknown
  try {
    data = JSON.parse(raw.data)
  } catch {
    console.warn(`Ignoring SSE "${raw.event}" event with malformed JSON`)
    return null
  }
  if (!isRecord(data)) return null

  switch (raw.event) {
    case 'run_started':
      return typeof data.runId === 'string' ? { event: 'run_started', data: { runId: data.runId } } : null
    case 'token':
      return typeof data.text === 'string' ? { event: 'token', data: { text: data.text } } : null
    case 'trace': {
      const trace = toTraceEvent(data)
      return trace ? { event: 'trace', data: trace } : null
    }
    case 'handoff':
      return { event: 'handoff', data: { reason: typeof data.reason === 'string' ? data.reason : '' } }
    case 'run_completed': {
      if (typeof data.runId !== 'string') return null
      const completed: RunCompletedData = {
        runId: data.runId,
        tokensIn: numberOr0(data.tokensIn),
        tokensOut: numberOr0(data.tokensOut),
        costUsd: numberOr0(data.costUsd),
        durationMs: numberOr0(data.durationMs),
        model: stringOrNull(data.model),
        provider: stringOrNull(data.provider),
      }
      return { event: 'run_completed', data: completed }
    }
    case 'error':
      return typeof data.message === 'string'
        ? { event: 'error', data: { message: data.message, retryable: data.retryable === true } }
        : null
    default:
      return null
  }
}

/** Reads a response body as typed stream events until the server closes it. */
export async function* readStreamEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<StreamEvent> {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  const parser = new SseParser()
  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      for (const raw of parser.feed(decoder.decode(value, { stream: true }))) {
        const event = decodeStreamEvent(raw)
        if (event) yield event
      }
    }
    // Flush a multi-byte sequence the decoder might still hold; a final unterminated event is dropped, as the spec says.
    parser.feed(decoder.decode())
  } finally {
    await reader.cancel().catch(() => undefined)
  }
}
