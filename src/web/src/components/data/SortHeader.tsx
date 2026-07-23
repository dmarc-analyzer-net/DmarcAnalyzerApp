import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react'

import { cn } from '@/lib/utils'

export type SortDir = 'asc' | 'desc'

type SortHeaderProps<K extends string> = {
  label: string
  column: K
  /** Currently active sort column; null means no client-side sort is applied. */
  sortKey: K | null
  sortDir: SortDir
  onSort: (key: K) => void
}

/**
 * Sortable column-header button for the analytics tables, styled with the
 * console design tokens. Lives inside a `TableHead`; alignment is inherited
 * from the cell's text alignment.
 */
export function SortHeader<K extends string>({
  label,
  column,
  sortKey,
  sortDir,
  onSort,
}: SortHeaderProps<K>) {
  const active = column === sortKey
  const Arrow = active ? (sortDir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown
  return (
    <button
      type="button"
      onClick={() => onSort(column)}
      className={cn(
        'group inline-flex items-center gap-1 rounded-xs font-semibold text-secondary transition-colors duration-[120ms] ease-out hover:text-body focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none',
        active && 'text-body',
      )}
    >
      {label}
      <Arrow
        aria-hidden
        className={cn(
          'h-3 w-3 shrink-0',
          !active &&
            'opacity-0 transition-opacity group-hover:opacity-60 group-focus-visible:opacity-60',
        )}
      />
    </button>
  )
}
