import { useId, useState } from 'react'

/** "3 keys" / "2 items" / "empty": what a collapsed object or array says about itself. */
function describeContainer(value: object): string {
  const count = Array.isArray(value) ? value.length : Object.keys(value).length
  if (count === 0) return 'empty'
  const unit = Array.isArray(value) ? 'item' : 'key'
  return `${count} ${unit}${count === 1 ? '' : 's'}`
}

function Chevron({ open }: { open: boolean }) {
  return (
    <svg
      width="10"
      height="10"
      viewBox="0 0 10 10"
      aria-hidden="true"
      className={`shrink-0 transition-transform ${open ? 'rotate-90' : ''}`}
    >
      <path d="M3 1.5 7 5 3 8.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function Primitive({ value }: { value: unknown }) {
  if (typeof value === 'string') {
    return <span className="whitespace-pre-wrap break-words text-emerald-800">{JSON.stringify(value)}</span>
  }
  if (typeof value === 'number') return <span className="text-blue-800">{String(value)}</span>
  if (typeof value === 'boolean') return <span className="text-purple-800">{String(value)}</span>
  return <span className="text-slate-600">{value === null || value === undefined ? 'null' : String(value)}</span>
}

interface NodeProps {
  /** Key (object) or index (array) this value sits under; null at the root. */
  name: string | number | null
  value: unknown
  depth: number
}

const rowClass = 'flex min-w-0 items-start gap-1 py-px'

function Name({ name }: { name: string | number | null }) {
  if (name === null) return null
  return <span className="shrink-0 text-slate-700">{typeof name === 'number' ? `${name}:` : `${JSON.stringify(name)}:`}</span>
}

/** Objects and arrays below the first level start collapsed; click a row to open or close it. */
function JsonNode({ name, value, depth }: NodeProps) {
  const [open, setOpen] = useState(depth < 1)
  const childrenId = useId()

  if (typeof value !== 'object' || value === null) {
    return (
      <div className={rowClass}>
        <Name name={name} />
        <Primitive value={value} />
      </div>
    )
  }

  const entries: [string | number, unknown][] = Array.isArray(value)
    ? value.map((item, index) => [index, item])
    : Object.entries(value)
  const [openBracket, closeBracket] = Array.isArray(value) ? ['[', ']'] : ['{', '}']

  if (entries.length === 0) {
    return (
      <div className={rowClass}>
        <Name name={name} />
        <span className="text-slate-600">{openBracket + closeBracket}</span>
      </div>
    )
  }

  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-controls={childrenId}
        className="flex min-w-0 items-center gap-1 rounded py-px text-left hover:bg-slate-200/70"
      >
        <Chevron open={open} />
        <Name name={name} />
        <span className="text-slate-600">
          {openBracket}
          {!open && (
            <>
              {' '}
              <span className="italic">{describeContainer(value)}</span> {closeBracket}
            </>
          )}
        </span>
      </button>
      {open && (
        <div id={childrenId} className="ml-[5px] border-l border-slate-300 pl-3">
          {entries.map(([key, child]) => (
            <JsonNode key={key} name={key} value={child} depth={depth + 1} />
          ))}
        </div>
      )}
      {open && <span className="ml-[15px] text-slate-600">{closeBracket}</span>}
    </div>
  )
}

interface JsonViewerProps {
  label: string
  value: unknown
  /** Start with the payload visible. Collapsed by default: a timeline of many steps stays scannable. */
  defaultOpen?: boolean
}

/** A labelled, collapsible JSON payload with light syntax colouring. */
export function JsonViewer({ label, value, defaultOpen = false }: JsonViewerProps) {
  const [open, setOpen] = useState(defaultOpen)
  const bodyId = useId()
  const hint = typeof value === 'object' && value !== null ? describeContainer(value) : null

  return (
    <div className="min-w-0">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-controls={bodyId}
        className="flex items-center gap-1.5 rounded px-1 py-0.5 text-[11px] font-semibold text-slate-700 hover:bg-slate-100"
      >
        <Chevron open={open} />
        {label}
        {hint && <span className="font-normal text-slate-600">({hint})</span>}
      </button>
      {open && (
        <div
          id={bodyId}
          className="mt-1 max-h-64 overflow-auto rounded-md border border-slate-200 bg-slate-50 p-2 font-mono text-[11px] leading-[1.45] text-slate-800"
        >
          <JsonNode name={null} value={value} depth={0} />
        </div>
      )}
    </div>
  )
}
