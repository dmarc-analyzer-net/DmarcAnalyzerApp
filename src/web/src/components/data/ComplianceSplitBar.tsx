import { SegmentBar } from '@/components/data/SegmentBar'
import type { BarSegment } from '@/lib/bars'
import { formatCompact, formatPercentValue } from '@/lib/format'
import { cn } from '@/lib/utils'

type ComplianceSplitBarProps = {
  /** Messages that aligned on DKIM or SPF. */
  compliant: number
  /** Total messages reported in the window. */
  messages: number
  /** Bar width in px; omit to fill the container. */
  width?: number
  /** Appends "· 61 failing" after the percentage. */
  showFailing?: boolean
  className?: string
}

/**
 * Compliance as a *composition* — the aligned share in teal, the failing share in
 * critical red — rather than as a single fill whose colour steps at 95% and 75%.
 *
 * The proportional form is what makes a list scannable: a domain at 99.4% shows a
 * red sliver you can see is a sliver, and one at 40% shows that most of its mail is
 * unauthenticated. The verdict itself stays with the Status badge beside it, so the
 * bar does not have to encode it twice.
 *
 * Callers must not pass this a no-data row: zero messages renders an empty track,
 * which is honest but says less than the explicit "—" those rows show instead.
 */
export function ComplianceSplitBar({
  compliant,
  messages,
  width,
  showFailing = false,
  className,
}: ComplianceSplitBarProps) {
  const aligned = Math.max(0, Math.min(compliant, messages))
  const failing = Math.max(0, messages - aligned)
  const segments: BarSegment[] = [
    { key: 'aligned', value: aligned, color: 'var(--status-ok-dot)' },
    { key: 'failing', value: failing, color: 'var(--status-danger-dot)' },
  ]
  // Unrounded on purpose — formatPercentValue's never-round-up-to-100 guard only
  // works on full precision.
  const rate = messages > 0 ? (aligned / messages) * 100 : null

  return (
    // Without a width the bar flexes and the trailing percentage keeps its own
    // space; a 100%-wide bar inside an inline-flex claims the whole row and pushes
    // the percentage out of the card.
    <span
      className={cn('items-center gap-3', width == null ? 'flex w-full' : 'inline-flex', className)}
    >
      <span
        className={cn('inline-block', width == null ? 'min-w-0 flex-1' : 'shrink-0')}
        style={width != null ? { width } : undefined}
      >
        <SegmentBar
          segments={segments}
          total={messages}
          height={6}
          label={`${aligned.toLocaleString('en-US')} of ${messages.toLocaleString('en-US')} messages aligned`}
        />
      </span>
      <span className="whitespace-nowrap text-sm tabular-nums text-secondary">
        {rate == null ? '—' : formatPercentValue(rate)}
        {showFailing && failing > 0 ? (
          <span className="text-faint"> · {formatCompact(failing)} failing</span>
        ) : null}
      </span>
    </span>
  )
}
