import type { Ref } from 'react'
import type { Niche, SessionStatus } from '../api/types'
import { Avatar } from './Avatar'
import { TraceIcon } from './icons'

interface ChatHeaderProps {
  niche: Niche | null
  typing: boolean
  sessionStatus: SessionStatus
  traceOpen: boolean
  onToggleTrace: () => void
  /** Past session opened from the admin page. */
  readOnly?: boolean
  traceButtonRef?: Ref<HTMLButtonElement>
}

export function ChatHeader({
  niche,
  typing,
  sessionStatus,
  traceOpen,
  onToggleTrace,
  readOnly = false,
  traceButtonRef,
}: ChatHeaderProps) {
  const handoff = sessionStatus === 'AwaitingHuman'
  const status = readOnly ? 'read-only' : handoff ? 'Transferred to staff 👤' : typing ? 'typing…' : 'online'
  const statusClass = handoff
    ? 'font-semibold text-white'
    : typing
      ? 'font-medium text-whatsapp-user-bubble'
      : 'text-white'

  return (
    <header className="flex items-center gap-3 bg-whatsapp-header px-4 py-2.5 text-white">
      <Avatar avatar={niche?.avatar ?? null} />
      <div className="min-w-0 flex-1">
        <h1 className="truncate text-[15px] font-semibold leading-tight">{niche?.headerName ?? (readOnly ? 'Past session' : 'Connecting…')}</h1>
        <p className={`text-xs leading-tight ${statusClass}`} data-testid="chat-status">
          {status}
        </p>
      </div>
      <button
        ref={traceButtonRef}
        type="button"
        onClick={onToggleTrace}
        aria-pressed={traceOpen}
        aria-controls="trace-panel"
        aria-label={traceOpen ? 'Hide agent trace' : 'Show agent trace'}
        className="flex shrink-0 items-center gap-1.5 rounded-full border border-white/40 px-3 py-1.5 text-xs font-medium text-white transition hover:bg-white/10 aria-pressed:bg-white aria-pressed:text-whatsapp-teal"
      >
        <TraceIcon size={16} />
        <span className="sm:hidden">Trace</span>
        <span className="hidden sm:inline">{traceOpen ? 'Hide agent trace' : 'Show agent trace'}</span>
      </button>
    </header>
  )
}
