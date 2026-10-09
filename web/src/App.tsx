import { useCallback, useEffect, useRef, useState } from 'react'
import { BannerBar } from './components/BannerBar'
import { ChatHeader } from './components/ChatHeader'
import { Composer } from './components/Composer'
import { Footer } from './components/Footer'
import { MessageList } from './components/MessageList'
import { NicheBar } from './components/NicheBar'
import { TracePanel } from './components/TracePanel'
import { useChat } from './hooks/useChat'
import { viewedSessionFromSearch } from './lib/route'
import { canSend, canType } from './state/chatReducer'

function placeholderFor(state: ReturnType<typeof useChat>['state']): string {
  if (state.readOnly) return 'Read-only: this is a past session'
  if (state.sessionStatus === 'AwaitingHuman') return 'A human will continue this conversation'
  if (state.sessionStatus === 'Closed') return 'This conversation is closed'
  if (state.banner?.kind === 'session_ended' || (state.banner?.kind === 'rate_limit' && state.banner.code === 'session_limit')) {
    return 'Restart the demo to keep chatting'
  }
  return 'Type a message'
}

/** The trace sits beside the chat from this width up; below it, it is a bottom sheet. */
const isDesktop = () => window.matchMedia?.('(min-width: 1024px)').matches ?? false

export default function App() {
  // `/?session=<id>` (from the admin page) opens a past session read-only with the trace visible.
  const [viewed] = useState(() => viewedSessionFromSearch(window.location.search))
  const { state, send, retry, switchNiche, restart, reload, hydrateTrace } = useChat(viewed)
  const [traceOpen, setTraceOpen] = useState(viewed !== null)
  const panelRef = useRef<HTMLElement>(null)
  const toggleRef = useRef<HTMLButtonElement>(null)

  const closeTrace = useCallback(() => {
    setTraceOpen(false)
    toggleRef.current?.focus()
  }, [])

  // Opening the panel: ask the server for runs this page may have missed, and on a phone move focus
  // into the sheet (it covers the composer). On desktop focus stays where the user was typing.
  const sessionId = state.sessionId
  useEffect(() => {
    if (!traceOpen || !sessionId) return
    void hydrateTrace()
  }, [traceOpen, sessionId, hydrateTrace])
  useEffect(() => {
    if (traceOpen && !isDesktop()) panelRef.current?.focus()
  }, [traceOpen])

  // Esc closes the sheet on mobile; on desktop only while the user is inside the drawer.
  useEffect(() => {
    if (!traceOpen) return
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      if (!isDesktop() || panelRef.current?.contains(document.activeElement)) closeTrace()
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [traceOpen, closeTrace])

  const viewing = viewed !== null
  const niche = state.niches.find((n) => n.id === state.nicheId) ?? null

  return (
    <main className="flex min-h-dvh items-center justify-center gap-6 bg-whatsapp-dark sm:px-6 sm:py-10">
      <section className="flex h-dvh w-full max-w-[430px] flex-col overflow-hidden bg-white sm:h-[min(860px,calc(100dvh-5rem))] sm:rounded-[28px] sm:shadow-frame sm:ring-1 sm:ring-black/10">
        <NicheBar niches={state.niches} nicheId={state.nicheId} onChange={switchNiche} locked={viewing} />
        <ChatHeader
          niche={niche}
          typing={state.pending}
          sessionStatus={state.sessionStatus}
          traceOpen={traceOpen}
          onToggleTrace={() => (traceOpen ? closeTrace() : setTraceOpen(true))}
          readOnly={viewing}
          traceButtonRef={toggleRef}
        />

        {state.phase === 'ready' && (
          <MessageList
            niche={niche}
            messages={state.messages}
            pending={state.pending}
            quickRepliesEnabled={canSend(state)}
            onQuickReply={(text) => void send(text)}
          />
        )}
        {state.phase === 'loading' && (
          <div className="whatsapp-wallpaper flex flex-1 items-center justify-center px-6 text-center text-[13px] text-whatsapp-meta" role="status">
            Connecting to the assistant…
          </div>
        )}
        {state.phase === 'failed' && (
          <div className="whatsapp-wallpaper flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center" role="alert">
            <p className="max-w-[280px] rounded-lg bg-error-bg px-3 py-2 text-xs text-error-text">
              {viewing ? 'Could not open that session.' : 'Could not start the demo.'} {state.failure}
            </p>
            <button
              type="button"
              onClick={reload}
              className="rounded-full bg-whatsapp-teal px-4 py-1.5 text-xs font-semibold text-white transition hover:bg-whatsapp-teal-light"
            >
              Try again
            </button>
          </div>
        )}

        {state.readOnly && (
          <div
            role="status"
            data-testid="readonly-banner"
            className="flex items-center justify-center gap-2 border-t border-black/5 bg-notice-bg px-3 py-2 text-center text-notice-text"
          >
            <p className="text-[12px] font-medium">Viewing a past session</p>
            <a
              href="/"
              className="shrink-0 rounded-full bg-whatsapp-teal px-3 py-1 text-[11px] font-semibold text-white transition hover:bg-whatsapp-teal-light"
            >
              Start a new chat
            </a>
          </div>
        )}
        {state.banner && (
          <BannerBar banner={state.banner} sessionStatus={state.sessionStatus} onRetry={retry} onRestart={restart} />
        )}

        <Composer enabled={canType(state)} pending={state.pending} placeholder={placeholderFor(state)} onSend={(text) => void send(text)} />
        <Footer />
      </section>

      {traceOpen && state.phase !== 'failed' && (
        <>
          <div aria-hidden="true" onClick={closeTrace} className="fixed inset-0 z-10 bg-black/40 lg:hidden" />
          <TracePanel ref={panelRef} runs={state.runs} onClose={closeTrace} follow={!state.readOnly} />
        </>
      )}
    </main>
  )
}
