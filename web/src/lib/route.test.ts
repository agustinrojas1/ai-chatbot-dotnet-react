import { describe, expect, it } from 'vitest'
import { isAdminPath, viewedSessionFromSearch } from './route'

describe('route helpers', () => {
  it('recognises /admin with or without a trailing slash', () => {
    expect(isAdminPath('/admin')).toBe(true)
    expect(isAdminPath('/admin/')).toBe(true)
    expect(isAdminPath('/')).toBe(false)
    expect(isAdminPath('/administrator')).toBe(false)
  })

  it('reads the session to view from the query string', () => {
    expect(viewedSessionFromSearch('?session=abc&niche=dental')).toEqual({ sessionId: 'abc', niche: 'dental' })
    expect(viewedSessionFromSearch('?session=abc')).toEqual({ sessionId: 'abc', niche: null })
    expect(viewedSessionFromSearch('?session=')).toBeNull()
    expect(viewedSessionFromSearch('')).toBeNull()
  })
})
