const KEY = 'ai-booking-agent.session'

export interface StoredSession {
  sessionId: string
  niche: string
}

/** The session survives a reload: the server keeps the history, the browser only remembers the id. */
export function loadStoredSession(): StoredSession | null {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? 'null')
    if (typeof parsed !== 'object' || parsed === null) return null
    const { sessionId, niche } = parsed as Record<string, unknown>
    return typeof sessionId === 'string' && typeof niche === 'string' ? { sessionId, niche } : null
  } catch {
    return null
  }
}

export function saveStoredSession(session: StoredSession): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(session))
  } catch {
    // Storage unavailable (private mode, quota): the chat still works, it just will not survive a reload.
  }
}
