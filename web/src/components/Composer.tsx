import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { MAX_INPUT_CHARS } from '../state/chatReducer'
import { SendIcon } from './icons'

interface ComposerProps {
  /** The text box accepts input (false after a handoff, when the session is over, or while loading). */
  enabled: boolean
  /** An answer is on its way: typing is allowed, sending is not. */
  pending: boolean
  placeholder: string
  onSend: (text: string) => void
}

export function Composer({ enabled, pending, placeholder, onSend }: ComposerProps) {
  const [text, setText] = useState('')
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  // Auto-expand up to ~4 lines (112px), like WhatsApp.
  useEffect(() => {
    const el = textareaRef.current
    if (el) {
      el.style.height = 'auto'
      el.style.height = `${Math.min(el.scrollHeight, 112)}px`
    }
  }, [text])

  const canSubmit = enabled && !pending && text.trim() !== ''

  const submit = () => {
    if (!canSubmit) return
    onSend(text.trim())
    setText('')
  }

  const onSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    submit()
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter sends, Shift+Enter inserts a newline. Ignore Enter while an IME is composing.
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      submit()
    }
  }

  const nearLimit = text.length >= MAX_INPUT_CHARS * 0.9

  return (
    <div className="bg-whatsapp-input px-3 pb-1 pt-2.5">
      <form onSubmit={onSubmit} className="flex w-full items-end gap-2">
        <div className="flex min-w-0 flex-1 items-end rounded-[20px] border border-slate-200/80 bg-white px-4 py-1.5 shadow-sm focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-whatsapp-check">
          <textarea
            ref={textareaRef}
            rows={1}
            value={text}
            maxLength={MAX_INPUT_CHARS}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={placeholder}
            aria-label="Message"
            disabled={!enabled}
            className="max-h-[112px] w-full resize-none overflow-y-auto bg-transparent py-1 text-[14px] leading-[1.4] text-whatsapp-text placeholder-whatsapp-meta outline-none focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-60"
          />
        </div>
        <button
          type="submit"
          aria-label="Send message"
          disabled={!canSubmit}
          className="mb-1 flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-whatsapp-accent text-whatsapp-dark transition hover:bg-whatsapp-accent-bright disabled:cursor-not-allowed disabled:opacity-40"
        >
          <SendIcon size={18} />
        </button>
      </form>
      <p
        className={`h-4 pr-14 text-right text-[10px] ${nearLimit ? 'font-semibold text-error-text' : 'text-whatsapp-meta'}`}
        data-testid="char-counter"
        aria-live="off"
      >
        {text.length > 0 ? `${text.length}/${MAX_INPUT_CHARS}` : ''}
      </p>
    </div>
  )
}
