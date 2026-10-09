import { cleanup, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { FormattedText } from './FormattedText'

afterEach(cleanup)

describe('FormattedText', () => {
  it('renders formats as elements', () => {
    const { container } = render(<FormattedText text="*bold* _it_ ~gone~ `code`" />)
    expect(container.querySelector('strong')?.textContent).toBe('bold')
    expect(container.querySelector('em')?.textContent).toBe('it')
    expect(container.querySelector('del')?.textContent).toBe('gone')
    expect(container.querySelector('code')?.textContent).toBe('code')
  })

  it('escapes HTML instead of interpreting it', () => {
    const { container } = render(<FormattedText text={'<script>alert(1)</script> *<img src=x onerror=alert(1)>*'} />)
    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(container.innerHTML).toContain('&lt;script&gt;')
    expect(container.querySelector('strong')?.textContent).toBe('<img src=x onerror=alert(1)>')
  })

  it('keeps line breaks as text for whitespace-pre-wrap', () => {
    const { container } = render(<FormattedText text={'one\ntwo'} />)
    expect(container.textContent).toBe('one\ntwo')
  })
})
