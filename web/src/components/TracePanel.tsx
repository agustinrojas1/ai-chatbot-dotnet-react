import { useEffect, useMemo, useRef, useState, type ReactNode, type Ref } from 'react'
import type { RunState } from '../state/chatReducer'
import {
  buildRunView,
  formatCost,
  formatDuration,
  formatTokens,
  sessionTotals,
  type RunTotalsView,
  type RunView,
  type StepTone,
  type TraceStep,
} from '../lib/trace'
import { AlertIcon, CloseIcon, ShieldIcon, SparkIcon, TraceIcon, UserIcon, WrenchIcon } from './icons'
import { JsonViewer } from './JsonViewer'

const STICK_THRESHOLD_PX = 80

const TONES: Record<StepTone, { badge: string; card: string; icon: typeof SparkIcon }> = {
  model: { badge: 'bg-sky-100 text-sky-900 ring-sky-300', card: 'border-slate-200 bg-white', icon: SparkIcon },
  tool: { badge: 'bg-violet-100 text-violet-900 ring-violet-300', card: 'border-slate-200 bg-white', icon: WrenchIcon },
  guardrail: { badge: 'bg-amber-100 text-amber-900 ring-amber-300', card: 'border-amber-300 bg-amber-50', icon: ShieldIcon },
  handoff: { badge: 'bg-emerald-100 text-emerald-900 ring-emerald-300', card: 'border-emerald-300 bg-emerald-50', icon: UserIcon },
  error: { badge: 'bg-red-100 text-red-900 ring-red-300', card: 'border-red-300 bg-red-50', icon: AlertIcon },
}

const STATUS_BADGE: Record<RunView['status'], string> = {
  running: 'bg-sky-100 text-sky-900',
  ok: 'bg-emerald-100 text-emerald-900',
  error: 'bg-red-100 text-red-900',
}

/** The rail every step hangs on: a numbered node and, below it, the line down to the next step. */
function Rail({ children, line }: { children: ReactNode; line: boolean }) {
  return (
    <li className="relative pb-3 pl-9 last:pb-0">
      {line && <span aria-hidden="true" className="absolute bottom-0 left-[11px] top-6 w-px bg-slate-300" />}
      {children}
    </li>
  )
}

function StepItem({ step, line }: { step: TraceStep; line: boolean }) {
  const tone = TONES[step.tone]
  const Icon = tone.icon
  return (
    <Rail line={line}>
      <span
        aria-hidden="true"
        className={`absolute left-0 top-0 flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-bold ring-1 ${tone.badge}`}
      >
        {step.number}
      </span>
      <div className={`rounded-lg border px-2.5 py-2 ${tone.card}`} data-testid="trace-step" data-kind={step.kind}>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="sr-only">Step {step.number}: </span>
          <Icon size={14} className={step.tone === 'error' ? 'text-red-800' : 'text-slate-700'} />
          <h4 className="text-[13px] font-semibold text-slate-900">{step.label}</h4>
          {step.failed && step.tone !== 'error' && (
            <span className="rounded bg-red-100 px-1.5 py-px text-[10px] font-semibold text-red-900">failed</span>
          )}
          {step.chips.map((chip) => (
            <span key={chip} className="rounded bg-slate-100 px-1.5 py-px text-[11px] text-slate-800">
              {chip}
            </span>
          ))}
        </div>
        <p className="mt-1 text-[12px] leading-snug text-slate-800">{step.summary}</p>
        {step.excerpt && (
          <blockquote className="mt-1 border-l-2 border-slate-300 pl-2 text-[12px] italic leading-snug text-slate-700">
            {step.excerpt}
          </blockquote>
        )}
        {step.sections.length > 0 && (
          <div className="mt-1.5 space-y-0.5">
            {step.sections.map((section) => (
              <JsonViewer key={section.label} label={section.label} value={section.value} />
            ))}
          </div>
        )}
      </div>
    </Rail>
  )
}

function RunningStep({ number }: { number: number }) {
  return (
    <Rail line={false}>
      <span
        aria-hidden="true"
        className="trace-pulse absolute left-0 top-0 flex h-6 w-6 items-center justify-center rounded-full bg-sky-600 text-[11px] font-bold text-white"
      >
        {number}
      </span>
      <div
        role="status"
        data-testid="trace-running"
        className="trace-pulse rounded-lg border border-dashed border-sky-400 bg-sky-50 px-2.5 py-2 text-[12px] font-medium text-sky-900"
      >
        Running… the agent is working on the next step
      </div>
    </Rail>
  )
}

function Totals({ totals }: { totals: RunTotalsView }) {
  const cell = (label: string, value: string, title?: string) => (
    <div className="min-w-0" title={title}>
      <dt className="text-[10px] uppercase tracking-wide text-slate-600">{label}</dt>
      <dd className="text-[12px] font-semibold tabular-nums text-slate-900">{value}</dd>
    </div>
  )
  return (
    <div data-testid="run-totals" className="mt-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
      <h4 className="mb-1 text-[11px] font-semibold text-slate-700">
        {totals.failed ? 'Run totals (failed run, up to the failure)' : 'Run totals'}
      </h4>
      <dl className="grid grid-cols-4 gap-2 max-[420px]:grid-cols-2">
        {cell('Tokens in', formatTokens(totals.tokensIn))}
        {cell('Tokens out', formatTokens(totals.tokensOut))}
        {cell('Cost', formatCost(totals.costUsd), `$${totals.costUsd}`)}
        {cell('Duration', formatDuration(totals.durationMs))}
      </dl>
      {totals.model && <p className="mt-1.5 break-words text-[11px] text-slate-700">{totals.failed ? 'Last model' : 'Answered by'} {totals.model}</p>}
    </div>
  )
}

function RunCard({ view, index }: { view: RunView; index: number }) {
  const headingId = `trace-run-${view.runId}`
  return (
    <section aria-labelledby={headingId} data-testid="trace-run" data-status={view.status} className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div className="mb-2 flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <p className="text-[10px] font-semibold uppercase tracking-wide text-slate-600">Message {index + 1}</p>
          <h3 id={headingId} className="line-clamp-3 break-words text-[13px] font-semibold leading-snug text-slate-900">
            {view.title || 'Message'}
          </h3>
        </div>
        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${STATUS_BADGE[view.status]}`}>
          {view.statusLabel}
        </span>
      </div>

      {view.steps.length === 0 && view.status !== 'running' && (
        <p className="text-[12px] text-slate-700">No steps were recorded for this message.</p>
      )}
      <ol className="list-none">
        {view.steps.map((step, i) => (
          <StepItem key={step.key} step={step} line={i < view.steps.length - 1 || view.status === 'running'} />
        ))}
        {view.status === 'running' && <RunningStep number={view.steps.length + 1} />}
      </ol>

      {view.totals && <Totals totals={view.totals} />}
    </section>
  )
}

interface TracePanelProps {
  runs: RunState[]
  onClose: () => void
  /** Keep the newest step in view as events arrive. Off for a finished past session, which is read from the top. */
  follow?: boolean
  /** Focus target when the panel opens and for Esc handling by the parent. */
  ref?: Ref<HTMLElement>
}

/**
 * "Show agent trace": one timeline per user message with every model call, tool call, guardrail,
 * handoff and error, updated live from the SSE `trace` events. Desktop = drawer, mobile = bottom sheet.
 */
export function TracePanel({ runs, onClose, follow = true, ref }: TracePanelProps) {
  const views = useMemo(() => runs.map(buildRunView), [runs])
  const totals = useMemo(() => sessionTotals(runs), [runs])
  const scrollRef = useRef<HTMLDivElement>(null)
  const stickToBottom = useRef(true)
  const [showJump, setShowJump] = useState(false)

  const onScroll = () => {
    const el = scrollRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < STICK_THRESHOLD_PX
    stickToBottom.current = atBottom
    setShowJump(!atBottom)
  }

  // The panel can mount before the session it shows has loaded: follow/not follow is decided by the latest prop.
  useEffect(() => {
    stickToBottom.current = follow
    if (!follow && scrollRef.current) scrollRef.current.scrollTop = 0
  }, [follow])

  // Follow the newest step as events arrive, unless the reader scrolled up to look at something.
  useEffect(() => {
    const el = scrollRef.current
    if (el && stickToBottom.current) el.scrollTop = el.scrollHeight
  }, [runs])

  const jumpToLatest = () => {
    const el = scrollRef.current
    if (!el) return
    stickToBottom.current = true
    setShowJump(false)
    el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' })
  }

  return (
    <aside
      ref={ref}
      id="trace-panel"
      data-testid="trace-panel"
      aria-label="Agent trace"
      tabIndex={-1}
      className="trace-sheet fixed inset-x-0 bottom-0 z-20 flex h-[68dvh] flex-col overflow-hidden rounded-t-2xl bg-slate-100 shadow-frame outline-none lg:static lg:h-[min(860px,calc(100dvh-5rem))] lg:w-[420px] lg:shrink-0 lg:rounded-[28px] lg:ring-1 lg:ring-black/10"
    >
      <header className="border-b border-slate-200 bg-white px-4 pb-2.5 pt-2">
        <div aria-hidden="true" className="mx-auto mb-2 h-1 w-10 rounded-full bg-slate-300 lg:hidden" />
        <div className="flex items-center gap-2">
          <TraceIcon size={18} className="text-whatsapp-teal" />
          <h2 className="flex-1 text-[15px] font-semibold text-slate-900">Agent trace</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close agent trace"
            className="flex h-8 w-8 items-center justify-center rounded-full text-slate-700 transition hover:bg-slate-100"
          >
            <CloseIcon size={18} />
          </button>
        </div>
        <p className="mt-0.5 text-[12px] leading-snug text-slate-700">
          Every step the AI took to answer: model calls, tools it used, and what they returned.
        </p>
        <p data-testid="session-totals" className="mt-2 rounded-md bg-slate-100 px-2.5 py-1.5 text-[12px] tabular-nums text-slate-800">
          <span className="font-semibold">Session:</span> {totals.runs} {totals.runs === 1 ? 'run' : 'runs'} ·{' '}
          {formatTokens(totals.tokensIn)} in / {formatTokens(totals.tokensOut)} out tokens ·{' '}
          <span title={`$${totals.costUsd}`}>{formatCost(totals.costUsd)}</span>
        </p>
      </header>

      <div className="relative min-h-0 flex-1">
        <div
          ref={scrollRef}
          onScroll={onScroll}
          tabIndex={0}
          role="region"
          aria-label="Agent steps"
          className="chat-scroll h-full space-y-3 overflow-y-auto overscroll-contain px-3 py-3"
        >
          {views.length === 0 ? (
            <p data-testid="trace-empty" className="px-2 py-8 text-center text-[13px] leading-snug text-slate-700">
              Nothing yet. Send a message and every step the agent takes will appear here, live.
            </p>
          ) : (
            views.map((view, index) => <RunCard key={view.runId} view={view} index={index} />)
          )}
        </div>
        {showJump && (
          <button
            type="button"
            onClick={jumpToLatest}
            className="absolute bottom-3 left-1/2 -translate-x-1/2 rounded-full bg-whatsapp-teal px-3 py-1.5 text-[12px] font-semibold text-white shadow-lg transition hover:bg-whatsapp-teal-light"
          >
            Jump to latest
          </button>
        )}
      </div>
    </aside>
  )
}
