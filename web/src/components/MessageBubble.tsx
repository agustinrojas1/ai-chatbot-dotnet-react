import type { Niche } from '../api/types'
import type { ChatMessage } from '../state/chatReducer'
import { Avatar } from './Avatar'
import { FormattedText } from './FormattedText'
import { CheckCheckIcon } from './icons'

function formatTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

interface MessageBubbleProps {
  message: ChatMessage
  avatar: Niche['avatar'] | null
}

export function MessageBubble({ message, avatar }: MessageBubbleProps) {
  if (message.role === 'system') {
    return (
      <div className="flex justify-center py-1">
        <div className="rounded-lg bg-notice-bg px-3 py-1.5 text-center text-[11px] font-medium text-notice-text shadow-sm">
          🔀 {message.text}
        </div>
      </div>
    )
  }

  const isUser = message.role === 'user'
  return (
    <div className={`flex gap-1.5 ${isUser ? 'justify-end' : 'justify-start'}`}>
      {!isUser && <Avatar avatar={avatar} size="xs" />}
      <div
        className={`max-w-[82%] px-2.5 py-1.5 shadow-bubble ${
          isUser
            ? 'rounded-lg rounded-tr-none bg-whatsapp-user-bubble'
            : 'rounded-lg rounded-tl-none bg-whatsapp-bot-bubble'
        }`}
        data-role={message.role}
      >
        <p className="whitespace-pre-wrap break-words text-[14px] leading-[1.35] text-whatsapp-text">
          <FormattedText text={message.text} />
        </p>
        <div className="mt-0.5 flex items-center justify-end gap-1 text-[10px] text-whatsapp-meta">
          <time dateTime={message.createdAt}>{formatTime(message.createdAt)}</time>
          {isUser && <CheckCheckIcon size={14} className="text-whatsapp-check" />}
        </div>
      </div>
    </div>
  )
}
