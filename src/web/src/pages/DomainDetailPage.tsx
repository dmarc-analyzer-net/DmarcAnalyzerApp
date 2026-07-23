import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'

import { ComplianceBar } from '@/components/data/ComplianceBar'
import { PolicyBadge } from '@/components/data/PolicyBadge'
import { SortHeader, type SortDir } from '@/components/data/SortHeader'
import { StatCard } from '@/components/data/StatCard'
import { TrendChart } from '@/components/data/TrendChart'
import { DaysSelector } from '@/components/DaysSelector'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon, type IconName } from '@/components/ui/icon'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  ENFORCEMENT_STATUS_META,
  parseAnalyticsDays,
  resolveEnforcementStatus,
  type AnalyticsDays,
  type DomainDrilldown,
  type DomainSourceAnalytics,
  type DrilldownDomain,
  type DrilldownTotals,
  type EnforcementGuidance,
  type EvaluatedCombo,
  type RecordInspection,
  type SourceDetail,
  type ValueCount,
} from '@/lib/analytics'
import { ApiError, fetchJson } from '@/lib/api'
import { formatCompact, formatFullDate, formatPercent, formatRelativeOrDate, formatShortDate } from '@/lib/format'
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

/** Maps a pass/fail trend series onto the shared TrendChart datum shape. */
function trendData(trend: DomainDrilldown['trend']) {
  return trend.map((point) => ({
    label: formatShortDate(point.date),
    pass: point.compliant,
    fail: point.failed,
  }))
}

type StatusTone = 'ok' | 'warn' | 'danger' | 'neutral'

const TONE_DOT: Record<StatusTone, string> = {
  ok: 'var(--status-ok-dot)',
  warn: 'var(--status-warn-dot)',
  danger: 'var(--status-danger-dot)',
  neutral: 'var(--status-neutral-dot)',
}

const TONE_ICON: Record<StatusTone, IconName> = {
  ok: 'circle-check',
  warn: 'triangle-alert',
  danger: 'circle-alert',
  neutral: 'info',
}

function PanelSectionTitle({ children }: { children: ReactNode }) {
  return (
    <h4 className="text-xs font-semibold uppercase tracking-wide text-secondary">{children}</h4>
  )
}

/** Colors a raw DKIM/SPF verdict: pass ok, hard failures danger, soft results warn. */
function resultTone(result: string): string {
  const value = result.toLowerCase()
  if (value === 'pass') return 'text-[var(--status-ok-fg)]'
  if (value === 'fail' || value === 'permerror' || value === 'temperror')
    return 'text-[var(--status-danger-fg)]'
  return 'text-[var(--status-warn-fg)]'
}

function EvaluatedChip({ combo }: { combo: EvaluatedCombo }) {
  const compliant = combo.dkim === 'pass' || combo.spf === 'pass'
  const tone = (result: 'pass' | 'fail') =>
    result === 'pass' ? 'text-[var(--status-ok-fg)]' : 'text-[var(--status-danger-fg)]'
  return (
    <div className="flex items-center gap-2 rounded-md border border-border bg-surface-card px-3 py-1.5 text-xs">
      <span className="text-secondary">
        DKIM <span className={cn('font-semibold uppercase', tone(combo.dkim))}>{combo.dkim}</span>
      </span>
      <span aria-hidden className="text-faint">
        /
      </span>
      <span className="text-secondary">
        SPF <span className={cn('font-semibold uppercase', tone(combo.spf))}>{combo.spf}</span>
      </span>
      <span className="font-semibold tabular-nums text-body">{formatCompact(combo.messages)} msgs</span>
      <Badge variant={compliant ? 'success' : 'danger'}>{compliant ? 'compliant' : 'failed'}</Badge>
    </div>
  )
}

function ValueList({ items, emptyText }: { items: ValueCount[]; emptyText: string }) {
  if (items.length === 0) {
    return <p className="mt-2 text-sm text-secondary">{emptyText}</p>
  }
  return (
    <ul className="mt-2 space-y-1.5">
      {items.map((item) => (
        <li key={item.value} className="flex items-baseline justify-between gap-3">
          <span className="min-w-0 break-all font-mono text-xs text-body">{item.value}</span>
          <span className="text-xs tabular-nums text-secondary">{formatCompact(item.messages)}</span>
        </li>
      ))}
    </ul>
  )
}

// --- Path to enforcement checklist (derived from real signals) ---

type EnforcementCheck = {
  tone: StatusTone
  title: string
  detail: string
}

function buildEnforcementChecks(
  domain: DrilldownDomain,
  totals: DrilldownTotals,
): EnforcementCheck[] {
  const policy = domain.publishedPolicy ?? 'none'
  const atQuarantine = policy === 'quarantine' || policy === 'reject'
  return [
    {
      tone: totals.reports > 0 ? 'ok' : 'danger',
      title: totals.reports > 0 ? 'Receiving DMARC reports' : 'No DMARC reports yet',
      detail:
        totals.reports > 0
          ? `${formatCompact(totals.reports)} reports · ${formatCompact(totals.messages)} messages`
          : 'Aggregate reports are not arriving for this domain',
    },
    {
      tone: totals.dkimPassRate >= 0.95 ? 'ok' : 'warn',
      title: totals.dkimPassRate >= 0.95 ? 'DKIM aligned' : 'DKIM alignment gaps',
      detail: `${formatPercent(totals.dkimPassRate)} of mail passes DKIM`,
    },
    {
      tone: totals.spfPassRate >= 0.95 ? 'ok' : 'warn',
      title: totals.spfPassRate >= 0.95 ? 'SPF aligned' : 'SPF alignment gaps',
      detail: `${formatPercent(totals.spfPassRate)} of mail passes SPF`,
    },
    {
      tone: atQuarantine ? 'ok' : 'warn',
      title: atQuarantine ? 'Policy at quarantine or stronger' : 'Policy still monitoring',
      detail: `Published p=${policy}`,
    },
    {
      tone: policy === 'reject' ? 'ok' : 'neutral',
      title: policy === 'reject' ? 'Full enforcement reached' : 'Not yet at p=reject',
      detail:
        policy === 'reject'
          ? 'DMARC is enforcing at reject'
          : 'Reject blocks spoofed mail outright',
    },
  ]
}

/** Short SPF/DKIM alignment + rollout summary, or null when unknown. */
function alignmentSummary(domain: DrilldownDomain): string | null {
  const parts: string[] = []
  if (domain.spfAlignment) parts.push(`SPF ${domain.spfAlignment}`)
  if (domain.dkimAlignment) parts.push(`DKIM ${domain.dkimAlignment}`)
  if (domain.publishedPct != null && domain.publishedPct < 100) {
    parts.push(`${domain.publishedPct}% rollout`)
  }
  return parts.length ? parts.join(' · ') : null
}

// --- Record inspection (live DNS vs observed policy) ---

const LOOKUP_STATUS_META: Record<
  'found' | 'missing' | 'lookup_failed',
  { label: string; badge: 'success' | 'danger' | 'warning' }
> = {
  found: { label: 'Published', badge: 'success' },
  missing: { label: 'Missing', badge: 'danger' },
  lookup_failed: { label: 'Lookup failed', badge: 'warning' },
}

function RecordBlock({
  title,
  status,
  raw,
  meta,
  issues,
}: {
  title: string
  status: RecordInspection['dmarc']['status']
  raw: string | null
  meta?: string | null
  issues: string[]
}) {
  const statusMeta = LOOKUP_STATUS_META[status]
  return (
    <div>
      <div className="flex items-center gap-2">
        <PanelSectionTitle>{title}</PanelSectionTitle>
        <Badge variant={statusMeta.badge}>{statusMeta.label}</Badge>
        {meta ? <span className="font-mono text-xs text-secondary">{meta}</span> : null}
      </div>
      {raw ? (
        <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-all rounded-md border border-border bg-surface-sunken px-3 py-2 font-mono text-xs leading-relaxed text-body">
          {raw}
        </pre>
      ) : null}
      {issues.length > 0 ? (
        <ul className="mt-2 space-y-1">
          {issues.map((issue) => (
            <li key={issue} className="flex items-start gap-1.5 text-xs text-[var(--status-warn-fg)]">
              <Icon name="triangle-alert" size={13} className="mt-px shrink-0" />
              {issue}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}

/**
 * Live DNS DMARC/SPF records vs the policy reporters observed. Fetched
 * separately from the analytics payload because the server does real DNS
 * lookups — a slow resolver must never block the drill-down render.
 */
function RecordInspectionCard({ domainId }: { domainId: string }) {
  const [inspection, setInspection] = useState<RecordInspection | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setBusy(true)
    setError(null)
    void fetchJson<RecordInspection>(`/api/v1/analytics/domains/${domainId}/records`)
      .then((payload) => {
        if (!cancelled) setInspection(payload)
      })
      .catch((loadError: unknown) => {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to inspect records')
        }
      })
      .finally(() => {
        if (!cancelled) setBusy(false)
      })
    return () => {
      cancelled = true
    }
  }, [domainId])

  const mismatches = inspection?.comparison.filter((c) => !c.match) ?? []

  return (
    <Card pad>
      <CardHeader
        title="Record inspection"
        description="The DMARC and SPF records published in DNS right now, checked against what reporters observed"
      />
      {busy ? (
        <div className="flex items-center gap-2 py-4 text-sm text-secondary">
          <Icon name="loader-circle" size={16} className="animate-spin" />
          Looking up live DNS records…
        </div>
      ) : error ? (
        <p className="rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </p>
      ) : inspection ? (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <RecordBlock
            title="DMARC (live DNS)"
            status={inspection.dmarc.status}
            raw={inspection.dmarc.raw}
            issues={inspection.dmarc.issues}
          />
          <RecordBlock
            title="SPF (live DNS)"
            status={inspection.spf.status}
            raw={inspection.spf.raw}
            meta={
              inspection.spf.status === 'found'
                ? `${inspection.spf.lookupMechanisms}/10 lookups${inspection.spf.allQualifier ? ` · ${inspection.spf.allQualifier}all` : ''}`
                : null
            }
            issues={inspection.spf.issues}
          />
          {inspection.observed && inspection.comparison.length > 0 ? (
            <div className="lg:col-span-2">
              <div className="flex items-center gap-2">
                <PanelSectionTitle>Published vs observed</PanelSectionTitle>
                {mismatches.length === 0 ? (
                  <Badge variant="success">in sync</Badge>
                ) : (
                  <Badge variant="warning">
                    {mismatches.length} difference{mismatches.length === 1 ? '' : 's'}
                  </Badge>
                )}
                <span className="text-xs text-secondary">
                  observed by {inspection.observed.reportedBy} · {formatRelativeOrDate(inspection.observed.asOfUtc)}
                </span>
              </div>
              <div className="mt-2 flex flex-wrap gap-2">
                {inspection.comparison.map((row) => (
                  <div
                    key={row.field}
                    className={cn(
                      'flex items-center gap-2 rounded-md border px-3 py-1.5 font-mono text-xs',
                      row.match
                        ? 'border-border bg-surface-card text-secondary'
                        : 'border-[var(--status-warn-bg)] bg-[var(--status-warn-bg)] text-[var(--status-warn-fg)]',
                    )}
                    title={
                      row.match
                        ? undefined
                        : 'DNS differs from the last report — a recent change may still be propagating to reporters'
                    }
                  >
                    <span className="font-semibold">{row.field}=</span>
                    <span>{row.published ?? '—'}</span>
                    {!row.match ? (
                      <>
                        <Icon name="arrow-right" size={12} aria-hidden />
                        <span>observed {row.observed ?? '—'}</span>
                      </>
                    ) : null}
                  </div>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      ) : null}
    </Card>
  )
}

// --- Expandable per-source detail panel ---

type SourceDetailPanelProps = {
  domainId: string
  sourceIp: string
  days: AnalyticsDays
}

function SourceDetailPanel({ domainId, sourceIp, days }: SourceDetailPanelProps) {
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
      <div className="flex items-center gap-2 px-5 py-6 text-sm text-secondary">
        <Icon name="loader-circle" size={16} className="animate-spin" />
        Loading source detail…
      </div>
    )
  }

  if (error) {
    return (
      <div className="px-5 py-4">
        <p className="rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
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
        <p className="mt-1 text-xs text-secondary">
          {formatCompact(detail.compliantMessages)} of {formatCompact(detail.messages)} messages
          compliant ({formatPercent(detail.complianceRate)}) — a message is compliant when DKIM or
          SPF passes with alignment.
        </p>
        {evaluated.length === 0 ? (
          <p className="mt-2 text-sm text-secondary">No evaluation results reported.</p>
        ) : (
          <div className="mt-2 flex flex-wrap gap-2">
            {evaluated.map((combo) => (
              <EvaluatedChip key={`${combo.dkim}-${combo.spf}`} combo={combo} />
            ))}
          </div>
        )}
        <div className="mt-3 flex flex-wrap items-center gap-2 text-xs">
          <span className="font-medium text-secondary">Dispositions</span>
          <Badge variant="neutral">none · {formatCompact(detail.dispositions.none)}</Badge>
          <Badge variant="warning">quarantine · {formatCompact(detail.dispositions.quarantine)}</Badge>
          <Badge variant="danger">reject · {formatCompact(detail.dispositions.reject)}</Badge>
        </div>
      </section>

      {/* Raw auth results identify the actual sending service. */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section className="rounded-md border border-border bg-surface-card p-3">
          <PanelSectionTitle>Raw DKIM authentication</PanelSectionTitle>
          {detail.dkimAuth.length === 0 ? (
            <p className="mt-2 text-sm text-secondary">No DKIM signatures reported.</p>
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
                    <TableCell mono className="break-all">
                      {row.domain}
                    </TableCell>
                    <TableCell mono>{row.selector ?? '—'}</TableCell>
                    <TableCell>
                      <span className={cn('font-medium', resultTone(row.result))}>{row.result}</span>
                    </TableCell>
                    <TableCell align="right" className="tabular-nums">
                      {formatCompact(row.messages)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>

        <section className="rounded-md border border-border bg-surface-card p-3">
          <PanelSectionTitle>Raw SPF authentication</PanelSectionTitle>
          {detail.spfAuth.length === 0 ? (
            <p className="mt-2 text-sm text-secondary">No SPF checks reported.</p>
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
                    <TableCell mono className="break-all">
                      {row.domain}
                    </TableCell>
                    <TableCell className="text-xs text-secondary">{row.scope ?? '—'}</TableCell>
                    <TableCell>
                      <span className={cn('font-medium', resultTone(row.result))}>{row.result}</span>
                    </TableCell>
                    <TableCell align="right" className="tabular-nums">
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
        <section className="rounded-md border border-border bg-surface-card p-3">
          <PanelSectionTitle>Header from</PanelSectionTitle>
          <ValueList items={detail.headerFroms} emptyText="No header-from domains reported." />
        </section>
        <section className="rounded-md border border-border bg-surface-card p-3">
          <PanelSectionTitle>Envelope from</PanelSectionTitle>
          <ValueList items={detail.envelopeFroms} emptyText="No envelope-from domains reported." />
        </section>
        <section className="rounded-md border border-border bg-surface-card p-3">
          <PanelSectionTitle>Reporters</PanelSectionTitle>
          {detail.reporters.length === 0 ? (
            <p className="mt-2 text-sm text-secondary">No reporters in this window.</p>
          ) : (
            <ul className="mt-2 space-y-1.5">
              {detail.reporters.map((reporter) => (
                <li key={reporter.organizationName} className="flex items-baseline justify-between gap-3">
                  <span className="min-w-0 break-all text-sm text-body">{reporter.organizationName}</span>
                  <span className="whitespace-nowrap text-xs tabular-nums text-secondary">
                    {formatCompact(reporter.messages)} msgs · {formatCompact(reporter.reports)} rpts
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <section className="rounded-md border border-border bg-surface-card p-3">
        <PanelSectionTitle>Daily volume from {detail.sourceIp}</PanelSectionTitle>
        <TrendChart className="mt-2" data={trendData(detail.trend)} height={128} />
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
  const [guidance, setGuidance] = useState<EnforcementGuidance | null>(null)
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
      const [drilldownData, sourceData, guidanceData] = await Promise.all([
        fetchJson<DomainDrilldown>(`/api/v1/analytics/domains/${domainId}/drilldown?days=${days}`),
        fetchJson<DomainSourceAnalytics[]>(
          `/api/v1/analytics/domains/${domainId}/sources?days=${days}`,
        ),
        fetchJson<EnforcementGuidance>(
          `/api/v1/analytics/domains/${domainId}/enforcement?days=${days}`,
        ),
      ])
      if (seq !== requestSeq.current) return
      setDrilldown(drilldownData)
      setSources(sourceData)
      setGuidance(guidanceData)
      setNotFound(false)
    } catch (loadError) {
      if (seq !== requestSeq.current) return
      if (loadError instanceof ApiError && loadError.status === 404) {
        setNotFound(true)
      } else {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load domain analytics')
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
      <Card pad>
        <div className="flex flex-col items-center gap-3 py-16 text-center">
          <Icon name="search" size={40} className="text-faint" />
          <div>
            <p className="text-base font-semibold text-body">Domain not found</p>
            <p className="mt-1 max-w-md text-sm text-secondary">
              This domain does not exist or may have been removed.
            </p>
          </div>
          <Button asChild variant="secondary" size="sm">
            <Link to={backHref}>
              <Icon name="chevron-left" size={14} />
              Back to domains
            </Link>
          </Button>
        </div>
      </Card>
    )
  }

  const domain = drilldown?.domain
  const totals = drilldown?.totals
  const enforcement =
    totals && domain
      ? resolveEnforcementStatus(totals.messages, totals.complianceRate, domain.publishedPolicy)
      : null
  const enfMeta = enforcement ? ENFORCEMENT_STATUS_META[enforcement] : null
  const alignment = domain ? alignmentSummary(domain) : null

  const subtitleParts: string[] = []
  if (domain && domain.clientSlug !== 'default') subtitleParts.push(domain.clientName)
  if (drilldown) {
    subtitleParts.push(
      drilldown.window.anchoredToLatestData
        ? `data through ${formatFullDate(drilldown.window.endUtc)}`
        : `last ${drilldown.window.days} days`,
    )
  }

  return (
    <>
      <div className="mb-5">
        <Link
          to={backHref}
          className="inline-flex items-center gap-1.5 text-sm text-secondary transition-colors hover:text-brand"
        >
          <Icon name="chevron-left" size={14} />
          Domains
        </Link>
        <div className="mt-2.5 flex items-start justify-between gap-4">
          <div className="min-w-0">
            <h1 className="break-all font-mono text-xl font-semibold tracking-tight text-body">
              {domain?.name ?? 'Domain drill-down'}
            </h1>
            {subtitleParts.length > 0 ? (
              <p className="mt-1 text-sm text-secondary">{subtitleParts.join(' · ')}</p>
            ) : null}
          </div>
          <DaysSelector value={days} onChange={setDays} disabled={busy} />
        </div>
        {domain && enfMeta ? (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <PolicyBadge policy={domain.publishedPolicy ?? 'none'} />
            <Badge variant={enfMeta.badge}>{enfMeta.label}</Badge>
            {domain.clientSlug === 'default' ? (
              <Badge variant="warning">Default — needs client</Badge>
            ) : null}
            <Badge variant={domain.isActive ? 'success' : 'neutral'}>
              {domain.isActive ? 'Active' : 'Inactive'}
            </Badge>
            {alignment ? (
              <span className="font-mono text-xs text-secondary">{alignment}</span>
            ) : null}
          </div>
        ) : null}
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {!drilldown && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {drilldown && totals && domain ? (
        <div className={cn('space-y-3.5 transition-opacity', busy && 'opacity-60')}>
          <div className="grid grid-cols-2 gap-3.5 xl:grid-cols-4">
            <StatCard
              label="Compliance"
              value={totals.status === 'no_data' ? '—' : formatPercent(totals.complianceRate)}
            />
            <StatCard label={`Messages ${days}d`} value={formatCompact(totals.messages)} />
            <StatCard label="DKIM pass rate" value={formatPercent(totals.dkimPassRate)} />
            <StatCard label="SPF pass rate" value={formatPercent(totals.spfPassRate)} />
          </div>

          <div className="grid grid-cols-1 gap-3.5 sm:grid-cols-3">
            <StatCard label="Sending sources" value={totals.sources.toLocaleString('en-US')} />
            <StatCard label="Reporters" value={totals.reporters.toLocaleString('en-US')} />
            <StatCard
              label="Quarantined + rejected"
              value={formatCompact(totals.quarantined + totals.rejected)}
              extra={
                totals.quarantined + totals.rejected > 0 ? (
                  <Badge variant="danger">blocked</Badge>
                ) : undefined
              }
            />
          </div>

          <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-[1.6fr_1fr]">
            <Card pad>
              <CardHeader title="Message volume" description="Daily messages, compliant vs failed" />
              <TrendChart data={trendData(drilldown.trend)} height={170} />
            </Card>

            <Card pad>
              <CardHeader
                title="Path to enforcement"
                description="What stands between p=none and p=reject"
              />
              {guidance ? (
                <div
                  className={cn(
                    'mb-3.5 rounded-md border px-3 py-2.5',
                    guidance.readyToAdvance
                      ? 'border-[var(--status-ok-bg)] bg-[var(--status-ok-bg)]'
                      : 'border-[var(--status-warn-bg)] bg-[var(--status-warn-bg)]',
                  )}
                >
                  <div className="flex items-start gap-2">
                    <span
                      className="mt-px inline-flex"
                      style={{ color: guidance.readyToAdvance ? TONE_DOT.ok : TONE_DOT.warn }}
                    >
                      <Icon name={guidance.readyToAdvance ? 'circle-check' : 'triangle-alert'} size={16} />
                    </span>
                    <div className="min-w-0">
                      <div
                        className={cn(
                          'text-sm font-semibold',
                          guidance.readyToAdvance
                            ? 'text-[var(--status-ok-fg)]'
                            : 'text-[var(--status-warn-fg)]',
                        )}
                      >
                        {guidance.recommendedAction}
                      </div>
                      <p className="mt-0.5 text-xs leading-relaxed text-secondary">{guidance.rationale}</p>
                    </div>
                  </div>
                  {guidance.blockingSources.length > 0 ? (
                    <ul className="mt-2.5 space-y-1 border-t border-[color-mix(in_srgb,currentColor_12%,transparent)] pt-2">
                      {guidance.blockingSources.slice(0, 5).map((source) => (
                        <li key={source.sourceIp} className="flex items-baseline justify-between gap-3">
                          <button
                            type="button"
                            onClick={() => toggleSource(source.sourceIp)}
                            className="min-w-0 break-all text-left font-mono text-xs text-body underline decoration-dotted underline-offset-2 hover:text-brand"
                            title="Show this source in the table below"
                          >
                            {source.sourceIp}
                          </button>
                          <span className="whitespace-nowrap text-xs tabular-nums text-secondary">
                            {formatCompact(source.failedMessages)} failed
                          </span>
                        </li>
                      ))}
                      {guidance.blockingSourceCount > 5 ? (
                        <li className="text-xs text-secondary">
                          +{guidance.blockingSourceCount - 5} more below
                        </li>
                      ) : null}
                    </ul>
                  ) : null}
                </div>
              ) : null}
              <div className="flex flex-col gap-3">
                {buildEnforcementChecks(domain, totals).map((check) => (
                  <div key={check.title} className="flex items-start gap-2.5">
                    <span className="mt-px inline-flex" style={{ color: TONE_DOT[check.tone] }}>
                      <Icon name={TONE_ICON[check.tone]} size={16} />
                    </span>
                    <div className="min-w-0">
                      <div className="text-sm font-semibold text-body">{check.title}</div>
                      <div className="mt-0.5 font-mono text-xs text-secondary">{check.detail}</div>
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          </div>

          <RecordInspectionCard domainId={domainId} />

          {/* The centerpiece: per-source breakdown */}
          <Card>
            <div className="flex items-start justify-between gap-3 px-5 pt-5">
              <CardHeader
                title="Sending sources"
                description={`Per-IP DMARC results over the last ${days} days, worst offenders first — expand a row for the full authentication breakdown`}
              />
              <Badge variant="neutral">{sources.length} sources</Badge>
            </div>
            {sources.length === 0 ? (
              <p className="px-5 pb-6 pt-2 text-sm text-secondary">
                No sending sources reported in this window.
              </p>
            ) : (
              <div className="overflow-x-auto">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead aria-sort={sortKey === 'ip' ? ariaSort : undefined}>
                        <SortHeader label="Source IP" column="ip" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'messages' ? ariaSort : undefined}>
                        <SortHeader label="Messages" column="messages" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'failed' ? ariaSort : undefined}>
                        <SortHeader label="Failed" column="failed" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead aria-sort={sortKey === 'compliance' ? ariaSort : undefined}>
                        <SortHeader label="Compliance" column="compliance" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'dkim' ? ariaSort : undefined}>
                        <SortHeader label="DKIM" column="dkim" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'spf' ? ariaSort : undefined}>
                        <SortHeader label="SPF" column="spf" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'quarantined' ? ariaSort : undefined}>
                        <SortHeader label="Quarantined" column="quarantined" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'rejected' ? ariaSort : undefined}>
                        <SortHeader label="Rejected" column="rejected" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead className="text-right" aria-sort={sortKey === 'reporters' ? ariaSort : undefined}>
                        <SortHeader label="Reporters" column="reporters" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead aria-sort={sortKey === 'lastSeen' ? ariaSort : undefined}>
                        <SortHeader label="Last seen" column="lastSeen" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {sortedSources.map((source) => {
                      const expanded = selectedSource === source.sourceIp
                      return (
                        <Fragment key={source.sourceIp}>
                          <TableRow
                            className={cn(expanded && 'bg-gray-50')}
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
                                className="inline-flex items-center gap-1.5 rounded-xs font-mono text-sm font-medium text-body transition-colors hover:text-brand focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none"
                              >
                                <Icon
                                  name="chevron-right"
                                  size={14}
                                  className={cn('shrink-0 text-secondary transition-transform', expanded && 'rotate-90')}
                                />
                                {source.sourceIp}
                              </button>
                              {hostnames[source.sourceIp] ? (
                                <div className="mt-0.5 pl-5 text-xs text-secondary">
                                  {hostnames[source.sourceIp]}
                                </div>
                              ) : null}
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {formatCompact(source.messages)}
                            </TableCell>
                            <TableCell
                              align="right"
                              className={cn(
                                'tabular-nums',
                                source.failedMessages > 0 && 'font-medium text-[var(--status-danger-fg)]',
                              )}
                            >
                              {formatCompact(source.failedMessages)}
                            </TableCell>
                            <TableCell>
                              <ComplianceBar value={+(source.complianceRate * 100).toFixed(1)} width={110} />
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {formatPercent(source.dkimPassRate)}
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {formatPercent(source.spfPassRate)}
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {formatCompact(source.quarantined)}
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {formatCompact(source.rejected)}
                            </TableCell>
                            <TableCell align="right" className="tabular-nums">
                              {source.reporters.toLocaleString('en-US')}
                            </TableCell>
                            <TableCell className="whitespace-nowrap text-secondary">
                              {formatRelativeOrDate(source.lastSeenUtc)}
                            </TableCell>
                          </TableRow>
                          {expanded ? (
                            <TableRow className="hover:bg-transparent">
                              <TableCell colSpan={SOURCE_COLUMN_COUNT} className="bg-gray-50 p-0">
                                <SourceDetailPanel
                                  domainId={domainId}
                                  sourceIp={source.sourceIp}
                                  days={days}
                                />
                              </TableCell>
                            </TableRow>
                          ) : null}
                        </Fragment>
                      )
                    })}
                  </TableBody>
                </Table>
              </div>
            )}
          </Card>
        </div>
      ) : null}
    </>
  )
}
