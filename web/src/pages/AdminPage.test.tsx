import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminBooking, AdminHandoff } from '../api/types'
import AdminPage from './AdminPage'

const json = (body: unknown, init?: ResponseInit) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init })

const bookings: AdminBooking[] = [
  {
    id: 'b1', niche: 'dental', sessionId: 'sess-1', status: 'PendingConfirmation', customerName: 'Ana Perez',
    customerPhone: '+54 11 ****-4444', slotStartsAt: '2026-10-12T14:00:00+00:00', offeringName: 'Teeth cleaning',
    items: null, total: null, notes: 'First visit', createdAt: '2026-10-09T20:16:00+00:00',
  },
  {
    id: 'b2', niche: 'restaurant', sessionId: 'sess-2', status: 'PendingConfirmation', customerName: 'Luca Rossi',
    customerPhone: '+54 11 ****-1111', slotStartsAt: null, offeringName: null,
    items: [{ name: 'Pepperoni', qty: 2 }, { name: 'Margherita', qty: 1 }], total: 31.5, notes: null,
    createdAt: '2026-10-09T19:00:00+00:00',
  },
]
const handoffs: AdminHandoff[] = [
  { id: 'h1', niche: 'dental', sessionId: 'sess-3', reason: 'customer asked for a person', summary: 'Wants staff.', status: 'Open', createdAt: '2026-10-09T20:20:00+00:00' },
]

interface Call { url: string; key: string | null }
let calls: Call[]
let respond: (url: string, key: string | null) => Response

beforeEach(() => {
  sessionStorage.clear()
  calls = []
  respond = (url) => json(url.startsWith('/api/admin/bookings') ? bookings : handoffs)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const key = new Headers(init?.headers).get('X-Admin-Key')
      calls.push({ url: String(input), key })
      return respond(String(input), key)
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function unlock(key = 'secret') {
  fireEvent.change(screen.getByLabelText('Admin key'), { target: { value: key } })
  fireEvent.click(screen.getByRole('button', { name: 'Show data' }))
}

describe('AdminPage', () => {
  it('asks for the key first and always shows the demo-grade note, the demo link and the footer', () => {
    render(<AdminPage />)
    expect((screen.getByLabelText('Admin key') as HTMLInputElement).type).toBe('password')
    expect(screen.getByTestId('demo-note').textContent).toMatch(/Demo-grade/)
    expect(screen.getByRole('link', { name: /Back to the demo/ }).getAttribute('href')).toBe('/')
    expect(screen.getByRole('link', { name: 'GitHub' })).toBeTruthy()
    expect(calls).toHaveLength(0)
  })

  it('sends the key in X-Admin-Key, stores it in sessionStorage only and lists newest-first rows with trace links', async () => {
    render(<AdminPage />)
    unlock()
    await screen.findByText('Ana Perez')
    expect(calls).toEqual([
      { url: '/api/admin/bookings', key: 'secret' },
      { url: '/api/admin/handoffs', key: 'secret' },
    ])
    expect(sessionStorage.getItem('ai-booking-agent.admin-key')).toBe('secret')
    expect(localStorage.getItem('ai-booking-agent.admin-key')).toBeNull()

    const rows = screen.getAllByTestId('booking-row')
    expect(rows).toHaveLength(2)
    expect(within(rows[0]).getByText('+54 11 ****-4444')).toBeTruthy()
    expect(within(rows[0]).getByText('Pending confirmation')).toBeTruthy()
    expect(within(rows[0]).getByRole('link', { name: 'View trace' }).getAttribute('href')).toBe('/?session=sess-1&niche=dental')
    expect(within(rows[1]).getByText(/2× Pepperoni, 1× Margherita/)).toBeTruthy()
    expect(within(rows[1]).getByText('31.50')).toBeTruthy()

    const handoffRows = screen.getAllByTestId('handoff-row')
    expect(handoffRows).toHaveLength(1)
    expect(within(handoffRows[0]).getByText('customer asked for a person')).toBeTruthy()
    expect(within(handoffRows[0]).getByRole('link', { name: 'View trace' }).getAttribute('href')).toBe('/?session=sess-3&niche=dental')
  })

  it('uses a key kept in sessionStorage without asking again', async () => {
    sessionStorage.setItem('ai-booking-agent.admin-key', 'kept')
    render(<AdminPage />)
    await screen.findByText('Ana Perez')
    expect(calls[0].key).toBe('kept')
    expect(screen.queryByLabelText('Admin key')).toBeNull()
  })

  it('shows a loading state while the lists load', async () => {
    let release!: () => void
    const gate = new Promise<void>((resolve) => (release = resolve))
    vi.stubGlobal('fetch', vi.fn(async () => { await gate; return json([]) }))
    sessionStorage.setItem('ai-booking-agent.admin-key', 'k')
    render(<AdminPage />)
    expect(screen.getByText('Loading bookings…')).toBeTruthy()
    expect(screen.getByText('Loading handoffs…')).toBeTruthy()
    release()
    await screen.findByText(/No bookings yet/)
  })

  it('401: forgets the key and says it was not accepted', async () => {
    respond = () => json({ title: 'Unauthorized' }, { status: 401 })
    render(<AdminPage />)
    unlock('wrong')
    await screen.findByText(/That key was not accepted/)
    expect(sessionStorage.getItem('ai-booking-agent.admin-key')).toBeNull()
    expect(screen.getByLabelText('Admin key')).toBeTruthy()
    // Typing a new key and sending it works.
    respond = (url) => json(url.startsWith('/api/admin/bookings') ? bookings : handoffs)
    unlock('right')
    await screen.findByText('Ana Perez')
    expect(screen.queryByText(/That key was not accepted/)).toBeNull()
  })

  it('503: explains that the admin is disabled on the server', async () => {
    respond = () => json({ title: 'Admin disabled' }, { status: 503 })
    render(<AdminPage />)
    unlock()
    await screen.findByText('Admin is disabled on this server')
    expect(screen.queryByTestId('booking-row')).toBeNull()
  })

  it('other failures offer a retry', async () => {
    respond = () => json({ title: 'Boom' }, { status: 500 })
    render(<AdminPage />)
    unlock()
    await screen.findByText('Boom')
    respond = (url) => json(url.startsWith('/api/admin/bookings') ? bookings : handoffs)
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await screen.findByText('Ana Perez')
  })

  it('shows an empty state per table', async () => {
    respond = () => json([])
    render(<AdminPage />)
    unlock()
    await screen.findByText(/No bookings yet/)
    expect(screen.getByText(/No handoffs yet/)).toBeTruthy()
    expect(screen.queryByRole('table')).toBeNull()
  })

  it('filters by niche, asking the API for that niche, and can go back to all', async () => {
    render(<AdminPage />)
    unlock()
    await screen.findByText('Ana Perez')
    expect(screen.getByRole('button', { name: 'All niches' }).getAttribute('aria-pressed')).toBe('true')

    respond = (url) => json(url.includes('niche=restaurant') ? (url.includes('bookings') ? [bookings[1]] : []) : [])
    fireEvent.click(screen.getByRole('button', { name: 'Restaurant' }))
    await screen.findByText('Luca Rossi')
    expect(screen.queryByText('Ana Perez')).toBeNull()
    expect(screen.getByRole('button', { name: 'Restaurant' }).getAttribute('aria-pressed')).toBe('true')
    expect(calls.slice(-2).map((c) => c.url)).toEqual(['/api/admin/bookings?niche=restaurant', '/api/admin/handoffs?niche=restaurant'])
    await waitFor(() => expect(screen.getByText(/No handoffs yet/)).toBeTruthy())

    respond = (url) => json(url.startsWith('/api/admin/bookings') ? bookings : handoffs)
    fireEvent.click(screen.getByRole('button', { name: 'All niches' }))
    await screen.findByText('Ana Perez')
    expect(calls.at(-1)?.url).toBe('/api/admin/handoffs')
  })

  it('Forget key returns to the key form', async () => {
    render(<AdminPage />)
    unlock()
    await screen.findByText('Ana Perez')
    fireEvent.click(screen.getByRole('button', { name: 'Forget key' }))
    expect(screen.getByLabelText('Admin key')).toBeTruthy()
    expect(sessionStorage.getItem('ai-booking-agent.admin-key')).toBeNull()
  })
})
