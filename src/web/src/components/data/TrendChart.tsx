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
  className?: string
}

/**
 * Pass/fail daily trend as a compact, dependency-free SVG bar chart: teal pass
 * bars with critical-red fail stacked on top. Ports the design bundle's
 * TrendChart faithfully (viewBox 0 0 100 40, non-uniform scaling).
 */
export function TrendChart({ data = [], height = 160, showLabels = true, className }: TrendChartProps) {
  const w = 100
  const gap = 1.2
  const max = Math.max(1, ...data.map((d) => d.pass + d.fail))
  const bw = data.length ? (w - gap * (data.length - 1)) / data.length : 0

  return (
    <div className={className}>
      <svg
        viewBox={`0 0 ${w} 40`}
        preserveAspectRatio="none"
        className="block w-full"
        style={{ height }}
      >
        {[10, 20, 30].map((y) => (
          <line key={y} x1="0" x2={w} y1={y} y2={y} stroke="#eef3f1" strokeWidth="0.3" />
        ))}
        {data.map((d, i) => {
          const ph = (d.pass / max) * 38
          const fh = (d.fail / max) * 38
          const x = i * (bw + gap)
          return (
            <g key={`${d.label}-${i}`}>
              <rect x={x} width={bw} y={40 - ph} height={ph} rx="0.6" fill="#0e9481" />
              {fh > 0 ? (
                <rect x={x} width={bw} y={40 - ph - fh - 0.5} height={fh} rx="0.6" fill="#dc3d5c" />
              ) : null}
            </g>
          )
        })}
      </svg>
      {showLabels && data.length ? (
        <div className="mt-1.5 flex justify-between font-body text-xs text-faint">
          <span>{data[0].label}</span>
          <span>{data[data.length - 1].label}</span>
        </div>
      ) : null}
    </div>
  )
}
