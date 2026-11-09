import { segmentLabel, type BarSegment } from '@/lib/bars'
import { cn } from '@/lib/utils'

type SegmentBarProps = {
  segments: BarSegment[]
  /**
   * Denominator. Defaults to the sum of the segments, which is what a composition
   * bar wants; pass it explicitly when the segments are a *part* of a larger whole
   * and the remainder should read as empty track rather than being scaled away.
   */
  total?: number
  /** Bar thickness in px. */
  height?: number
  className?: string
  /**
   * What the bar says, for people who cannot see it. Required: a bar with no
   * accessible name is decoration, and none of these are.
   */
  label: string
}

/**
 * A horizontal stacked bar of labelled segments — the one shape this app uses for
 * "composition of a total", whether that total is messages (aligned vs failing),
 * dispositions (delivered / quarantined / rejected) or a portfolio of domains by
 * enforcement posture.
 *
 * Zero total renders the empty track rather than collapsing: "nothing reported" and
 * "all of it failed" must not look the same.
 */
export function SegmentBar({ segments, total, height = 8, className, label }: SegmentBarProps) {
  const sum = segments.reduce((acc, segment) => acc + Math.max(0, segment.value), 0)
  const denominator = total != null && total > 0 ? total : sum

  return (
    <span
      className={cn('flex w-full overflow-hidden rounded-[3px] bg-surface-sunken', className)}
      style={{ height }}
      role="img"
      aria-label={label}
    >
      {denominator > 0
        ? segments.map((segment) =>
            segment.value > 0 ? (
              <span
                key={segment.key}
                className="h-full first:rounded-l-[3px] last:rounded-r-[3px]"
                style={{
                  width: `${(Math.max(0, segment.value) / denominator) * 100}%`,
                  background: segment.color,
                }}
                // Native tooltip rather than a popover: the bar sits inside table cells
                // and Card bodies that both clip overflow (see Card's overflow-hidden).
                title={`${segmentLabel(segment)}: ${segment.value.toLocaleString('en-US')}`}
              />
            ) : null,
          )
        : null}
    </span>
  )
}
