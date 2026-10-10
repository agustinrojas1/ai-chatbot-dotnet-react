import { useId, useState } from 'react'
import { formatScore, type RetrievalPassageView, type RetrievalView } from '../lib/trace'

function ScoreRow({ label, title, score, bar, tone }: { label: string; title: string; score: number | null; bar: number | null; tone: string }) {
  return (
    <div className="flex items-center gap-2" title={title}>
      <span className="w-[4.5rem] shrink-0 text-[11px] text-slate-700">{label}</span>
      <span aria-hidden="true" className="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-slate-200">
        {bar !== null && <span className={`block h-full rounded-full ${tone}`} style={{ width: `${Math.round(bar * 100)}%` }} />}
      </span>
      <span className={`w-12 shrink-0 text-right text-[11px] tabular-nums ${score === null ? 'text-slate-500' : 'font-semibold text-slate-900'}`}>
        {score === null ? 'n/a' : formatScore(score)}
      </span>
    </div>
  )
}

function Passage({ passage }: { passage: RetrievalPassageView }) {
  const [open, setOpen] = useState(false)
  const bodyId = useId()
  return (
    <li
      data-testid="retrieval-passage"
      data-above={passage.aboveThreshold}
      className={`rounded-md border ${passage.aboveThreshold ? 'border-teal-300 bg-white' : 'border-slate-200 bg-slate-50'}`}
    >
      <button
        type="button"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setOpen((v) => !v)}
        className="flex w-full min-w-0 items-start gap-1.5 rounded-md px-2 py-1.5 text-left hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sky-600"
      >
        <svg
          width="10"
          height="10"
          viewBox="0 0 10 10"
          aria-hidden="true"
          className={`mt-1 shrink-0 text-slate-700 transition-transform ${open ? 'rotate-90' : ''}`}
        >
          <path d="M3 1.5 7 5 3 8.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span className="min-w-0 flex-1 break-words text-[12px] font-medium text-slate-900">
          {passage.title}
          {passage.heading && <span className="font-normal text-slate-700"> › {passage.heading}</span>}
        </span>
        <span
          className={`shrink-0 rounded px-1.5 py-px text-[10px] font-semibold ${
            passage.aboveThreshold ? 'bg-teal-100 text-teal-900' : 'bg-slate-200 text-slate-800'
          }`}
        >
          {passage.aboveThreshold ? '✓ above threshold' : '✗ below threshold'}
        </span>
      </button>
      {open && (
        <div id={bodyId} className="space-y-2 border-t border-slate-200 px-2 py-2">
          <span className="inline-block max-w-full break-all rounded bg-slate-100 px-1.5 py-px font-mono text-[11px] text-slate-800">
            {passage.sourceId}
          </span>
          <div className="space-y-1">
            <ScoreRow label="Semantic" title="Vector similarity (cosine)" score={passage.semanticScore} bar={passage.semanticBar} tone="bg-sky-500" />
            <ScoreRow label="Keyword" title="Full-text search rank" score={passage.lexicalScore} bar={passage.lexicalBar} tone="bg-violet-500" />
            <ScoreRow label="Final (RRF)" title="Reciprocal Rank Fusion of both rankings" score={passage.finalScore} bar={passage.finalBar} tone="bg-teal-600" />
          </div>
          {passage.text ? (
            <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words border-l-2 border-slate-300 pl-2 text-[12px] leading-snug text-slate-800">
              {passage.text}
            </p>
          ) : (
            <p className="text-[12px] italic text-slate-600">No text in this passage.</p>
          )}
        </div>
      )}
    </li>
  )
}

/** The body of a "Knowledge search" card: the query, how the search went and the passages it found. */
export function RetrievalDetails({ retrieval }: { retrieval: RetrievalView }) {
  return (
    <div data-testid="retrieval" className="mt-1.5 space-y-1.5">
      {retrieval.query && (
        <p className="break-words text-[12px] text-slate-800">
          <span className="text-slate-700">Query: </span>
          <q className="font-medium">{retrieval.query}</q>
        </p>
      )}
      <p
        data-testid="retrieval-result"
        className={`rounded px-2 py-1 text-[12px] font-medium ${
          retrieval.thresholdPassed ? 'bg-teal-50 text-teal-900' : 'bg-amber-50 text-amber-900'
        }`}
      >
        {retrieval.result}
        {retrieval.threshold !== null && <span className="font-normal"> · threshold {formatScore(retrieval.threshold)}</span>}
      </p>
      {retrieval.passages.length > 0 && (
        <ul className="space-y-1">
          {retrieval.passages.map((passage) => (
            <Passage key={passage.key} passage={passage} />
          ))}
        </ul>
      )}
    </div>
  )
}
