import { useEffect, useRef } from 'react'
import type { Niche } from '../api/types'
import type { ChatMessage } from '../state/chatReducer'
import { Avatar } from './Avatar'
import { MessageBubble } from './MessageBubble'
import { TypingIndicator } from './TypingIndicator'

interface MessageListProps {
  niche: Niche | null
  messages: ChatMessage[]
  pending: boolean
  quickRepliesEnabled: boolean
  onQuickReply: (text: string) => void
}

const STICK_THRESHOLD_PX = 120

export function MessageList({
  niche,
  messages,
  pending,
  quickRepliesEnabled,
  onQuickReply,
}: MessageListProps) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const stickToBottom = useRef(true)

  // A bubble without text yet is not shown: the typing indicator stands in for it.
  const visible = messages.filter((m) => m.role === 'system' || m.text !== '')
  const last = visible.at(-1)
  const typing = pending && !(last?.role === 'assistant' && last.streaming)

  const onScroll = () => {
    const el = scrollRef.current
    if (el) stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < STICK_THRESHOLD_PX
  }

  // Keep the view pinned to the latest message as tokens arrive, unless the reader scrolled up.
  useEffect(() => {
    const el = scrollRef.current
    if (el && stickToBottom.current) el.scrollTop = el.scrollHeight
  }, [messages, typing])

  const empty = messages.length === 0

  return (
    <div
      ref={scrollRef}
      onScroll={onScroll}
      role="log"
      aria-live="polite"
      aria-relevant="additions text"
      aria-busy={pending}
      aria-label="Conversation"
      tabIndex={0}
      className="whatsapp-wallpaper chat-scroll flex-1 space-y-1.5 overflow-y-auto px-3 py-3"
    >
      {empty && niche && (
        <div className="flex flex-col items-center gap-3 px-4 py-10 text-center">
          <div className="rounded-full bg-white/90 p-2 shadow-bubble">
            <Avatar avatar={niche.avatar} />
          </div>
          <p className="max-w-[270px] text-[13px] leading-snug text-whatsapp-meta">{niche.greeting}</p>
          <div className="mt-1 flex flex-wrap justify-center gap-2">
            {niche.quickReplies.map((text) => (
              <button
                key={text}
                type="button"
                onClick={() => onQuickReply(text)}
                disabled={!quickRepliesEnabled}
                className="rounded-full border border-whatsapp-teal/20 bg-white/90 px-3 py-1.5 text-xs font-medium text-whatsapp-teal shadow-sm transition hover:bg-white disabled:cursor-not-allowed disabled:opacity-50"
              >
                {text}
              </button>
            ))}
          </div>
        </div>
      )}

      {visible.map((message) => (
        <MessageBubble key={message.id} message={message} avatar={niche?.avatar ?? null} />
      ))}

      {typing && <TypingIndicator avatar={niche?.avatar ?? null} />}
    </div>
  )
}
