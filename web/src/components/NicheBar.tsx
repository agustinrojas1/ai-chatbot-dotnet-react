import type { Niche } from '../api/types'

interface NicheBarProps {
  niches: Niche[]
  nicheId: string | null
  onChange: (nicheId: string) => void
}

/** Top strip with the niche selector. Changing the niche starts a new session. */
export function NicheBar({ niches, nicheId, onChange }: NicheBarProps) {
  return (
    <div className="flex items-center justify-between gap-2 border-b border-black/5 bg-whatsapp-dark px-3 py-2 text-white">
      <label className="flex shrink-0 items-center gap-1.5 text-[11px] text-white/80">
        <span className="font-medium">Niche:</span>
        <select
          value={nicheId ?? ''}
          onChange={(e) => onChange(e.target.value)}
          disabled={niches.length === 0}
          className="cursor-pointer rounded-full border border-white/15 bg-white/10 py-1 pl-2 pr-6 text-[11px] font-medium text-white transition hover:bg-white/15 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {niches.map((n) => (
            <option key={n.id} value={n.id} className="bg-white text-slate-900">
              {n.avatar.emoji} {n.name}
            </option>
          ))}
        </select>
      </label>
      <span className="min-w-0 truncate text-[11px] text-white/80 max-[399px]:hidden">
        AI booking agent demo
      </span>
    </div>
  )
}
