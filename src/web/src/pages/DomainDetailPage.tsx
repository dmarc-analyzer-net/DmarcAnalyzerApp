import { ArrowLeft, ChevronRight, Loader2, SearchX } from 'lucide-react'
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'

import { DaysSelector } from '@/components/DaysSelector'
import { SortButton, type SortDir } from '@/components/SortButton'
import { TrendChart } from '@/components/TrendChart'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  DOMAIN_STATUS_META,
  parseAnalyticsDays,
  type AnalyticsDays,
  type DomainDrilldown,
  type DomainSourceAnalytics,
  type DomainStatus,
  type EvaluatedCombo,
  type SourceDetail,
  type ValueCount,
} from '@/lib/analytics'
import { ApiError, fetchJson } from '@/lib/api'
import { formatCompact, formatFullDate, formatPercent, formatRelativeOrDate } from '@/lib/format'
import { cn } from '@/lib/utils'

// --- Sources table sorting (same interaction pattern as DomainsPage) ---

type SourceSortKey =
  | 'ip'
  | 'messages'
  | 'failed'
  | 'compliance'
  | 'dkim'
  | 'spf'
  | 'quarantined'
  | 'rejected'
  | 'reporters'
  | 'lastSeen'

/** Direction applied when a column first becomes the active sort. */
const defaultSortDir: Record<SourceSortKey, SortDir> = {
  ip: 'asc',
  messages: 'desc',
  failed: 'desc',
  compliance: 'asc',
  dkim: 'asc',
  spf: 'asc',
  quarantined: 'desc',
  rejected: 'desc',
  reporters: 'desc',
  lastSeen: 'desc',
}

const compareIps = (a: string, b: string) => a.localeCompare(b, 'en', { numeric: true })

function compareSources(
  a: DomainSourceAnalytics,
  b: DomainSourceAnalytics,
  key: SourceSortKey,
  dir: SortDir,
): number {
  const flip = dir === 'asc' ? 1 : -1
  let cmp = 0
  switch (key) {
    case 'ip':
      cmp = compareIps(a.sourceIp, b.sourceIp)
      break
    case 'messages':
      cmp = a.messages - b.messages
      break
    case 'failed':
      cmp = a.failedMessages - b.failedMessages
      break
    case 'compliance':
      cmp = a.complianceRate - b.complianceRate
      break
    case 'dkim':
      cmp = a.dkimPassRate - b.dkimPassRate
      break
    case 'spf':
      cmp = a.spfPassRate - b.spfPassRate
      break
    case 'quarantined':
      cmp = a.quarantined - b.quarantined
      break
    case 'rejected':
      cmp = a.rejected - b.rejected
      break
    case 'reporters':
      cmp = a.reporters - b.reporters
      break
    case 'lastSeen':
      cmp = Date.parse(a.lastSeenUtc) - Date.parse(b.lastSeenUtc)
      break
  }
  if (cmp !== 0) return cmp * flip

  // Stable tie-breakers mirroring the server's worst-first ordering.
  if (a.failedMessages !== b.failedMessages) return b.failedMessages - a.failedMessages
  if (a.messages !== b.messages) return b.messages - a.messages
  return compareIps(a.sourceIp, b.sourceIp)
}

// --- Small presentational helpers ---

/** Maps a per-source compliance rate onto the shared domain status colors. */
function rateStatus(rate: number): DomainStatus {
  if (rate >= 0.98) return 'aligned'
  if (rate >= 0.8) return 'issues'
  return 'critical'
}

function RateMeter({ rate }: { rate: number }) {
  const meta = DOMAIN_STATUS_META[rateStatus(rate)]
  const pct = Math.max(0, Math.min(1, rate)) * 100
  return (
    <div className="flex items-center gap-2">
      <span className="w-12 text-right tabular-nums">{formatPercent(rate)}</span>
      <div
        className="h-1.5 w-16 shrink-0 overflow-hidden rounded-full"
        style={{ background: meta.track }}
        role="presentation"
      >
        <div className="h-full rounded-full" style={{ width: `${pct}%`, background: meta.fill }} />
      </div>
    </div>
  )
}

type StatTileProps = {
  label: string
  value: string
  sub?: string
  subClassName?: string
}

function StatTile({ label, value, sub, subClassName }: StatTileProps) {
  return (
    <Card>
      <CardContent className="pt-4">
        <p className="text-xs font-medium text-muted-foreground">{label}</p>
        <p className="mt-1 text-2xl font-semibold">{value}</p>
        {sub && <p className={cn('mt-0.5 text-xs text-muted-foreground', subClassName)}>{sub}</p>}
      </CardContent>
    </Card>
  )
}

function PanelSectionTitle({ children }: { children: ReactNode }) {
  return (
    <h4 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
      {children}
    </h4>
  )
}

/** Colors a raw DKIM/SPF verdict: pass green, hard failures red, soft results amber. */
function resultTone(result: string): string {
  const value = result.toLowerCase()
  if (value === 'pass') return 'text-emerald-700'
  if (value === 'fail' || value === 'permerror' || value === 'temperror') return 'text-rose-700'
  return 'text-amber-700'
}

function EvaluatedChip({ combo }: { combo: EvaluatedCombo }) {
  const compliant = combo.dkim === 'pass' || combo.spf === 'pass'
  const tone = (result: 'pass' | 'fail') =>
    result === 'pass' ? 'text-emerald-700' : 'text-rose-700'
  return (
    <div
      className={cn(
        'flex items-center gap-2 rounded-md border bg-card px-3 py-1.5 text-xs',
        compliant ? 'border-emerald-200' : 'border-rose-200',
      )}
    >
      <span className="text-muted-foreground">
        DKIM <span className={cn('font-semibold uppercase', tone(combo.dkim))}>{combo.dkim}</span>
      </span>
      <span aria-hidden className="text-muted-foreground/50">
        /
      </span>
      <span className="text-muted-foreground">
        SPF <span className={cn('font-semibold uppercase', tone(combo.spf))}>{combo.spf}</span>
      </span>
      <span className="font-semibold tabular-nums">{formatCompact(combo.messages)} msgs</span>
      <Badge variant={compliant ? 'success' : 'danger'}>{compliant ? 'compliant' : 'failed'}</Badge>
    </div>
  )
}

function ValueList({ items, emptyText }: { items: ValueCount[]; emptyText: string }) {
  if (items.length === 0) {
    return <p className="mt-2 text-sm text-muted-foreground">{emptyText}</p>
  }
  return (
    <ul className="mt-2 space-y-1.5">
      {items.map((item) => (
        <li key={item.value} className="flex items-baseline justify-between gap-3">
          <span className="min-w-0 break-all font-mono text-xs">{item.value}</span>
          <span className="text-xs tabular-nums text-muted-foreground">
            {formatCompact(item.messages)}
          </span>
        </li>
      ))}
    </ul>
  )
}

// --- Expandable per-source detail panel ---

type SourceDetailPanelProps = {
  domainId: string
  sourceIp: string
  days: AnalyticsDays
  windowBeginUtc: string
  windowEndUtc: string
}

function SourceDetailPanel({
  domainId,
  sourceIp,
  days,
  windowBeginUtc,
  windowEndUtc,
}: SourceDetailPanelProps) {
  const [detail, setDetail] = useState<SourceDetail | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const requestSeq = useRef(0)

  const loadDetail = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    setDetail(null)
    try {
      const payload = await fetchJson<SourceDetail>(
        `/api/v1/analytics/domains/${domainId}/source-detail?ip=${encodeURIComponent(sourceIp)}&days=${days}`,
      )
      if (seq === requestSeq.current) setDetail(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load source detail')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [domainId, sourceIp, days])

  useEffect(() => {
    void loadDetail()
  }, [loadDetail])

  if (busy) {
    return (
      <div className="flex items-center gap-2 px-5 py-6 text-sm text-muted-foreground">
        <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
        Loading source detail…
      </div>
    )
  }

  if (error) {
    return (
      <div className="px-5 py-4">
        <p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {error}
        </p>
      </div>
    )
  }

  if (!detail) return null

  const evaluated = [...detail.evaluated].sort((a, b) => b.messages - a.messages)

  return (
    <div className="space-y-4 px-5 py-4">
      {/* Policy-evaluated DKIM x SPF combos: this is what DMARC compliance is judged on. */}
      <section>
        <PanelSectionTitle>DMARC evaluation</PanelSectionTitle>
        <p className="mt-1 text-xs text-muted-foreground">
          {formatCompact(detail.compliantMessages)} of {formatCompact(detail.messages)} messages
          compliant ({formatPercent(detail.complianceRate)}) — a message is compliant when DKIM or
          SPF passes with alignment.
        </p>
        {evaluated.length === 0 ? (
          <p className="mt-2 text-sm text-muted-foreground">No evaluation results reported.</p>
        ) : (
          <div className="mt-2 flex flex-wrap gap-2">
            {evaluated.map((combo) => (
              <EvaluatedChip key={`${combo.dkim}-${combo.spf}`} combo={combo} />
            ))}
          </div>
        )}
        <div className="mt-3 flex flex-wrap items-center gap-2 text-xs">
          <span className="font-medium text-muted-foreground">Dispositions</span>
          <Badge variant="muted">none · {formatCompact(detail.dispositions.none)}</Badge>
          <Badge variant="warning">
            quarantine · {formatCompact(detail.dispositions.quarantine)}
          </Badge>
          <Badge variant="danger">reject · {formatCompact(detail.dispositions.reject)}</Badge>
        </div>
      </section>

      {/* Raw auth results identify the actual sending service. */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section className="rounded-md border bg-card p-3">
          <PanelSectionTitle>Raw DKIM authentication</PanelSectionTitle>
          {detail.dkimAuth.length === 0 ? (
            <p className="mt-2 text-sm text-muted-foreground">No DKIM signatures reported.</p>
          ) : (
            <Table className="mt-1">
              <TableHeader>
                <TableRow>
                  <TableHead>Domain</TableHead>
                  <TableHead>Selector</TableHead>
                  <TableHead>Result</TableHead>
                  <TableHead className="text-right">Messages</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {detail.dkimAuth.map((row, index) => (
                  <TableRow key={`${row.domain}-${row.selector ?? ''}-${row.result}-${index}`}>
                    <TableCell className="break-all font-mono text-xs">{row.domain}</TableCell>
                    <TableCell className="font-mono text-xs">{row.selector ?? '—'}</TableCell>
                    <TableCell>
                      <span className={cn('font-medium', resultTone(row.result))}>
                        {row.result}
                      </span>
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatCompact(row.messages)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>

        <section className="rounded-md border bg-card p-3">
          <PanelSectionTitle>Raw SPF authentication</PanelSectionTitle>
          {detail.spfAuth.length === 0 ? (
            <p className="mt-2 text-sm text-muted-foreground">No SPF checks reported.</p>
          ) : (
            <Table className="mt-1">
              <TableHeader>
                <TableRow>
                  <TableHead>Domain</TableHead>
                  <TableHead>Scope</TableHead>
                  <TableHead>Result</TableHead>
                  <TableHead className="text-right">Messages</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {detail.spfAuth.map((row, index) => (
                  <TableRow key={`${row.domain}-${row.scope ?? ''}-${row.result}-${index}`}>
                    <TableCell className="break-all font-mono text-xs">{row.domain}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {row.scope ?? '—'}
                    </TableCell>
                    <TableCell>
                      <span className={cn('font-medium', resultTone(row.result))}>
                        {row.result}
                      </span>
                    </TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatCompact(row.messages)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>
      </div>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <section className="rounded-md border bg-card p-3">
          <PanelSectionTitle>Header From</PanelSectionTitle>
          <ValueList items={detail.headerFroms} emptyText="No header-from domains reported." />
        </section>
        <section className="rounded-md border bg-card p-3">
          <PanelSectionTitle>Envelope From</PanelSectionTitle>
          <ValueList items={detail.envelopeFroms} emptyText="No envelope-from domains reported." />
        </section>
        <section className="rounded-md border bg-card p-3">
          <PanelSectionTitle>Reporters</PanelSectionTitle>
          {detail.reporters.length === 0 ? (
            <p className="mt-2 text-sm text-muted-foreground">No reporters in this window.</p>
          ) : (
            <ul className="mt-2 space-y-1.5">
              {detail.reporters.map((reporter) => (
                <li
                  key={reporter.organizationName}
                  className="flex items-baseline justify-between gap-3"
                >
                  <span className="min-w-0 break-all text-sm">{reporter.organizationName}</span>
                  <span className="whitespace-nowrap text-xs tabular-nums text-muted-foreground">
                    {formatCompact(reporter.messages)} msgs · {formatCompact(reporter.reports)}{' '}
                    rpts
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <section className="rounded-md border bg-card p-3">
        <PanelSectionTitle>Daily volume from {detail.sourceIp}</PanelSectionTitle>
        <div className="mt-2">
          <TrendChart
            trend={detail.trend}
            beginUtc={windowBeginUtc}
            endUtc={windowEndUtc}
            height={128}
          />
        </div>
      </section>
    </div>
  )
}

// --- Page ---

const SOURCE_COLUMN_COUNT = 10

export function DomainDetailPage() {
  const { domainId = '' } = useParams()
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))
  const clientParam = searchParams.get('client')
  const selectedSource = searchParams.get('source')

  const [drilldown, setDrilldown] = useState<DomainDrilldown | null>(null)
  const [sources, setSources] = useState<DomainSourceAnalytics[]>([])
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notFound, setNotFound] = useState(false)
  const [sortKey, setSortKey] = useState<SourceSortKey | null>(null)
  const [sortDir, setSortDir] = useState<SortDir>('desc')
  const [hostnames, setHostnames] = useState<Record<string, string | null>>({})
  const requestSeq = useRef(0)

  const loadData = useCallback(async () => {
    if (!domainId) return
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const [drilldownData, sourceData] = await Promise.all([
        fetchJson<DomainDrilldown>(`/api/v1/analytics/domains/${domainId}/drilldown?days=${days}`),
        fetchJson<DomainSourceAnalytics[]>(
          `/api/v1/analytics/domains/${domainId}/sources?days=${days}`,
        ),
      ])
      if (seq !== requestSeq.current) return
      setDrilldown(drilldownData)
      setSources(sourceData)
      setNotFound(false)
    } catch (loadError) {
      if (seq !== requestSeq.current) return
      if (loadError instanceof ApiError && loadError.status === 404) {
        setNotFound(true)
      } else {
        setError(
          loadError instanceof Error ? loadError.message : 'Failed to load domain analytics',
        )
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [domainId, days])

  useEffect(() => {
    void loadData()
  }, [loadData])

  // Reverse-DNS enrichment: resolved lazily after the table renders so slow
  // PTR lookups never block the sources list. Merges keep earlier answers.
  useEffect(() => {
    if (sources.length === 0) return
    let cancelled = false
    const ips = sources.slice(0, 100).map((s) => s.sourceIp)
    void fetchJson<Record<string, string | null>>(
      `/api/v1/analytics/hostnames?ips=${encodeURIComponent(ips.join(','))}`,
    )
      .then((resolved) => {
        if (!cancelled) setHostnames((prev) => ({ ...prev, ...resolved }))
      })
      .catch(() => {
        // Hostname enrichment is best-effort; the table stays IP-only on failure.
      })
    return () => {
      cancelled = true
    }
  }, [sources])

  // Back link to the domains list, preserving the window and client filter it was opened with.
  const backHref = useMemo(() => {
    const params = new URLSearchParams()
    if (days !== 30) params.set('days', String(days))
    if (clientParam) params.set('client', clientParam)
    const query = params.toString()
    return query ? `/domains?${query}` : '/domains'
  }, [days, clientParam])

  const setDays = (next: AnalyticsDays) => {
    setSearchParams(
      (prev) => {
        const params = new URLSearchParams(prev)
        if (next === 30) params.delete('days')
        else params.set('days', String(next))
        return params
      },
      { replace: true },
    )
  }

  // ?source=<ip> drives the (single) expanded row, so expanded state is linkable.
  const toggleSource = (ip: string) => {
    setSearchParams(
      (prev) => {
        const params = new URLSearchParams(prev)
        if (params.get('source') === ip) params.delete('source')
        else params.set('source', ip)
        return params
      },
      { replace: true },
    )
  }

  const handleSort = (key: SourceSortKey) => {
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortKey(key)
      setSortDir(defaultSortDir[key])
    }
  }

  const ariaSort: 'ascending' | 'descending' = sortDir === 'asc' ? 'ascending' : 'descending'

  // Server order (failed desc, then messages desc) until a column sort is chosen.
  const sortedSources = useMemo(() => {
    if (!sortKey) return sources
    return [...sources].sort((a, b) => compareSources(a, b, sortKey, sortDir))
  }, [sources, sortKey, sortDir])

  if (notFound) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-16 text-center">
          <SearchX className="h-10 w-10 text-muted-foreground" aria-hidden />
          <div>
            <p className="text-base font-semibold">Domain not found</p>
            <p className="mt-1 max-w-md text-sm text-muted-foreground">
              This domain does not exist or may have been removed.
            </p>
          </div>
          <Button asChild variant="outline" size="sm">
            <Link to={backHref}>
              <ArrowLeft className="h-4 w-4" />
              Back to Domains
            </Link>
          </Button>
        </CardContent>
      </Card>
    )
  }

  const totals = drilldown?.totals
  const statusMeta = totals ? DOMAIN_STATUS_META[totals.status] : null

  return (
    <>
      <Card>
        <CardHeader>
          <div className="min-w-0">
            <Link
              to={backHref}
              className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground transition-colors hover:text-primary"
            >
              <ArrowLeft className="h-3.5 w-3.5" aria-hidden />
              Domains
            </Link>
            <div className="mt-1.5 flex flex-wrap items-center gap-2">
              <CardTitle className="break-all font-mono text-2xl">
                {drilldown?.domain.name ?? 'Domain drill-down'}
              </CardTitle>
              {drilldown && (
                <>
                  {drilldown.domain.clientSlug === 'default' ? (
                    <Badge variant="warning">Default — needs client</Badge>
                  ) : (
                    <Badge variant="muted">{drilldown.domain.clientName}</Badge>
                  )}
                  {statusMeta && <Badge variant={statusMeta.badge}>{statusMeta.label}</Badge>}
                  <Badge variant={drilldown.domain.isActive ? 'success' : 'muted'}>
                    {drilldown.domain.isActive ? 'Active' : 'Inactive'}
                  </Badge>
                </>
              )}
            </div>
            {drilldown && (
              <p className="mt-1 text-xs text-muted-foreground">
                {drilldown.window.anchoredToLatestData
                  ? `Data through ${formatFullDate(drilldown.window.endUtc)} — window anchored to the latest report data`
                  : `Last ${drilldown.window.days} days`}
              </p>
            )}
          </div>
          <DaysSelector value={days} onChange={setDays} disabled={busy} />
        </CardHeader>
        {!!error && (
          <CardContent>
            <p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {error}
            </p>
          </CardContent>
        )}
      </Card>

      {!drilldown && busy && (
        <div className="flex justify-center py-20">
          <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" aria-label="Loading" />
        </div>
      )}

      {drilldown && totals && (
        <div className={cn('space-y-4 transition-opacity', busy && 'opacity-60')}>
          {/* Stat tiles: compliance hero + the numbers that explain it */}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Card>
              <CardContent className="pt-4">
                <p className="text-xs font-medium text-muted-foreground">DMARC compliance</p>
                <p className="mt-1 text-5xl font-semibold leading-tight text-primary">
                  {totals.status === 'no_data' ? '—' : formatPercent(totals.complianceRate)}
                </p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  {formatCompact(totals.compliantMessages)} of {formatCompact(totals.messages)}{' '}
                  messages compliant
                </p>
              </CardContent>
            </Card>
            <StatTile
              label="Messages"
              value={formatCompact(totals.messages)}
              sub={`across ${formatCompact(totals.reports)} reports`}
            />
            <StatTile label="DKIM pass rate" value={formatPercent(totals.dkimPassRate)} />
            <StatTile label="SPF pass rate" value={formatPercent(totals.spfPassRate)} />
          </div>

          <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
            <StatTile
              label="Sending sources"
              value={totals.sources.toLocaleString('en-US')}
              sub="unique IPs in this window"
            />
            <StatTile
              label="Reporters"
              value={totals.reporters.toLocaleString('en-US')}
              sub="organizations reporting"
            />
            <StatTile
              label="Quarantined + rejected"
              value={formatCompact(totals.quarantined + totals.rejected)}
              sub={`${formatCompact(totals.quarantined)} quarantined · ${formatCompact(totals.rejected)} rejected`}
              subClassName={
                totals.quarantined + totals.rejected > 0
                  ? 'font-medium text-destructive'
                  : undefined
              }
            />
          </div>

          {/* Domain-level trend */}
          <Card>
            <CardHeader>
              <div>
                <CardTitle>Message volume</CardTitle>
                <CardDescription className="mt-1">
                  Daily messages, compliant vs failed
                </CardDescription>
              </div>
            </CardHeader>
            <CardContent>
              <TrendChart
                trend={drilldown.trend}
                beginUtc={drilldown.window.beginUtc}
                endUtc={drilldown.window.endUtc}
              />
            </CardContent>
          </Card>

          {/* The centerpiece: per-source breakdown */}
          <Card>
            <CardHeader>
              <div>
                <CardTitle>Sending sources</CardTitle>
                <CardDescription className="mt-1">
                  Per-IP DMARC results over the last {days} days, worst offenders first — expand a
                  row for the full authentication breakdown
                </CardDescription>
              </div>
              <Badge variant="muted">{sources.length} sources</Badge>
            </CardHeader>
            <CardContent>
              {sources.length === 0 ? (
                <p className="py-6 text-center text-sm text-muted-foreground">
                  No sending sources reported in this window.
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead aria-sort={sortKey === 'ip' ? ariaSort : undefined}>
                          <SortButton label="Source IP" column="ip" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'messages' ? ariaSort : undefined}>
                          <SortButton label="Messages" column="messages" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'failed' ? ariaSort : undefined}>
                          <SortButton label="Failed" column="failed" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead aria-sort={sortKey === 'compliance' ? ariaSort : undefined}>
                          <SortButton label="Compliance" column="compliance" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'dkim' ? ariaSort : undefined}>
                          <SortButton label="DKIM" column="dkim" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'spf' ? ariaSort : undefined}>
                          <SortButton label="SPF" column="spf" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'quarantined' ? ariaSort : undefined}>
                          <SortButton label="Quarantined" column="quarantined" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'rejected' ? ariaSort : undefined}>
                          <SortButton label="Rejected" column="rejected" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead className="text-right" aria-sort={sortKey === 'reporters' ? ariaSort : undefined}>
                          <SortButton label="Reporters" column="reporters" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                        <TableHead aria-sort={sortKey === 'lastSeen' ? ariaSort : undefined}>
                          <SortButton label="Last seen" column="lastSeen" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                        </TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {sortedSources.map((source) => {
                        const expanded = selectedSource === source.sourceIp
                        return (
                          <Fragment key={source.sourceIp}>
                            <TableRow
                              className={cn('cursor-pointer', expanded && 'bg-muted/35')}
                              onClick={() => toggleSource(source.sourceIp)}
                            >
                              <TableCell>
                                <button
                                  type="button"
                                  aria-expanded={expanded}
                                  onClick={(event) => {
                                    event.stopPropagation()
                                    toggleSource(source.sourceIp)
                                  }}
                                  className="inline-flex items-center gap-1.5 rounded-sm font-mono text-sm font-medium transition-colors hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                                >
                                  <ChevronRight
                                    aria-hidden
                                    className={cn(
                                      'h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform',
                                      expanded && 'rotate-90',
                                    )}
                                  />
                                  {source.sourceIp}
                                </button>
                                {hostnames[source.sourceIp] && (
                                  <div className="mt-0.5 pl-5 text-xs text-muted-foreground">
                                    {hostnames[source.sourceIp]}
                                  </div>
                                )}
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {formatCompact(source.messages)}
                              </TableCell>
                              <TableCell
                                className={cn(
                                  'text-right tabular-nums',
                                  source.failedMessages > 0 && 'font-medium text-rose-700',
                                )}
                              >
                                {formatCompact(source.failedMessages)}
                              </TableCell>
                              <TableCell>
                                <RateMeter rate={source.complianceRate} />
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {formatPercent(source.dkimPassRate)}
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {formatPercent(source.spfPassRate)}
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {formatCompact(source.quarantined)}
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {formatCompact(source.rejected)}
                              </TableCell>
                              <TableCell className="text-right tabular-nums">
                                {source.reporters.toLocaleString('en-US')}
                              </TableCell>
                              <TableCell className="whitespace-nowrap text-muted-foreground">
                                {formatRelativeOrDate(source.lastSeenUtc)}
                              </TableCell>
                            </TableRow>
                            {expanded && (
                              <TableRow className="hover:bg-transparent">
                                <TableCell
                                  colSpan={SOURCE_COLUMN_COUNT}
                                  className="bg-muted/40 p-0"
                                >
                                  <SourceDetailPanel
                                    domainId={domainId}
                                    sourceIp={source.sourceIp}
                                    days={days}
                                    windowBeginUtc={drilldown.window.beginUtc}
                                    windowEndUtc={drilldown.window.endUtc}
                                  />
                                </TableCell>
                              </TableRow>
                            )}
                          </Fragment>
                        )
                      })}
                    </TableBody>
                  </Table>
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      )}
    </>
  )
}
