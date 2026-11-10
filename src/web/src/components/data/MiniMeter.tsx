import { formatPercent } from '@/lib/format'
import { cn } from '@/lib/utils'

type MiniMeterProps = {
  /** Short label shown before the bar, e.g. "DKIM". */
  label: string
  /** 0..1 fraction, or null when there is no data to rate. */
  value: number | null
  /** Bar width in px. */
  width?: number
  /** Longer explanation attached as a native tooltip. */
  title?: string
  className?: string
}

/**
 * A labelled micro meter — "DKIM ▁▃▅ 98.2%" — for a single rate beside its name.
 *
 * Deliberately **one neutral brand tone, not a traffic light**. The obvious
 * temptation is to redden a low bar, but a DMARC-compliant domain only needs one
 * of DKIM and SPF to align: a forwarding-heavy sender legitimately sits at 40%
 * SPF with 99% compliance, and colouring that red asserts a failure that is not
 * one. The verdict belongs to the compliance bar next to it; these two bars
 * answer the follow-up question, which is *which mechanism carries alignment*.
 *
 * `value === null` renders an empty track and "—", so "no mail to judge" never
 * reads as "0% passed".
 */
export function MiniMeter({ label, value, width = 44, title, className }: MiniMeterProps) {
  const pct = value == null ? 0 : Math.max(0, Math.min(100, value * 100))
  return (
    <span className={cn('inline-flex items-center gap-1.5', className)} title={title}>
      <span className="text-xs text-secondary">{label}</span>
      <span
        className="inline-block h-1 overflow-hidden rounded-[2px] bg-surface-sunken"
        style={{ width }}
        aria-hidden
      >
        <span
          className="block h-full rounded-[2px]"
          style={{ width: `${pct}%`, background: 'var(--brand)' }}
        />
      </span>
      <span className="text-xs tabular-nums text-secondary">
        {value == null ? '—' : formatPercent(value)}
      </span>
    </span>
  )
}
