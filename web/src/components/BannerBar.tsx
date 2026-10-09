import type { Banner, ChatState } from '../state/chatReducer'

function formatWait(seconds: number): string {
  if (seconds < 60) return `${Math.max(1, Math.ceil(seconds))} s`
  return `${Math.ceil(seconds / 60)} min`
}

interface BannerBarProps {
  banner: Banner
  sessionStatus: ChatState['sessionStatus']
  onRetry: () => void
  onRestart: () => void
}

const actionClass =
  'shrink-0 rounded-full bg-whatsapp-teal px-3 py-1 text-[11px] font-semibold text-white transition hover:bg-whatsapp-teal-light'

/** Notice above the input for rate limits and failures, with the action that gets the user unstuck. */
export function BannerBar({ banner, sessionStatus, onRetry, onRestart }: BannerBarProps) {
  const isError = banner.kind === 'error'
  const colors = isError ? 'bg-error-bg text-error-text' : 'bg-notice-bg text-notice-text'

  let action: { label: string; onClick: () => void } | null = null
  let message = banner.message
  if (banner.kind === 'session_ended' || (banner.kind === 'rate_limit' && banner.code === 'session_limit')) {
    action = { label: 'Restart demo', onClick: onRestart }
  } else if (banner.kind === 'rate_limit') {
    if (banner.retryAfterSeconds !== null) message += ` Try again in ${formatWait(banner.retryAfterSeconds)}.`
    if (banner.code === 'ip_rate_limit') action = { label: 'Try again', onClick: onRetry }
  } else if (banner.kind === 'error' && banner.retryable && sessionStatus === 'Active') {
    action = { label: 'Retry', onClick: onRetry }
  }

  return (
    <div
      role="alert"
      data-testid="banner"
      data-kind={banner.kind}
      className={`flex items-center justify-center gap-2 border-t border-black/5 px-3 py-2 text-center ${colors}`}
    >
      <p className="text-[12px] font-medium">{message}</p>
      {action && (
        <button type="button" onClick={action.onClick} className={actionClass}>
          {action.label}
        </button>
      )}
    </div>
  )
}
