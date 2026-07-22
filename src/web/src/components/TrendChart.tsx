import { useMemo, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import type { TrendPoint } from '@/lib/analytics'
import { formatCompact, formatFullDate, formatShortDate } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * Series colors validated with the dataviz palette checker on the white card
 * surface (CVD deutan dE 11.0, normal-vision dE 28.7, contrast >= 3:1).
 * Compliant/failed carry pass/fail meaning, so they wear status-style hues:
 * brand teal for compliant, critical red for failed.
 */
const COMPLIANT_COLOR = '#0d9488'
const FAILED_COLOR = '#d03b3b'

const DEFAULT_PLOT_HEIGHT = 224
const DAY_MS = 24 * 60 * 60 * 1000
const MAX_DAYS = 370

const toUtcDayMs = (iso: string): number => {
  const date = new Date(iso.length === 10 ? `${iso}T00:00:00Z` : iso)
  return Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate())
}

/** Fills missing days with zeros across the window (and any trend data outside it). */
function fillDays(trend: TrendPoint[], beginUtc: string, endUtc: string): TrendPoint[] {
  let start = toUtcDayMs(beginUtc)
  let end = toUtcDayMs(endUtc)
  if (trend.length > 0) {
    start = Math.min(start, toUtcDayMs(trend[0].date))
    end = Math.max(end, toUtcDayMs(trend[trend.length - 1].date))
  }
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return trend
  if ((end - start) / DAY_MS > MAX_DAYS) start = end - MAX_DAYS * DAY_MS

  const byDate = new Map(trend.map((point) => [point.date, point]))
  const days: TrendPoint[] = []
  for (let t = start; t <= end; t += DAY_MS) {
    const key = new Date(t).toISOString().slice(0, 10)
    days.push(byDate.get(key) ?? { date: key, messages: 0, compliant: 0, failed: 0 })
  }
  return days
}

/** Smallest "nice" number (1/2/2.5/5 x 10^k) at or above value. */
function niceCeil(value: number): number {
  if (value <= 0) return 1
  const exp = Math.floor(Math.log10(value))
  const base = Math.pow(10, exp)
  const frac = value / base
  const nice = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 2.5 ? 2.5 : frac <= 5 ? 5 : 10
  return nice * base
}

const formatTick = (value: number): string =>
  value < 1000 && !Number.isInteger(value) ? value.toLocaleString('en-US') : formatCompact(value)

type TrendChartProps = {
  trend: TrendPoint[]
  beginUtc: string
  endUtc: string
  /** Plot height in px; defaults to the full-size dashboard chart. */
  height?: number
}

export function TrendChart({ trend, beginUtc, endUtc, height = DEFAULT_PLOT_HEIGHT }: TrendChartProps) {
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const [showTable, setShowTable] = useState(false)

  const days = useMemo(() => fillDays(trend, beginUtc, endUtc), [trend, beginUtc, endUtc])

  const { tickStep, niceMax } = useMemo(() => {
    const maxTotal = days.reduce((max, d) => Math.max(max, d.compliant + d.failed), 0)
    const step = Math.max(1, niceCeil(Math.max(maxTotal, 1) / 4))
    return { tickStep: step, niceMax: step * Math.ceil(Math.max(maxTotal, 1) / step) }
  }, [days])

  const ticks = useMemo(() => {
    const values: number[] = []
    for (let v = 0; v <= niceMax + tickStep / 2; v += tickStep) values.push(v)
    return values
  }, [niceMax, tickStep])

  const hasData = days.some((d) => d.compliant + d.failed > 0)
  const labelStep = Math.max(1, Math.ceil(days.length / 8))
  const hovered = hoverIndex != null ? days[hoverIndex] : null

  const legend = (
    <div className="flex items-center gap-4 text-xs text-muted-foreground">
      <span className="flex items-center gap-1.5">
        <span className="h-2.5 w-2.5 rounded-[3px]" style={{ background: COMPLIANT_COLOR }} />
        Compliant
      </span>
      <span className="flex items-center gap-1.5">
        <span className="h-2.5 w-2.5 rounded-[3px]" style={{ background: FAILED_COLOR }} />
        Failed
      </span>
    </div>
  )

  if (!hasData) {
    return (
      <div>
        <div className="mb-3 flex items-center justify-between">{legend}</div>
        <div
          className="flex items-center justify-center rounded-md border border-dashed"
          style={{ height }}
        >
          <p className="text-sm text-muted-foreground">No message volume in this window.</p>
        </div>
      </div>
    )
  }

  return (
    <figure className="m-0">
      <div className="mb-3 flex items-center justify-between">
        {legend}
        <Button variant="ghost" size="sm" onClick={() => setShowTable((x) => !x)}>
          {showTable ? 'Chart view' : 'Table view'}
        </Button>
      </div>

      {showTable ? (
        <div className="max-h-72 overflow-y-auto rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Date</TableHead>
                <TableHead className="text-right">Compliant</TableHead>
                <TableHead className="text-right">Failed</TableHead>
                <TableHead className="text-right">Total</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {days.map((d) => (
                <TableRow key={d.date}>
                  <TableCell>{formatFullDate(d.date)}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {d.compliant.toLocaleString('en-US')}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {d.failed.toLocaleString('en-US')}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {(d.compliant + d.failed).toLocaleString('en-US')}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : (
        <div className="relative" onMouseLeave={() => setHoverIndex(null)}>
          <div className="relative ml-12" style={{ height }}>
            {/* Gridlines + y ticks: solid hairlines, recessive */}
            {ticks.map((tick) => (
              <div
                key={tick}
                className="pointer-events-none absolute inset-x-0"
                style={{ top: height - (tick / niceMax) * height }}
              >
                <div className={cn('border-t', tick === 0 ? 'border-[#c9d4de]' : 'border-[#e8edf2]')} />
                <span className="absolute -left-12 top-0 w-10 -translate-y-1/2 text-right text-[11px] tabular-nums text-muted-foreground">
                  {formatTick(tick)}
                </span>
              </div>
            ))}

            {/* Bars: full-height bands are the hit targets */}
            <div className="absolute inset-0 flex items-stretch gap-[2px]">
              {days.map((d, i) => {
                const compliantH = (d.compliant / niceMax) * height
                const failedH = (d.failed / niceMax) * height
                const isHovered = hoverIndex === i
                return (
                  <div
                    key={d.date}
                    tabIndex={0}
                    role="img"
                    aria-label={`${formatFullDate(d.date)}: ${d.compliant.toLocaleString('en-US')} compliant, ${d.failed.toLocaleString('en-US')} failed`}
                    className="relative flex min-w-0 flex-1 items-end justify-center outline-none"
                    onMouseEnter={() => setHoverIndex(i)}
                    onFocus={() => setHoverIndex(i)}
                    onBlur={() => setHoverIndex(null)}
                  >
                    {isHovered && (
                      <div className="pointer-events-none absolute inset-y-0 -inset-x-px rounded-sm bg-slate-500/10" />
                    )}
                    <div
                      className="flex w-full max-w-[24px] flex-col justify-end"
                      style={{ filter: isHovered ? 'brightness(1.1)' : undefined }}
                    >
                      {d.failed > 0 && (
                        <div
                          style={{
                            height: Math.max(failedH, 2),
                            background: FAILED_COLOR,
                            borderRadius: '4px 4px 0 0',
                          }}
                        />
                      )}
                      {d.compliant > 0 && (
                        <div
                          style={{
                            height: Math.max(compliantH, 2),
                            background: COMPLIANT_COLOR,
                            borderRadius: d.failed > 0 ? 0 : '4px 4px 0 0',
                            marginTop: d.failed > 0 ? 2 : 0,
                          }}
                        />
                      )}
                    </div>
                  </div>
                )
              })}
            </div>

            {/* Tooltip: values lead, labels follow; every series at this X */}
            {hovered && hoverIndex != null && (
              <div
                className={cn(
                  'pointer-events-none absolute top-2 z-10 rounded-md border bg-card px-3 py-2 shadow-panel',
                  hoverIndex > days.length / 2 ? '-translate-x-full -ml-2' : 'ml-2',
                )}
                style={{ left: `${((hoverIndex + 0.5) / days.length) * 100}%` }}
              >
                <p className="mb-1 whitespace-nowrap text-xs text-muted-foreground">
                  {formatFullDate(hovered.date)}
                </p>
                <div className="space-y-0.5 text-xs">
                  <p className="flex items-center gap-2 whitespace-nowrap">
                    <span className="h-[3px] w-3 rounded-full" style={{ background: COMPLIANT_COLOR }} />
                    <span className="font-semibold tabular-nums">
                      {hovered.compliant.toLocaleString('en-US')}
                    </span>
                    <span className="text-muted-foreground">Compliant</span>
                  </p>
                  <p className="flex items-center gap-2 whitespace-nowrap">
                    <span className="h-[3px] w-3 rounded-full" style={{ background: FAILED_COLOR }} />
                    <span className="font-semibold tabular-nums">
                      {hovered.failed.toLocaleString('en-US')}
                    </span>
                    <span className="text-muted-foreground">Failed</span>
                  </p>
                  <p className="whitespace-nowrap border-t pt-0.5 text-muted-foreground">
                    Total{' '}
                    <span className="font-medium tabular-nums text-foreground">
                      {(hovered.compliant + hovered.failed).toLocaleString('en-US')}
                    </span>
                  </p>
                </div>
              </div>
            )}
          </div>

          {/* X-axis labels: sparse, centered under their band */}
          <div className="relative ml-12 mt-1.5 h-5">
            {days.map((d, i) =>
              i % labelStep === 0 ? (
                <span
                  key={d.date}
                  className="absolute top-1 -translate-x-1/2 whitespace-nowrap text-[11px] text-muted-foreground"
                  style={{ left: `${((i + 0.5) / days.length) * 100}%` }}
                >
                  {formatShortDate(d.date)}
                </span>
              ) : null,
            )}
          </div>
        </div>
      )}
    </figure>
  )
}
