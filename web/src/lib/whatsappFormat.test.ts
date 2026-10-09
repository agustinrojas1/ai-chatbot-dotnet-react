import { describe, expect, it } from 'vitest'
import { parseWhatsAppText } from './whatsappFormat'

describe('parseWhatsAppText', () => {
  it('returns plain text untouched', () => {
    expect(parseWhatsAppText('hello world')).toEqual(['hello world'])
  })

  it('parses bold, italic, strike and mono', () => {
    expect(parseWhatsAppText('a *b* _c_ ~d~ `e`')).toEqual([
      'a ',
      { kind: 'bold', children: ['b'] },
      ' ',
      { kind: 'italic', children: ['c'] },
      ' ',
      { kind: 'strike', children: ['d'] },
      ' ',
      { kind: 'mono', children: ['e'] },
    ])
  })

  it('nests formats', () => {
    expect(parseWhatsAppText('*bold _and italic_ text*')).toEqual([
      { kind: 'bold', children: ['bold ', { kind: 'italic', children: ['and italic'] }, ' text'] },
    ])
  })

  it('keeps mono content literal', () => {
    expect(parseWhatsAppText('`*not bold*`')).toEqual([{ kind: 'mono', children: ['*not bold*'] }])
  })

  it('accepts double markers from Markdown habits', () => {
    expect(parseWhatsAppText('**bold** and __italic__')).toEqual([
      { kind: 'bold', children: ['bold'] },
      ' and ',
      { kind: 'italic', children: ['italic'] },
    ])
  })

  it('leaves unmatched markers as literal text', () => {
    expect(parseWhatsAppText('*not closed')).toEqual(['*not closed'])
    expect(parseWhatsAppText('closed* only')).toEqual(['closed* only'])
    expect(parseWhatsAppText('a * b * c')).toEqual(['a * b * c'])
  })

  it('does not format inside words (snake_case, 2*3*4)', () => {
    expect(parseWhatsAppText('use snake_case_names here')).toEqual(['use snake_case_names here'])
    expect(parseWhatsAppText('2*3*4')).toEqual(['2*3*4'])
  })

  it('never lets a span cross a line break', () => {
    expect(parseWhatsAppText('*first\nsecond*')).toEqual(['*first\nsecond*'])
    expect(parseWhatsAppText('*one*\n_two_')).toEqual([
      { kind: 'bold', children: ['one'] },
      '\n',
      { kind: 'italic', children: ['two'] },
    ])
  })

  it('does not format empty spans or spans starting with a space', () => {
    expect(parseWhatsAppText('**')).toEqual(['**'])
    expect(parseWhatsAppText('* spaced *')).toEqual(['* spaced *'])
  })

  it('formats next to punctuation', () => {
    expect(parseWhatsAppText('(*USD 60*), ok.')).toEqual([
      '(',
      { kind: 'bold', children: ['USD 60'] },
      '), ok.',
    ])
  })

  it('does not interpret HTML: tags stay literal text', () => {
    expect(parseWhatsAppText('<img src=x onerror=alert(1)> *<b>hi</b>*')).toEqual([
      '<img src=x onerror=alert(1)> ',
      { kind: 'bold', children: ['<b>hi</b>'] },
    ])
  })

  it('shows a half-streamed bold as text until the closing marker arrives', () => {
    expect(parseWhatsAppText('A cleaning costs *USD')).toEqual(['A cleaning costs *USD'])
    expect(parseWhatsAppText('A cleaning costs *USD 60*')).toEqual([
      'A cleaning costs ',
      { kind: 'bold', children: ['USD 60'] },
    ])
  })
})
