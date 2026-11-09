import { formatCompact } from '@/lib/format'
import { cn } from '@/lib/utils'

export type TrendDatum = {
  label: string
  pass: number
  fail: number
}

type TrendChartProps = {
  data?: TrendDatum[]
  /** Plot height in px. */
  height?: number
  showLabels?: boolean
  /**
   * Renders the pass/fail key and the peak-day value above the plot. Off by
   * default so the compact in-panel copies stay compact.
   */
  legend?: boolean
  className?: string
}

/**
 * Pass/fail daily trend as a compact, dependency-free SVG bar chart: teal pass
 * bars with critical-red fail stacked on top. Ports the design bundle's
 * TrendChart faithfully (viewBox 0 0 100 40, non-uniform scaling).
 *
 * Every bar carries an SVG `<title>` so a day's numbers are readable on hover
 * without a tooltip layer — the chart renders inside Cards that clip overflow.
 * The whole plot is one `role="img"`, because forty individually-focusable bars
 * is worse for a screen reader than one sentence.
 */
export function TrendChart({
  data = [],
  height = 160,
  showLabels = true,
  legend = false,
  className,
}: TrendChartProps) {
  const w = 100
  const gap = data.length > 1 ? Math.min(1.2, (w / data.length) * 0.25) : 0
  const peak = Math.max(0, ...data.map((d) => d.pass + d.fail))
  const max = Math.max(1, peak)
  const bw = data.length ? Math.max(0, (w - gap * (data.length - 1)) / data.length) : 0

  const totalPass = data.reduce((acc, d) => acc + d.pass, 0)
  const totalFail = data.reduce((acc, d) => acc + d.fail, 0)
  const summary = data.length
    ? `Daily message volume from ${data[0].label} to ${data[data.length - 1].label}: ` +
      `${totalPass.toLocaleString('en-US')} compliant, ${totalFail.toLocaleString('en-US')} failed, ` +
      `peak day ${peak.toLocaleString('en-US')} messages.`
    : 'No message volume in this window.'

  return (
    <div className={className}>
      {legend ? (
        <div className="mb-2 flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
          <div className="flex items-center gap-3">
            <TrendKey color="var(--status-ok-dot)" label="Compliant" />
            <TrendKey color="var(--status-danger-dot)" label="Failed" />
          </div>
          {/* The plot has no y axis, so without this the tallest bar has no value. */}
          <span className="text-xs tabular-nums text-faint">
            {peak > 0 ? `peak ${formatCompact(peak)}/day` : 'no volume'}
          </span>
        </div>
      ) : null}
      <svg
        viewBox={`0 0 ${w} 40`}
        preserveAspectRatio="none"
        className="block w-full"
        style={{ height }}
        role="img"
        aria-label={summary}
      >
        {[10, 20, 30].map((y) => (
          <line key={y} x1="0" x2={w} y1={y} y2={y} stroke="var(--surface-sunken)" strokeWidth="0.3" />
        ))}
        {data.map((d, i) => {
          const ph = (d.pass / max) * 38
          const fh = (d.fail / max) * 38
          const x = i * (bw + gap)
          return (
            <g key={`${d.label}-${i}`}>
              <title>
                {`${d.label}: ${d.pass.toLocaleString('en-US')} compliant, ${d.fail.toLocaleString('en-US')} failed`}
              </title>
              <rect x={x} width={bw} y={40 - ph} height={ph} rx="0.6" fill="var(--status-ok-dot)" />
              {fh > 0 ? (
                <rect
                  x={x}
                  width={bw}
                  y={40 - ph - fh - 0.5}
                  height={fh}
                  rx="0.6"
                  fill="var(--status-danger-dot)"
                />
              ) : null}
              {/* An invisible full-height hit area: a one-message day is a 0.1px bar
                  nobody can hover, and the <title> above covers the whole group. */}
              <rect x={x} width={bw} y="0" height="40" fill="transparent" />
            </g>
          )
        })}
      </svg>
      {showLabels && data.length ? (
        <div className={cn('mt-1.5 flex justify-between font-body text-xs text-faint')}>
          <span>{data[0].label}</span>
          <span>{data[data.length - 1].label}</span>
        </div>
      ) : null}
    </div>
  )
}

function TrendKey({ color, label }: { color: string; label: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-secondary">
      <span className="h-2 w-2 shrink-0 rounded-[2px]" style={{ background: color }} aria-hidden />
      {label}
    </span>
  )
}
