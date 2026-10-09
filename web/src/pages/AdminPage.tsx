import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { getAdminBookings, getAdminHandoffs } from '../api/client'
import { ApiError } from '../api/errors'
import type { AdminBooking, AdminHandoff } from '../api/types'
import { Footer } from '../components/Footer'

const KEY_STORAGE = 'ai-booking-agent.admin-key'

const NICHE_FILTERS: { id: string | null; label: string }[] = [
  { id: null, label: 'All niches' },
  { id: 'realestate', label: 'Real estate' },
  { id: 'dental', label: 'Dental clinic' },
  { id: 'restaurant', label: 'Restaurant' },
]

const NICHE_LABELS: Record<string, string> = {
  realestate: 'Real estate',
  dental: 'Dental clinic',
  restaurant: 'Restaurant',
}

/** The key lives in sessionStorage only: it is gone when the tab closes. */
function readStoredKey(): string | null {
  try {
    return sessionStorage.getItem(KEY_STORAGE)
  } catch {
    return null
  }
}

function storeKey(key: string | null): void {
  try {
    if (key === null) sessionStorage.removeItem(KEY_STORAGE)
    else sessionStorage.setItem(KEY_STORAGE, key)
  } catch {
    // Storage unavailable: the key only lasts until the page is reloaded.
  }
}

type Outcome =
  | { kind: 'ok'; bookings: AdminBooking[]; handoffs: AdminHandoff[] }
  | { kind: 'disabled' }
  | { kind: 'failed'; message: string }

function formatDateTime(iso: string | null): string {
  if (!iso) return '—'
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' })
}

/** Order lines of a restaurant booking as "2× Pepperoni, 1× Margherita"; anything unexpected is skipped. */
function summarizeItems(items: unknown): string | null {
  if (!Array.isArray(items)) return null
  const lines = items.flatMap((item): string[] => {
    if (typeof item !== 'object' || item === null) return []
    const { name, qty } = item as Record<string, unknown>
    return typeof name === 'string' ? [typeof qty === 'number' ? `${qty}× ${name}` : name] : []
  })
  return lines.length > 0 ? lines.join(', ') : null
}

function bookingWhat(booking: AdminBooking): string {
  const parts = [
    booking.offeringName,
    booking.slotStartsAt ? formatDateTime(booking.slotStartsAt) : null,
    summarizeItems(booking.items),
  ].filter((part): part is string => part !== null)
  return parts.length > 0 ? parts.join(' · ') : '—'
}

function traceHref(sessionId: string, niche: string): string {
  return `/?session=${encodeURIComponent(sessionId)}&niche=${encodeURIComponent(niche)}`
}

const STATUS_STYLES: Record<string, string> = {
  PendingConfirmation: 'bg-amber-100 text-amber-900',
  Open: 'bg-amber-100 text-amber-900',
  Confirmed: 'bg-emerald-100 text-emerald-900',
}

function StatusBadge({ status }: { status: string }) {
  const label = status.replace(/([a-z])([A-Z])/g, (_, a: string, b: string) => `${a} ${b.toLowerCase()}`)
  return (
    <span className={`whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-semibold ${STATUS_STYLES[status] ?? 'bg-slate-200 text-slate-900'}`}>
      {label}
    </span>
  )
}

const th = 'whitespace-nowrap px-3 py-2 text-left text-[11px] font-semibold uppercase tracking-wide text-slate-700'
const td = 'px-3 py-2 align-top text-[13px] text-slate-900'

function TraceLink({ sessionId, niche }: { sessionId: string; niche: string }) {
  return (
    <a
      href={traceHref(sessionId, niche)}
      className="whitespace-nowrap font-medium text-whatsapp-teal underline-offset-2 hover:underline"
    >
      View trace
    </a>
  )
}

function TableCard({
  title,
  count,
  loading,
  emptyText,
  children,
}: {
  title: string
  count: number
  loading: boolean
  emptyText: string
  children: ReactNode
}) {
  return (
    <section aria-labelledby={`h-${title}`} className="rounded-xl bg-white shadow-sm ring-1 ring-black/5">
      <h2 id={`h-${title}`} className="px-4 pt-3 text-[15px] font-semibold text-slate-900">
        {title}
        {!loading && <span className="ml-2 text-[12px] font-normal text-slate-700">({count})</span>}
      </h2>
      {loading ? (
        <p role="status" className="px-4 py-8 text-center text-[13px] text-slate-700">
          Loading {title.toLowerCase()}…
        </p>
      ) : count === 0 ? (
        <p className="px-4 py-8 text-center text-[13px] text-slate-700">{emptyText}</p>
      ) : (
        <div className="overflow-x-auto px-1 pb-2 pt-1">{children}</div>
      )}
    </section>
  )
}

function BookingsTable({ rows }: { rows: AdminBooking[] }) {
  return (
    <table className="w-full min-w-[820px] border-collapse">
      <thead className="border-b border-slate-200">
        <tr>
          <th scope="col" className={th}>Created</th>
          <th scope="col" className={th}>Niche</th>
          <th scope="col" className={th}>Customer</th>
          <th scope="col" className={th}>Phone</th>
          <th scope="col" className={th}>Status</th>
          <th scope="col" className={th}>Offering / slot / items</th>
          <th scope="col" className={th}>Total</th>
          <th scope="col" className={th}>Notes</th>
          <th scope="col" className={th}>Session</th>
        </tr>
      </thead>
      <tbody className="divide-y divide-slate-100">
        {rows.map((b) => (
          <tr key={b.id} data-testid="booking-row">
            <td className={`${td} whitespace-nowrap`}>{formatDateTime(b.createdAt)}</td>
            <td className={`${td} whitespace-nowrap`}>{NICHE_LABELS[b.niche] ?? b.niche}</td>
            <td className={td}>{b.customerName}</td>
            <td className={`${td} whitespace-nowrap tabular-nums`}>{b.customerPhone}</td>
            <td className={td}><StatusBadge status={b.status} /></td>
            <td className={td}>{bookingWhat(b)}</td>
            <td className={`${td} tabular-nums`}>{b.total === null ? '—' : b.total.toFixed(2)}</td>
            <td className={td}>{b.notes ?? '—'}</td>
            <td className={td}><TraceLink sessionId={b.sessionId} niche={b.niche} /></td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function HandoffsTable({ rows }: { rows: AdminHandoff[] }) {
  return (
    <table className="w-full min-w-[720px] border-collapse">
      <thead className="border-b border-slate-200">
        <tr>
          <th scope="col" className={th}>Created</th>
          <th scope="col" className={th}>Niche</th>
          <th scope="col" className={th}>Reason</th>
          <th scope="col" className={th}>Summary</th>
          <th scope="col" className={th}>Status</th>
          <th scope="col" className={th}>Session</th>
        </tr>
      </thead>
      <tbody className="divide-y divide-slate-100">
        {rows.map((h) => (
          <tr key={h.id} data-testid="handoff-row">
            <td className={`${td} whitespace-nowrap`}>{formatDateTime(h.createdAt)}</td>
            <td className={`${td} whitespace-nowrap`}>{NICHE_LABELS[h.niche] ?? h.niche}</td>
            <td className={td}>{h.reason}</td>
            <td className={td}>{h.summary ?? '—'}</td>
            <td className={td}><StatusBadge status={h.status} /></td>
            <td className={td}><TraceLink sessionId={h.sessionId} niche={h.niche} /></td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function KeyForm({ rejected, onSubmit }: { rejected: boolean; onSubmit: (key: string) => void }) {
  const [value, setValue] = useState('')
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const key = value.trim()
    if (key) onSubmit(key)
  }
  return (
    <form onSubmit={submit} className="mx-auto mt-6 max-w-sm rounded-xl bg-white p-5 shadow-sm ring-1 ring-black/5">
      <h2 className="text-[15px] font-semibold text-slate-900">Enter the admin key</h2>
      <p className="mt-1 text-[12px] leading-snug text-slate-700">
        The key is set by whoever runs this demo (<code className="rounded bg-slate-100 px-1">ADMIN_KEY</code>).
      </p>
      <label htmlFor="admin-key" className="mt-3 block text-[12px] font-medium text-slate-800">
        Admin key
      </label>
      <input
        id="admin-key"
        type="password"
        autoComplete="off"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        aria-invalid={rejected}
        aria-describedby={rejected ? 'admin-key-error' : undefined}
        className="mt-1 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-[14px] text-slate-900"
      />
      {rejected && (
        <p id="admin-key-error" role="alert" className="mt-2 rounded-md bg-error-bg px-2.5 py-1.5 text-[12px] font-medium text-error-text">
          That key was not accepted. Check it and try again.
        </p>
      )}
      <button
        type="submit"
        disabled={value.trim() === ''}
        className="mt-3 w-full rounded-full bg-whatsapp-teal px-4 py-2 text-[13px] font-semibold text-white transition hover:bg-whatsapp-teal-light disabled:cursor-not-allowed disabled:opacity-50"
      >
        Show data
      </button>
    </form>
  )
}

export default function AdminPage() {
  const [key, setKey] = useState<string | null>(readStoredKey)
  const [rejected, setRejected] = useState(false)
  const [niche, setNiche] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [result, setResult] = useState<{ signature: string; outcome: Outcome } | null>(null)

  const signature = key === null ? null : `${key}|${niche ?? ''}|${attempt}`

  useEffect(() => {
    if (key === null || signature === null) return
    const controller = new AbortController()
    Promise.all([getAdminBookings(key, niche, controller.signal), getAdminHandoffs(key, niche, controller.signal)])
      .then(([bookings, handoffs]): Outcome => ({ kind: 'ok', bookings, handoffs }))
      .catch((error: unknown): Outcome | null => {
        if (controller.signal.aborted) return null
        if (error instanceof ApiError && error.status === 401) {
          storeKey(null)
          setKey(null)
          setRejected(true)
          return null
        }
        if (error instanceof ApiError && error.status === 503) return { kind: 'disabled' }
        return {
          kind: 'failed',
          message: error instanceof ApiError ? error.message : 'Something went wrong while loading the data.',
        }
      })
      .then((outcome) => {
        if (outcome) setResult({ signature, outcome })
      })
    return () => controller.abort()
  }, [key, niche, signature])

  const submitKey = (value: string) => {
    storeKey(value)
    setRejected(false)
    setKey(value)
  }

  const lock = () => {
    storeKey(null)
    setKey(null)
    setRejected(false)
    setResult(null)
  }

  const outcome = result !== null && result.signature === signature ? result.outcome : null
  const loading = key !== null && outcome === null
  const ok = outcome?.kind === 'ok' ? outcome : null

  return (
    <div className="flex min-h-dvh flex-col bg-slate-100 text-slate-900">
      <header className="bg-whatsapp-header px-4 py-3 text-white">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-4 gap-y-1">
          <h1 className="text-[16px] font-semibold">Admin · bookings &amp; handoffs</h1>
          <span className="flex-1" />
          {key !== null && (
            <button
              type="button"
              onClick={lock}
              className="rounded-full border border-white/40 px-3 py-1 text-[12px] font-medium text-white transition hover:bg-white/10"
            >
              Forget key
            </button>
          )}
          <a href="/" className="rounded-full bg-white px-3 py-1 text-[12px] font-semibold text-whatsapp-teal transition hover:bg-whatsapp-user-bubble">
            ← Back to the demo
          </a>
        </div>
      </header>

      <main className="mx-auto w-full max-w-6xl flex-1 space-y-4 px-4 py-5">
        <p role="note" data-testid="demo-note" className="rounded-lg bg-notice-bg px-3 py-2 text-[12px] leading-snug text-notice-text">
          Demo-grade protection: one shared key sent in an <code>X-Admin-Key</code> header, kept only in this tab&apos;s
          session storage. Do not reuse a real secret here. These tables are read-only.
        </p>

        {key === null && <KeyForm rejected={rejected} onSubmit={submitKey} />}

        {key !== null && outcome?.kind === 'disabled' && (
          <div role="alert" className="rounded-xl bg-white p-5 text-center shadow-sm ring-1 ring-black/5">
            <h2 className="text-[15px] font-semibold text-slate-900">Admin is disabled on this server</h2>
            <p className="mt-1 text-[13px] text-slate-700">
              No admin key is configured (<code className="rounded bg-slate-100 px-1">ADMIN_KEY</code> is empty), so these
              endpoints are switched off.
            </p>
          </div>
        )}

        {key !== null && outcome?.kind === 'failed' && (
          <div role="alert" className="rounded-xl bg-error-bg p-4 text-center text-error-text">
            <p className="text-[13px] font-medium">{outcome.message}</p>
            <button
              type="button"
              onClick={() => setAttempt((n) => n + 1)}
              className="mt-2 rounded-full bg-whatsapp-teal px-4 py-1.5 text-[12px] font-semibold text-white transition hover:bg-whatsapp-teal-light"
            >
              Try again
            </button>
          </div>
        )}

        {key !== null && outcome?.kind !== 'disabled' && outcome?.kind !== 'failed' && (
          <>
            <div role="group" aria-label="Filter by niche" className="flex flex-wrap gap-2">
              {NICHE_FILTERS.map((f) => (
                <button
                  key={f.id ?? 'all'}
                  type="button"
                  onClick={() => setNiche(f.id)}
                  aria-pressed={niche === f.id}
                  className="rounded-full border border-slate-300 bg-white px-3 py-1.5 text-[12px] font-medium text-slate-800 transition hover:bg-slate-50 aria-pressed:border-whatsapp-teal aria-pressed:bg-whatsapp-teal aria-pressed:text-white"
                >
                  {f.label}
                </button>
              ))}
            </div>

            <TableCard title="Bookings" count={ok?.bookings.length ?? 0} loading={loading} emptyText="No bookings yet. They show up here when the agent registers one.">
              {ok && <BookingsTable rows={ok.bookings} />}
            </TableCard>
            <TableCard title="Handoffs" count={ok?.handoffs.length ?? 0} loading={loading} emptyText="No handoffs yet. They show up here when the agent passes a conversation to a person.">
              {ok && <HandoffsTable rows={ok.handoffs} />}
            </TableCard>
            <p className="text-[11px] text-slate-700">Newest first, at most 200 rows per table.</p>
          </>
        )}
      </main>

      <Footer />
    </div>
  )
}
