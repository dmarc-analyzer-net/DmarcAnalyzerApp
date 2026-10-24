import { cn } from '@/lib/utils'

export type DmarcPolicy = 'none' | 'quarantine' | 'reject'

const TONES: Record<DmarcPolicy, string> = {
  reject: 'bg-(--status-ok-bg) text-(--status-ok-fg)',
  quarantine: 'bg-(--status-warn-bg) text-(--status-warn-fg)',
  none: 'bg-(--status-neutral-bg) text-(--status-neutral-fg)',
}

/** DMARC policy chip: mono `p=reject` on a tinted, square-ish chip. */
export function PolicyBadge({
  policy = 'none',
  className,
}: {
  policy?: DmarcPolicy
  className?: string
}) {
  return (
    <span
      className={cn(
        'inline-block rounded-xs px-2 py-[3px] font-mono text-xs font-medium whitespace-nowrap',
        TONES[policy],
        className,
      )}
    >
      p={policy}
    </span>
  )
}
