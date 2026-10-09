import type { Niche } from '../api/types'
import { Avatar } from './Avatar'

export function TypingIndicator({ avatar }: { avatar: Niche['avatar'] | null }) {
  return (
    <div className="flex justify-start gap-1.5" data-testid="typing-indicator">
      <Avatar avatar={avatar} size="xs" />
      <div className="flex items-center gap-1 rounded-lg rounded-tl-none bg-whatsapp-bot-bubble px-3 py-3 shadow-bubble">
        <span className="typing-dot h-1.5 w-1.5 rounded-full bg-whatsapp-meta" />
        <span className="typing-dot h-1.5 w-1.5 rounded-full bg-whatsapp-meta" />
        <span className="typing-dot h-1.5 w-1.5 rounded-full bg-whatsapp-meta" />
      </div>
    </div>
  )
}
