import { cn } from '@/lib/utils'

/** Thin compliance progress bar with a trailing percent. Color auto-derives
 * from the value: >= 95 teal, >= 75 amber, otherwise red. `value` is 0..100. */
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
  const color = value >= 95 ? '#0e9481' : value >= 75 ? '#d97706' : '#dc3d5c'
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
        <span className="min-w-[34px] text-right text-sm tabular-nums text-secondary">
          {value}%
        </span>
      ) : null}
    </span>
  )
}
