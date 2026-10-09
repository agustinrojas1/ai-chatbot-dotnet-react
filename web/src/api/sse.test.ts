import { afterEach, describe, expect, it, vi } from 'vitest'
import { decodeStreamEvent, readStreamEvents, SseParser } from './sse'
import type { StreamEvent } from './types'

function collect(parser: SseParser, chunks: string[]) {
  return chunks.flatMap((c) => parser.feed(c))
}

describe('SseParser', () => {
  it('parses a complete event', () => {
    const events = new SseParser().feed('event: token\ndata: {"text":"hi"}\n\n')
    expect(events).toEqual([{ event: 'token', data: '{"text":"hi"}' }])
  })

  it('handles an event split across chunks, even in the middle of a line', () => {
    const events = collect(new SseParser(), ['event: to', 'ken\nda', 'ta: {"text":', '"hi"}\n', '\n'])
    expect(events).toEqual([{ event: 'token', data: '{"text":"hi"}' }])
  })

  it('handles several events in one chunk', () => {
    const events = new SseParser().feed('event: a\ndata: 1\n\nevent: b\ndata: 2\n\n')
    expect(events.map((e) => e.event)).toEqual(['a', 'b'])
  })

  it('supports CRLF, including a CR/LF pair split between chunks', () => {
    const events = collect(new SseParser(), ['event: token\r\ndata: 1\r', '\n\r', '\nevent: token\r\ndata: 2\r\n\r\n'])
    expect(events).toEqual([
      { event: 'token', data: '1' },
      { event: 'token', data: '2' },
    ])
  })

  it('supports bare CR line endings', () => {
    // A trailing CR is held back until the next chunk shows whether an LF follows.
    expect(new SseParser().feed('event: x\rdata: 1\r\rev')).toEqual([{ event: 'x', data: '1' }])
  })

  it('joins multi-line data with newlines', () => {
    const events = new SseParser().feed('event: x\ndata: line1\ndata: line2\ndata:\ndata: line4\n\n')
    expect(events[0].data).toBe('line1\nline2\n\nline4')
  })

  it('ignores comments and unknown fields, and strips only one leading space', () => {
    const events = new SseParser().feed(': keep-alive\nid: 7\nretry: 100\nevent: x\ndata:  two spaces\n\n')
    expect(events).toEqual([{ event: 'x', data: ' two spaces' }])
  })

  it('does not dispatch events without data, and defaults the name to "message"', () => {
    const parser = new SseParser()
    expect(parser.feed('event: empty\n\n')).toEqual([])
    expect(parser.feed('data: x\n\n')).toEqual([{ event: 'message', data: 'x' }])
  })

  it('does not emit an event until the blank line arrives', () => {
    const parser = new SseParser()
    expect(parser.feed('event: x\ndata: 1\n')).toEqual([])
    expect(parser.feed('\n')).toHaveLength(1)
  })
})

describe('decodeStreamEvent', () => {
  afterEach(() => vi.restoreAllMocks())

  it('decodes every contract event', () => {
    const trace = {
      runId: 'r1',
      sequence: 2,
      type: 'ToolCall',
      name: 'search_catalog',
      input: { q: 'x' },
      output: [1],
      tokensIn: null,
      tokensOut: null,
      durationMs: 12,
      costUsd: null,
    }
    const cases: Array<[string, unknown, StreamEvent]> = [
      ['run_started', { runId: 'r1' }, { event: 'run_started', data: { runId: 'r1' } }],
      ['token', { text: 'hi' }, { event: 'token', data: { text: 'hi' } }],
      ['trace', trace, { event: 'trace', data: trace as never }],
      ['handoff', { reason: 'asked' }, { event: 'handoff', data: { reason: 'asked' } }],
      [
        'run_completed',
        { runId: 'r1', tokensIn: 10, tokensOut: 5, costUsd: 0.001, durationMs: 900, model: 'm', provider: 'p' },
        {
          event: 'run_completed',
          data: { runId: 'r1', tokensIn: 10, tokensOut: 5, costUsd: 0.001, durationMs: 900, model: 'm', provider: 'p' },
        },
      ],
      ['error', { message: 'boom', retryable: true }, { event: 'error', data: { message: 'boom', retryable: true } }],
    ]
    for (const [name, data, expected] of cases) {
      expect(decodeStreamEvent({ event: name, data: JSON.stringify(data) })).toEqual(expected)
    }
  })

  it('tolerates malformed JSON by ignoring the event', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    expect(decodeStreamEvent({ event: 'token', data: '{"text":' })).toBeNull()
  })

  it('ignores unknown event names and wrongly shaped data', () => {
    expect(decodeStreamEvent({ event: 'future_event', data: '{"a":1}' })).toBeNull()
    expect(decodeStreamEvent({ event: 'token', data: '{"text":5}' })).toBeNull()
    expect(decodeStreamEvent({ event: 'trace', data: '{"runId":"r","sequence":1,"type":"Nope"}' })).toBeNull()
    expect(decodeStreamEvent({ event: 'token', data: '[1]' })).toBeNull()
  })
})

describe('readStreamEvents', () => {
  it('decodes bytes split in the middle of a multi-byte character', async () => {
    const bytes = new TextEncoder().encode('event: token\ndata: {"text":"café ✓"}\n\n')
    const cut = bytes.indexOf(0xc3) + 1 // between the two bytes of "é"
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes.slice(0, cut))
        controller.enqueue(bytes.slice(cut))
        controller.close()
      },
    })

    const events: StreamEvent[] = []
    for await (const event of readStreamEvents(body)) events.push(event)

    expect(events).toEqual([{ event: 'token', data: { text: 'café ✓' } }])
  })
})
