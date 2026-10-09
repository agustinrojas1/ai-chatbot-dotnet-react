import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { JsonViewer } from './JsonViewer'

afterEach(cleanup)

const value = { query: 'cleaning', filters: { maxPrice: 80, tags: ['a', 'b'] }, empty: [], done: true, none: null }

describe('JsonViewer', () => {
  it('is collapsed until the label is clicked, and says what is inside', () => {
    render(<JsonViewer label="Arguments" value={value} />)
    const toggle = screen.getByRole('button', { name: /Arguments/ })
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(toggle.textContent).toContain('5 keys')
    expect(screen.queryByText('"cleaning"')).toBeNull()

    fireEvent.click(toggle)
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(screen.getByText('"cleaning"')).toBeTruthy()
    fireEvent.click(toggle)
    expect(screen.queryByText('"cleaning"')).toBeNull()
  })

  it('shows the first level open and nested objects collapsed with a preview, which can be opened', () => {
    render(<JsonViewer label="Result" value={value} defaultOpen />)
    expect(screen.queryByText('80')).toBeNull()
    const nested = screen.getByRole('button', { name: /"filters"/ })
    expect(nested.getAttribute('aria-expanded')).toBe('false')
    expect(nested.textContent).toContain('2 keys')

    fireEvent.click(nested)
    expect(nested.getAttribute('aria-expanded')).toBe('true')
    expect(screen.getByText('80')).toBeTruthy()
    // The array inside is one level deeper and starts collapsed again.
    const tags = screen.getByRole('button', { name: /"tags"/ })
    expect(tags.textContent).toContain('2 items')
    expect(screen.queryByText('"a"')).toBeNull()
    fireEvent.click(tags)
    expect(screen.getByText('"a"')).toBeTruthy()
  })

  it('renders primitives, empty containers and keeps long strings whole', () => {
    render(<JsonViewer label="Output" value={{ ...value, long: 'x'.repeat(300) }} defaultOpen />)
    expect(screen.getByText('true').className).toContain('purple')
    expect(screen.getByText('null')).toBeTruthy()
    expect(screen.getByText('[]')).toBeTruthy()
    expect(screen.getByText(`"${'x'.repeat(300)}"`)).toBeTruthy()
  })

  it('shows a scalar payload directly', () => {
    render(<JsonViewer label="Result" value="ok" defaultOpen />)
    expect(screen.getByText('"ok"')).toBeTruthy()
  })
})
