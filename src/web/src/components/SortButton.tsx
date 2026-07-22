import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react'

import { cn } from '@/lib/utils'

export type SortDir = 'asc' | 'desc'

type SortButtonProps<K extends string> = {
  label: string
  column: K
  /** Currently active sort column; null means no client-side sort is applied. */
  sortKey: K | null
  sortDir: SortDir
  onSort: (key: K) => void
}

/** Sortable column header button shared by the analytics tables. */
export function SortButton<K extends string>({
  label,
  column,
  sortKey,
  sortDir,
  onSort,
}: SortButtonProps<K>) {
  const active = column === sortKey
  const Arrow = active ? (sortDir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown
  return (
    <button
      type="button"
      onClick={() => onSort(column)}
      className={cn(
        'group inline-flex items-center gap-1 rounded-sm font-medium transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        active && 'text-foreground',
      )}
    >
      {label}
      <Arrow
        aria-hidden
        className={cn(
          'h-3 w-3 shrink-0',
          !active && 'opacity-0 transition-opacity group-hover:opacity-60 group-focus-visible:opacity-60',
        )}
      />
    </button>
  )
}
