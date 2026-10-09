import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import type { Niche } from './api/types'

const niches: Niche[] = [
  {
    id: 'realestate',
    name: 'Real Estate',
    persona: 'Sofia',
    headerName: 'Sofia | Real Estate AI',
    avatar: { emoji: '🏠', letter: 'S', colors: ['#128c7e', '#25d366'] },
    greeting: 'Hi! I am Sofia.',
    quickReplies: ['Show me rentals'],
  },
  {
    id: 'dental',
    name: 'Dental Clinic',
    persona: 'Alex',
    headerName: 'Dr. Smile Assistant',
    avatar: { emoji: '🦷', letter: 'D', colors: ['#0ea5e9', '#22d3ee'] },
    greeting: 'Hi! I am Alex.',
    quickReplies: ['How much is a cleaning?'],
  },
]

const encoder = new TextEncoder()
const json = (body: unknown, init?: ResponseInit) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init })

const sse = (event: string, data: unknown) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`

/** A response whose body the test feeds chunk by chunk. */
function liveStream() {
  let controller!: ReadableStreamDefaultController<Uint8Array>
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c
    },
  })
  return {
    response: new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }),
    push: (text: string) => act(async () => controller.enqueue(encoder.encode(text))),
    close: () => act(async () => controller.close()),
  }
}

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined

let handlers: Handler[]
let sessionCounter: number
let messageResponses: Array<() => Response>
let sentBodies: string[]

beforeEach(() => {
  localStorage.clear()
  sessionCounter = 0
  messageResponses = []
  sentBodies = []
  handlers = [
    (url) => (url === '/api/niches' ? json(niches) : undefined),
    (url, init) => {
      if (url !== '/api/sessions' || init?.method !== 'POST') return undefined
      sessionCounter += 1
      return json({ sessionId: `session-${sessionCounter}`, greeting: 'x', quickReplies: [] }, { status: 201 })
    },
    (url, init) => {
      if (!url.endsWith('/messages') || init?.method !== 'POST') return undefined
      sentBodies.push(String(init.body))
      const next = messageResponses.shift()
      return next ? next() : json({ title: 'No response scripted' }, { status: 500 })
    },
  ]
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      for (const handler of handlers) {
        const response = await handler(url, init)
        if (response) return response
      }
      return json({ title: `Unhandled ${init?.method ?? 'GET'} ${url}` }, { status: 404 })
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

async function renderReady() {
  render(<App />)
  await screen.findByRole('heading', { name: 'Sofia | Real Estate AI' })
  await screen.findByText('Hi! I am Sofia.')
}

function typeMessage(text: string) {
  const box = screen.getByLabelText('Message')
  fireEvent.change(box, { target: { value: text } })
  fireEvent.keyDown(box, { key: 'Enter' })
}

const completed = (runId: string) =>
  sse('run_completed', { runId, tokensIn: 10, tokensOut: 5, costUsd: 0.001, durationMs: 400, model: 'm', provider: 'p' })

describe('chat flow', () => {
  it('streams an answer with WhatsApp formatting and hides the quick replies after the first message', async () => {
    await renderReady()
    expect(screen.getByText('online')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Show me rentals' })).toBeTruthy()

    const stream = liveStream()
    messageResponses.push(() => stream.response)
    typeMessage('How much?')

    await screen.findByText('How much?')
    expect(screen.queryByRole('button', { name: 'Show me rentals' })).toBeNull()
    expect(screen.getByTestId('typing-indicator')).toBeTruthy()
    expect(screen.getByTestId('chat-status').textContent).toBe('typing…')
    expect(JSON.parse(sentBodies[0])).toEqual({ text: 'How much?' })

    await stream.push(sse('run_started', { runId: 'r1' }))
    await stream.push(sse('token', { text: 'It costs *USD' }))
    await screen.findByText('It costs *USD') // an unfinished marker stays literal
    await stream.push(sse('token', { text: ' 60* per month.' }))
    await waitFor(() => expect(screen.getByText('USD 60').tagName).toBe('STRONG'))
    expect(screen.queryByTestId('typing-indicator')).toBeNull()

    await stream.push(completed('r1'))
    await stream.close()
    await waitFor(() => expect(screen.getByTestId('chat-status').textContent).toBe('online'))
    expect(screen.getByLabelText('Message').hasAttribute('disabled')).toBe(false)
  })

  it('sends a quick reply as a message', async () => {
    await renderReady()
    messageResponses.push(() => new Response(sse('run_started', { runId: 'r1' }) + sse('token', { text: 'Sure' }) + completed('r1')))
    fireEvent.click(screen.getByRole('button', { name: 'Show me rentals' }))
    await screen.findByText('Sure')
    expect(screen.getByText('Show me rentals')).toBeTruthy()
    expect(JSON.parse(sentBodies[0])).toEqual({ text: 'Show me rentals' })
  })

  it('does not send on Shift+Enter and shows the character counter', async () => {
    await renderReady()
    const box = screen.getByLabelText('Message')
    fireEvent.change(box, { target: { value: 'hello' } })
    fireEvent.keyDown(box, { key: 'Enter', shiftKey: true })
    expect(sentBodies).toHaveLength(0)
    expect(screen.getByTestId('char-counter').textContent).toBe('5/1000')
  })

  it('shows the handoff notice and locks the input but keeps the conversation readable', async () => {
    await renderReady()
    messageResponses.push(
      () =>
        new Response(
          sse('run_started', { runId: 'r1' }) +
            sse('token', { text: 'Connecting you with a person.' }) +
            sse('handoff', { reason: 'asked' }) +
            completed('r1'),
        ),
    )
    typeMessage('I want a human')

    await screen.findByText(/A human will continue this conversation/)
    expect(screen.getByText('Connecting you with a person.')).toBeTruthy()
    expect(screen.getByTestId('chat-status').textContent).toBe('Transferred to staff 👤')
    expect(screen.getByLabelText('Message').hasAttribute('disabled')).toBe(true)
  })

  it('shows an error banner and Retry resends the same text, replacing the failed turn', async () => {
    await renderReady()
    messageResponses.push(
      () => new Response(sse('run_started', { runId: 'r1' }) + sse('error', { message: 'Provider is down.', retryable: true })),
      () => new Response(sse('run_started', { runId: 'r2' }) + sse('token', { text: 'Back online.' }) + completed('r2')),
    )
    typeMessage('Hello?')

    await screen.findByText('Provider is down.')
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))

    await screen.findByText('Back online.')
    expect(sentBodies.map((b) => JSON.parse(b).text)).toEqual(['Hello?', 'Hello?'])
    expect(screen.getAllByText('Hello?')).toHaveLength(1)
    expect(screen.queryByTestId('banner')).toBeNull()
  })

  it('treats a stream that ends early as a retryable error', async () => {
    await renderReady()
    messageResponses.push(() => new Response(sse('run_started', { runId: 'r1' }) + sse('token', { text: 'Half' })))
    typeMessage('Hi')
    await screen.findByText(/connection was interrupted/)
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy()
  })

  it('on a session_limit 429 shows the banner and Restart demo starts a fresh session', async () => {
    await renderReady()
    messageResponses.push(() =>
      json({ message: 'You reached the demo limit for this session.', code: 'session_limit' }, { status: 429 }),
    )
    typeMessage('One more')

    await screen.findByText('You reached the demo limit for this session.')
    expect(screen.getByLabelText('Message').hasAttribute('disabled')).toBe(true)

    fireEvent.click(screen.getByRole('button', { name: 'Restart demo' }))
    await waitFor(() => expect(screen.queryByTestId('banner')).toBeNull())
    await screen.findByText('Hi! I am Sofia.')
    expect(sessionCounter).toBe(2)
    expect(screen.queryByText('One more')).toBeNull()
    expect(JSON.parse(localStorage.getItem('ai-booking-agent.session')!).sessionId).toBe('session-2')
  })

  it('shows the Retry-After hint on an ip_rate_limit 429 and keeps the input usable', async () => {
    await renderReady()
    messageResponses.push(() =>
      json({ message: 'Too many messages.', code: 'ip_rate_limit' }, { status: 429, headers: { 'Retry-After': '90' } }),
    )
    typeMessage('Hi')
    await screen.findByText(/Too many messages\. Try again in 2 min\./)
    expect(screen.getByLabelText('Message').hasAttribute('disabled')).toBe(false)
  })

  it('switching niche starts a new session and aborts the stream in flight', async () => {
    await renderReady()
    const stream = liveStream()
    messageResponses.push(() => stream.response)
    typeMessage('Hello')
    await stream.push(sse('run_started', { runId: 'r1' }) + sse('token', { text: 'Typing a long answer' }))
    await screen.findByText('Typing a long answer')

    fireEvent.change(screen.getByLabelText('Niche:'), { target: { value: 'dental' } })

    await screen.findByRole('heading', { name: 'Dr. Smile Assistant' })
    await screen.findByText('Hi! I am Alex.')
    expect(screen.queryByText('Typing a long answer')).toBeNull()
    expect(sessionCounter).toBe(2)
    expect(screen.getByTestId('chat-status').textContent).toBe('online')
  })

  it('restores the stored session after a reload', async () => {
    localStorage.setItem('ai-booking-agent.session', JSON.stringify({ sessionId: 'stored-1', niche: 'dental' }))
    handlers.unshift((url) => {
      if (url === '/api/sessions/stored-1/messages') {
        return json({
          messages: [
            { id: 'a', role: 'user', text: 'Hi there', createdAt: '2026-01-01T10:00:00Z' },
            { id: 'b', role: 'assistant', text: 'Welcome *back*', createdAt: '2026-01-01T10:00:05Z' },
          ],
          sessionStatus: 'Active',
          language: 'en',
        })
      }
      if (url === '/api/sessions/stored-1/trace') return json({ sessionId: 'stored-1', runs: [] })
      return undefined
    })

    render(<App />)
    await screen.findByText('Hi there')
    expect(screen.getByText('back').tagName).toBe('STRONG')
    expect(screen.getByRole('heading', { name: 'Dr. Smile Assistant' })).toBeTruthy()
    expect(sessionCounter).toBe(0)
  })

  it('starts a fresh session when the stored one is gone (404)', async () => {
    localStorage.setItem('ai-booking-agent.session', JSON.stringify({ sessionId: 'old', niche: 'dental' }))
    render(<App />)
    await screen.findByText('Hi! I am Alex.')
    expect(sessionCounter).toBe(1)
    expect(JSON.parse(localStorage.getItem('ai-booking-agent.session')!).sessionId).toBe('session-1')
  })

  it('offers a retry when the niches cannot be loaded', async () => {
    handlers.unshift((url) => (url === '/api/niches' ? json({ title: 'down' }, { status: 503 }) : undefined))
    render(<App />)
    await screen.findByText(/Could not start the demo/)
    handlers.shift()
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await screen.findByText('Hi! I am Sofia.')
  })

  it('toggles the trace panel placeholder from the header', async () => {
    await renderReady()
    expect(screen.queryByTestId('trace-panel')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Show agent trace' }))
    expect(screen.getByTestId('trace-panel')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Hide agent trace' }))
    expect(screen.queryByTestId('trace-panel')).toBeNull()
  })

  it('links the author footer safely', async () => {
    await renderReady()
    const github = screen.getByRole('link', { name: 'GitHub' })
    const linkedin = screen.getByRole('link', { name: 'LinkedIn' })
    expect(github.getAttribute('href')).toBe('https://github.com/agustinrojas1')
    expect(linkedin.getAttribute('href')).toBe('https://www.linkedin.com/in/agustin-jorge-rojas/')
    for (const link of [github, linkedin]) {
      expect(link.getAttribute('target')).toBe('_blank')
      expect(link.getAttribute('rel')).toContain('noopener')
    }
  })
})
