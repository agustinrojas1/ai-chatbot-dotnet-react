import { useState } from 'react'
import { BannerBar } from './components/BannerBar'
import { ChatHeader } from './components/ChatHeader'
import { Composer } from './components/Composer'
import { Footer } from './components/Footer'
import { MessageList } from './components/MessageList'
import { NicheBar } from './components/NicheBar'
import { useChat } from './hooks/useChat'
import { canSend, canType } from './state/chatReducer'

function placeholderFor(state: ReturnType<typeof useChat>['state']): string {
  if (state.sessionStatus === 'AwaitingHuman') return 'A human will continue this conversation'
  if (state.sessionStatus === 'Closed') return 'This conversation is closed'
  if (state.banner?.kind === 'session_ended' || (state.banner?.kind === 'rate_limit' && state.banner.code === 'session_limit')) {
    return 'Restart the demo to keep chatting'
  }
  return 'Type a message'
}

export default function App() {
  const { state, send, retry, switchNiche, restart, reload } = useChat()
  const [traceOpen, setTraceOpen] = useState(false)

  const niche = state.niches.find((n) => n.id === state.nicheId) ?? null

  return (
    <main className="flex min-h-dvh items-center justify-center gap-6 bg-whatsapp-dark sm:px-6 sm:py-10">
      <section className="flex h-dvh w-full max-w-[430px] flex-col overflow-hidden bg-white sm:h-[min(860px,calc(100dvh-5rem))] sm:rounded-[28px] sm:shadow-frame sm:ring-1 sm:ring-black/10">
        <NicheBar niches={state.niches} nicheId={state.nicheId} onChange={switchNiche} />
        <ChatHeader
          niche={niche}
          typing={state.pending}
          sessionStatus={state.sessionStatus}
          traceOpen={traceOpen}
          onToggleTrace={() => setTraceOpen((open) => !open)}
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
              Could not start the demo. {state.failure}
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

        {state.banner && (
          <BannerBar banner={state.banner} sessionStatus={state.sessionStatus} onRetry={retry} onRestart={restart} />
        )}

        <Composer enabled={canType(state)} pending={state.pending} placeholder={placeholderFor(state)} onSend={(text) => void send(text)} />
        <Footer />
      </section>

      {traceOpen && (
        <aside
          id="trace-panel"
          data-testid="trace-panel"
          aria-label="Agent trace"
          className="fixed inset-x-0 bottom-0 z-20 h-[55dvh] overflow-y-auto rounded-t-2xl bg-white shadow-frame lg:static lg:h-[min(860px,calc(100dvh-5rem))] lg:w-[420px] lg:shrink-0 lg:rounded-[28px]"
        />
      )}
    </main>
  )
}
