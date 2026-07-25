import { formatPercentValue } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Thin compliance progress bar with a trailing percent. Color auto-derives
 * from the value: >= 95 teal, >= 75 amber, otherwise red. `value` is 0..100 and
 * must be unrounded — the trailing percent applies the never-round-up-to-100
 * guard itself, which only works on full precision. */
export function ComplianceBar({
  value = 0,
  width = 170,
  showValue = true,
  className,
}: {
  value?: number
  width?: number
  showValue?: boolean
  className?: string
}) {
  const color =
    value >= 95
      ? 'var(--status-ok-dot)'
      : value >= 75
        ? 'var(--status-warn-dot)'
        : 'var(--status-danger-dot)'
  const pct = Math.max(0, Math.min(100, value))
  return (
    <span className={cn('inline-flex items-center gap-3', className)}>
      <span
        className="inline-block h-1.5 overflow-hidden rounded-[3px] bg-gray-100"
        style={{ width }}
      >
        <span
          className="block h-full rounded-[3px] transition-[width] duration-200 ease-out"
          style={{ width: `${pct}%`, background: color }}
        />
      </span>
      {showValue ? (
        <span className="min-w-[44px] text-right text-sm tabular-nums text-secondary">
          {formatPercentValue(value)}
        </span>
      ) : null}
    </span>
  )
}
