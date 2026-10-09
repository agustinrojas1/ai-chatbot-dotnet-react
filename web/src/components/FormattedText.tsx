import type { ReactNode } from 'react'
import { parseWhatsAppText, type FormatNode } from '../lib/whatsappFormat'

function render(nodes: FormatNode[]): ReactNode[] {
  return nodes.map((node, i) => {
    if (typeof node === 'string') return node
    const children = render(node.children)
    switch (node.kind) {
      case 'bold':
        return (
          <strong key={i} className="font-semibold">
            {children}
          </strong>
        )
      case 'italic':
        return (
          <em key={i} className="italic">
            {children}
          </em>
        )
      case 'strike':
        return (
          <del key={i} className="line-through">
            {children}
          </del>
        )
      case 'mono':
        return (
          <code key={i} className="rounded bg-black/5 px-1 py-px font-mono text-[13px]">
            {children}
          </code>
        )
    }
  })
}

/** Renders WhatsApp-formatted text with React elements only; line breaks come from `whitespace-pre-wrap`. */
export function FormattedText({ text }: { text: string }) {
  return <>{render(parseWhatsAppText(text))}</>
}
