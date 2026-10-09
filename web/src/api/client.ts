import { ApiError, errorFromResponse } from './errors'
import { readStreamEvents } from './sse'
import type {
  AdminBooking,
  AdminHandoff,
  MessagesResponse,
  Niche,
  SessionCreated,
  StreamEvent,
  TraceResponse,
} from './types'

const BASE = import.meta.env.VITE_API_BASE ?? ''

async function request(path: string, init?: RequestInit): Promise<Response> {
  let response: Response
  try {
    response = await fetch(`${BASE}${path}`, init)
  } catch (error) {
    if (init?.signal?.aborted) throw error
    throw new ApiError('network', 0, 'Could not reach the server. Check your connection and try again.')
  }
  if (!response.ok) throw await errorFromResponse(response)
  return response
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await request(path, { headers: { Accept: 'application/json' }, signal })
  return (await response.json()) as T
}

export function fetchNiches(signal?: AbortSignal): Promise<Niche[]> {
  return getJson<Niche[]>('/api/niches', signal)
}

export async function createSession(niche: string, signal?: AbortSignal): Promise<SessionCreated> {
  const response = await request('/api/sessions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ niche }),
    signal,
  })
  return (await response.json()) as SessionCreated
}

export function getMessages(sessionId: string, signal?: AbortSignal): Promise<MessagesResponse> {
  return getJson<MessagesResponse>(`/api/sessions/${encodeURIComponent(sessionId)}/messages`, signal)
}

export function getTrace(sessionId: string, signal?: AbortSignal): Promise<TraceResponse> {
  return getJson<TraceResponse>(`/api/sessions/${encodeURIComponent(sessionId)}/trace`, signal)
}

function adminPath(resource: 'bookings' | 'handoffs', niche: string | null): string {
  return `/api/admin/${resource}${niche ? `?niche=${encodeURIComponent(niche)}` : ''}`
}

async function getAdminJson<T>(path: string, adminKey: string, signal?: AbortSignal): Promise<T> {
  const response = await request(path, { headers: { Accept: 'application/json', 'X-Admin-Key': adminKey }, signal })
  return (await response.json()) as T
}

/** Newest first, at most 200. 401 = wrong key, 503 = admin disabled on the server. */
export function getAdminBookings(adminKey: string, niche: string | null, signal?: AbortSignal): Promise<AdminBooking[]> {
  return getAdminJson<AdminBooking[]>(adminPath('bookings', niche), adminKey, signal)
}

export function getAdminHandoffs(adminKey: string, niche: string | null, signal?: AbortSignal): Promise<AdminHandoff[]> {
  return getAdminJson<AdminHandoff[]>(adminPath('handoffs', niche), adminKey, signal)
}

/**
 * Sends a message and yields the run's SSE events as they arrive.
 * Problems before the stream opens (400/404/409/429/network) throw an ApiError;
 * once the stream is open, failures arrive as an `error` event.
 */
export async function* streamMessage(
  sessionId: string,
  text: string,
  signal?: AbortSignal,
): AsyncGenerator<StreamEvent> {
  const response = await request(`/api/sessions/${encodeURIComponent(sessionId)}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify({ text }),
    signal,
  })
  if (!response.body) {
    throw new ApiError('http', response.status, 'The server returned an empty response.')
  }
  yield* readStreamEvents(response.body)
}
