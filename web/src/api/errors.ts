import type { RateLimitCode } from './types'

export type ApiErrorKind = 'network' | 'bad_request' | 'not_found' | 'conflict' | 'rate_limit' | 'http'

/** An HTTP-level failure (before streaming) or a network failure, in a form the UI can switch on. */
export class ApiError extends Error {
  readonly kind: ApiErrorKind
  readonly status: number
  /** Set on 429 when the server sent a known `code`. */
  readonly code: RateLimitCode | null
  /** Seconds from the `Retry-After` header, if any. */
  readonly retryAfterSeconds: number | null

  constructor(
    kind: ApiErrorKind,
    status: number,
    message: string,
    code: RateLimitCode | null = null,
    retryAfterSeconds: number | null = null,
  ) {
    super(message)
    this.name = 'ApiError'
    this.kind = kind
    this.status = status
    this.code = code
    this.retryAfterSeconds = retryAfterSeconds
  }
}

const RATE_LIMIT_CODES: readonly string[] = ['ip_rate_limit', 'session_limit', 'daily_cap']

function kindOf(status: number): ApiErrorKind {
  switch (status) {
    case 400:
      return 'bad_request'
    case 404:
      return 'not_found'
    case 409:
      return 'conflict'
    case 429:
      return 'rate_limit'
    default:
      return 'http'
  }
}

function firstValidationMessage(errors: unknown): string | null {
  if (typeof errors !== 'object' || errors === null) return null
  for (const value of Object.values(errors)) {
    if (Array.isArray(value) && typeof value[0] === 'string') return value[0]
  }
  return null
}

function parseRetryAfter(header: string | null): number | null {
  if (header === null) return null
  const seconds = Number(header)
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : null
}

/**
 * Turns a non-2xx response into an ApiError. Understands problem details
 * (`title`/`detail`/`errors`) and the rate limiter's `{ message, code }`.
 */
export async function errorFromResponse(response: Response): Promise<ApiError> {
  let body: unknown = null
  try {
    body = await response.json()
  } catch {
    // Not JSON (proxy error page, empty body): fall back to the status text below.
  }

  const record = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {}
  const message =
    (typeof record.message === 'string' && record.message) ||
    firstValidationMessage(record.errors) ||
    (typeof record.detail === 'string' && record.detail) ||
    (typeof record.title === 'string' && record.title) ||
    `Request failed (${response.status})`

  const code =
    typeof record.code === 'string' && RATE_LIMIT_CODES.includes(record.code)
      ? (record.code as RateLimitCode)
      : null

  return new ApiError(
    kindOf(response.status),
    response.status,
    message,
    code,
    parseRetryAfter(response.headers.get('Retry-After')),
  )
}
