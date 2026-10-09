import type { Niche } from '../api/types'

interface AvatarProps {
  avatar: Niche['avatar'] | null
  size?: 'md' | 'xs'
}

export function Avatar({ avatar, size = 'md' }: AvatarProps) {
  const [from, to] = avatar?.colors ?? ['#128c7e', '#25d366']
  const sizeClass = size === 'md' ? 'h-10 w-10 text-base' : 'h-6 w-6 text-[10px]'
  return (
    <div
      className={`${sizeClass} flex shrink-0 select-none items-center justify-center rounded-full font-semibold text-white`}
      style={{ backgroundImage: `linear-gradient(to bottom right, ${from}, ${to})` }}
      aria-hidden="true"
    >
      {avatar?.letter ?? ''}
    </div>
  )
}
