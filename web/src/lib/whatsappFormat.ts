/**
 * Parser for WhatsApp text formatting: *bold*, _italic_, ~strike~ and `mono`.
 * It produces a small tree instead of HTML so the UI renders it with React elements only.
 *
 * Rules, close to WhatsApp's own:
 * - a marker opens only if it is not preceded by a letter/digit and is followed by a non-space;
 * - it closes at the first marker that is preceded by a non-space and not followed by a letter/digit
 *   (so snake_case_names and `2 * 3 * 4` stay plain text);
 * - a span never crosses a line break, and an unmatched marker stays literal;
 * - `**bold**` and `__italic__` (models slip into Markdown) are accepted too;
 * - bold, italic and strike nest; mono content is literal.
 */

export type FormatKind = 'bold' | 'italic' | 'strike' | 'mono'

export type FormatNode = string | { kind: FormatKind; children: FormatNode[] }

const MARKERS: Record<string, FormatKind> = { '*': 'bold', _: 'italic', '~': 'strike', '`': 'mono' }

const isSpace = (ch: string | undefined) => ch !== undefined && /\s/.test(ch)
const isWordChar = (ch: string | undefined) => ch !== undefined && /[\p{L}\p{N}]/u.test(ch)

/** Index of the closing run of `marker` (length `len`) for a span whose content starts at `from`. */
function findClose(text: string, marker: string, len: number, from: number): number {
  for (let j = from; j < text.length; j++) {
    const ch = text[j]
    if (ch === '\n') return -1
    if (ch !== marker) continue
    if (j === from) continue // empty content
    if (len === 2 && text[j + 1] !== marker) continue
    const after = text[j + len]
    if (isSpace(text[j - 1]) || isWordChar(after)) continue
    return j
  }
  return -1
}

function parseInline(text: string): FormatNode[] {
  const nodes: FormatNode[] = []
  let plain = ''
  const flush = () => {
    if (plain) nodes.push(plain)
    plain = ''
  }

  let i = 0
  while (i < text.length) {
    const ch = text[i]
    const kind = MARKERS[ch]
    if (kind && !isWordChar(text[i - 1])) {
      // `**bold**` / `__italic__` first, then the single-character form.
      const lens = (ch === '*' || ch === '_') && text[i + 1] === ch ? [2, 1] : [1]
      let matched = false
      for (const len of lens) {
        const contentStart = i + len
        const first = text[contentStart]
        if (first === undefined || isSpace(first) || first === '\n') continue
        const close = findClose(text, ch, len, contentStart)
        if (close === -1) continue
        flush()
        const inner = text.slice(contentStart, close)
        nodes.push({ kind, children: kind === 'mono' ? [inner] : parseInline(inner) })
        i = close + len
        matched = true
        break
      }
      if (matched) continue
    }
    plain += ch
    i++
  }
  flush()
  return nodes
}

export function parseWhatsAppText(text: string): FormatNode[] {
  return parseInline(text)
}
