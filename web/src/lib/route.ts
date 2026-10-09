/** A past session to look at instead of starting a chat (`/?session=<id>&niche=<id>`). */
export interface ViewedSession {
  sessionId: string
  /** Only used to dress the header (avatar, name); the API does not return a session's niche. */
  niche: string | null
}

/** The app has two screens, so a pathname check is all the routing it needs. nginx serves index.html for both. */
export function isAdminPath(pathname: string): boolean {
  return pathname.replace(/\/+$/, '') === '/admin'
}

export function viewedSessionFromSearch(search: string): ViewedSession | null {
  const params = new URLSearchParams(search)
  const sessionId = params.get('session')?.trim()
  if (!sessionId) return null
  return { sessionId, niche: params.get('niche')?.trim() || null }
}
