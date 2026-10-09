import { useCallback, useEffect, useReducer, useRef, type Dispatch } from 'react'
import { createSession, getMessages, getTrace, fetchNiches, streamMessage } from '../api/client'
import { ApiError } from '../api/errors'
import { newLocalId } from '../lib/ids'
import type { ViewedSession } from '../lib/route'
import {
  canSend,
  chatReducer,
  initialChatState,
  MAX_INPUT_CHARS,
  type ChatAction,
} from '../state/chatReducer'
import { loadStoredSession, saveStoredSession } from '../state/storage'

const DEFAULT_NICHE = 'realestate'

const GENERIC_ERROR = 'Something went wrong while reaching the assistant. Check your connection and try again.'
const INTERRUPTED = 'The connection was interrupted before the answer finished. Please try again.'

type Send = Dispatch<ChatAction>

/** Restores the stored session of `nicheId` if the server still has it, otherwise creates a new one. */
async function establishSession(
  dispatch: Send,
  signal: AbortSignal,
  nicheId: string,
  tryRestore: boolean,
): Promise<void> {
  const stored = tryRestore ? loadStoredSession() : null
  if (stored?.niche === nicheId) {
    try {
      const messages = await getMessages(stored.sessionId, signal)
      const trace = await getTrace(stored.sessionId, signal).catch(() => null)
      signal.throwIfAborted()
      dispatch({
        type: 'session_restored',
        sessionId: stored.sessionId,
        nicheId,
        stored: messages,
        trace,
      })
      return
    } catch (error) {
      if (!(error instanceof ApiError && error.kind === 'not_found')) throw error
      // The server no longer knows this session: start a fresh one below.
    }
  }

  const created = await createSession(nicheId, signal)
  signal.throwIfAborted()
  saveStoredSession({ sessionId: created.sessionId, niche: nicheId })
  dispatch({ type: 'session_started', sessionId: created.sessionId, nicheId })
}

/** Opens a past session read-only (admin link): its messages and trace, no new session, nothing stored. */
async function viewSession(dispatch: Send, signal: AbortSignal, view: ViewedSession): Promise<void> {
  const niches = await fetchNiches(signal).catch(() => [])
  signal.throwIfAborted()
  dispatch({ type: 'niches_loaded', niches })
  const nicheId = niches.find((n) => n.id === view.niche)?.id ?? null
  dispatch({ type: 'reset', nicheId })
  const messages = await getMessages(view.sessionId, signal)
  const trace = await getTrace(view.sessionId, signal).catch(() => null)
  signal.throwIfAborted()
  dispatch({
    type: 'session_restored',
    sessionId: view.sessionId,
    nicheId,
    stored: messages,
    trace,
    readOnly: true,
  })
}

async function loadInitial(dispatch: Send, signal: AbortSignal, view: ViewedSession | null): Promise<void> {
  try {
    if (view) return await viewSession(dispatch, signal, view)
    const niches = await fetchNiches(signal)
    signal.throwIfAborted()
    if (niches.length === 0) throw new Error('The server has no niches configured.')
    dispatch({ type: 'niches_loaded', niches })
    const stored = loadStoredSession()
    const wanted =
      niches.find((n) => n.id === stored?.niche) ?? niches.find((n) => n.id === DEFAULT_NICHE) ?? niches[0]
    dispatch({ type: 'reset', nicheId: wanted.id })
    await establishSession(dispatch, signal, wanted.id, true)
  } catch (error) {
    if (signal.aborted) return
    const message =
      error instanceof ApiError && error.kind === 'not_found' && view
        ? 'That session does not exist (or has expired).'
        : error instanceof Error
          ? error.message
          : GENERIC_ERROR
    dispatch({ type: 'session_failed', message })
  }
}

function dispatchApiError(dispatch: Send, error: unknown): void {
  if (error instanceof ApiError) {
    switch (error.kind) {
      case 'rate_limit':
        dispatch({
          type: 'rate_limited',
          code: error.code,
          message: error.message,
          retryAfterSeconds: error.retryAfterSeconds,
        })
        return
      case 'not_found':
        dispatch({
          type: 'request_failed',
          message: 'This demo session expired. Restart the demo to chat again.',
          retryable: false,
          sessionEnded: true,
        })
        return
      case 'conflict':
        dispatch({
          type: 'request_failed',
          message: 'This conversation is closed. Restart the demo to start a new one.',
          retryable: false,
          sessionEnded: true,
        })
        return
      case 'bad_request':
        dispatch({ type: 'request_failed', message: error.message, retryable: false, sessionEnded: false })
        return
      case 'network':
        dispatch({ type: 'request_failed', message: error.message, retryable: true, sessionEnded: false })
        return
    }
  }
  dispatch({ type: 'request_failed', message: GENERIC_ERROR, retryable: true, sessionEnded: false })
}

export function useChat(view: ViewedSession | null = null) {
  const [state, dispatch] = useReducer(chatReducer, initialChatState)

  const stateRef = useRef(state)
  useEffect(() => {
    stateRef.current = state
  }, [state])

  // Aborts whatever session setup or stream is in flight when the session is replaced or the page unmounts.
  const setupAbort = useRef<AbortController | null>(null)
  const streamAbort = useRef<AbortController | null>(null)
  const sending = useRef(false)

  const abortAll = useCallback(() => {
    setupAbort.current?.abort()
    streamAbort.current?.abort()
    streamAbort.current = null
    sending.current = false
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setupAbort.current = controller
    void loadInitial(dispatch, controller.signal, view)
    return () => {
      controller.abort()
      streamAbort.current?.abort()
    }
    // The viewed session comes from the URL and does not change while the page lives.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /** Starts a brand-new session for a niche; used by the niche selector and "Restart demo". */
  const startSession = useCallback(
    async (nicheId: string) => {
      abortAll()
      const controller = new AbortController()
      setupAbort.current = controller
      dispatch({ type: 'reset', nicheId })
      try {
        await establishSession(dispatch, controller.signal, nicheId, false)
      } catch (error) {
        if (controller.signal.aborted) return
        dispatch({ type: 'session_failed', message: error instanceof Error ? error.message : GENERIC_ERROR })
      }
    },
    [abortAll],
  )

  const switchNiche = useCallback(
    (nicheId: string) => {
      if (nicheId === stateRef.current.nicheId && stateRef.current.phase === 'ready') return
      void startSession(nicheId)
    },
    [startSession],
  )

  const restart = useCallback(() => {
    const nicheId = stateRef.current.nicheId
    if (nicheId) void startSession(nicheId)
  }, [startSession])

  /** Retries loading the niches/session after the first load failed. */
  const reload = useCallback(() => {
    abortAll()
    const controller = new AbortController()
    setupAbort.current = controller
    dispatch({ type: 'reset', nicheId: stateRef.current.nicheId })
    void loadInitial(dispatch, controller.signal, view)
  }, [abortAll, view])

  const send = useCallback(async (rawText: string) => {
    const text = rawText.trim()
    const current = stateRef.current
    if (!text || text.length > MAX_INPUT_CHARS || sending.current || !canSend(current) || !current.sessionId) {
      return
    }

    const controller = new AbortController()
    streamAbort.current = controller
    sending.current = true
    dispatch({ type: 'user_message', id: newLocalId('user'), text, at: new Date().toISOString() })

    let finished = false
    try {
      for await (const event of streamMessage(current.sessionId, text, controller.signal)) {
        if (controller.signal.aborted) return
        const at = new Date().toISOString()
        switch (event.event) {
          case 'run_started':
            dispatch({ type: 'run_started', runId: event.data.runId, at })
            break
          case 'token':
            dispatch({ type: 'token', text: event.data.text, at })
            break
          case 'trace':
            dispatch({ type: 'trace', event: event.data, at })
            break
          case 'handoff':
            dispatch({ type: 'handoff', id: newLocalId('handoff'), at })
            break
          case 'run_completed':
            finished = true
            dispatch({ type: 'run_completed', data: event.data })
            break
          case 'error':
            finished = true
            dispatch({ type: 'run_failed', message: event.data.message, retryable: event.data.retryable })
            break
        }
      }
      if (!finished && !controller.signal.aborted) {
        dispatch({ type: 'run_failed', message: INTERRUPTED, retryable: true })
      }
    } catch (error) {
      if (controller.signal.aborted) return
      dispatchApiError(dispatch, error)
    } finally {
      if (streamAbort.current === controller) {
        streamAbort.current = null
        sending.current = false
      }
    }
  }, [])

  /** Sends the last message again after a failure, replacing the failed turn. */
  const retry = useCallback(() => {
    const text = stateRef.current.lastUserText
    if (!text) return
    dispatch({ type: 'retry_started' })
    void send(text)
  }, [send])

  /** Pulls the runs the server has that this page lacks (a reload whose `/trace` request failed). */
  const hydrateTrace = useCallback(async () => {
    const sessionId = stateRef.current.sessionId
    if (!sessionId) return
    try {
      const trace = await getTrace(sessionId)
      if (stateRef.current.sessionId === sessionId) dispatch({ type: 'trace_hydrated', trace })
    } catch {
      // The panel keeps showing what it has; the next open tries again.
    }
  }, [])

  return { state, send, retry, switchNiche, restart, reload, hydrateTrace }
}
