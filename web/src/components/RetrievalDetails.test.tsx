import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { TraceEvent } from '../api/types'
import { describeStep } from '../lib/trace'
import { RetrievalDetails } from './RetrievalDetails'

afterEach(cleanup)

function view(output: unknown) {
  const event: TraceEvent = {
    runId: 'r1',
    sequence: 1,
    type: 'Retrieval',
    name: 'search_knowledge_base',
    input: { query: 'Can I bring my dog?' },
    output,
    tokensIn: 6,
    tokensOut: null,
    durationMs: 90,
    costUsd: 0,
  }
  return describeStep(event, 1).retrieval!
}

const hit = { sourceId: 'pets#0', title: 'Pet policy', heading: 'Dogs', text: 'Small dogs are welcome.', semanticScore: 0.81, lexicalScore: null, finalScore: 0.0164, aboveThreshold: true }

describe('RetrievalDetails', () => {
  it('shows the query and the result, with passages collapsed until opened', () => {
    render(<RetrievalDetails retrieval={view({ thresholdPassed: true, threshold: 0.6, passages: [hit] })} />)
    expect(screen.getByText('Can I bring my dog?')).toBeTruthy()
    expect(screen.getByTestId('retrieval-result').textContent).toContain('1 passage, 1 above threshold')
    const toggle = screen.getByRole('button', { name: /Pet policy/ })
    expect(toggle.textContent).toContain('Dogs')
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(screen.queryByText('pets#0')).toBeNull()

    fireEvent.click(toggle)
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(screen.getByText('pets#0')).toBeTruthy()
    expect(screen.getByText('Small dogs are welcome.')).toBeTruthy()
    expect(screen.getByText('0.810')).toBeTruthy()
    expect(screen.getByText('0.016')).toBeTruthy()
    // The passage did not come from the keyword search.
    expect(screen.getByText('n/a')).toBeTruthy()

    fireEvent.click(toggle)
    expect(screen.queryByText('pets#0')).toBeNull()
  })

  it('shows a clear empty state below the threshold', () => {
    render(<RetrievalDetails retrieval={view({ thresholdPassed: false, threshold: 0.6, passages: [] })} />)
    expect(screen.getByTestId('retrieval-result').textContent).toContain('No relevant information found (below threshold)')
    expect(screen.queryByTestId('retrieval-passage')).toBeNull()
  })

  it('marks passages under the threshold', () => {
    render(<RetrievalDetails retrieval={view({ thresholdPassed: true, threshold: 0.6, passages: [hit, { ...hit, sourceId: 'pets#1', aboveThreshold: false }] })} />)
    const rows = screen.getAllByTestId('retrieval-passage')
    expect(rows[0].getAttribute('data-above')).toBe('true')
    expect(rows[1].getAttribute('data-above')).toBe('false')
    expect(rows[1].textContent).toContain('below threshold')
  })
})
