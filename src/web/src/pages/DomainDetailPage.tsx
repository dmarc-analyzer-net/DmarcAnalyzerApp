import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'

import { ComplianceBar } from '@/components/data/ComplianceBar'
import { PolicyBadge } from '@/components/data/PolicyBadge'
import { SortHeader, type SortDir } from '@/components/data/SortHeader'
import { StatCard } from '@/components/data/StatCard'
import { TrendChart } from '@/components/data/TrendChart'
import { DaysSelector } from '@/components/DaysSelector'
import { Notice } from '@/components/Notice'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Icon, type IconName } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
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
  type MtaStsReadiness,
  type MtaStsState,
  type RecordComparison,
  type RecordInspection,
  type SourceDetail,
  type TlsRptSummary,
  type ValueCount,
} from '@/lib/analytics'
import { ApiError, fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin, isStaff } from '@/lib/authz'
import type {
  Domain,
  MtaStsPolicyApplyOutcome,
  MtaStsPolicyBulkApplyResponse,
  MtaStsPolicyMode,
  MtaStsPolicyResponse,
} from '@/lib/entities'
import { formatCompact, formatFullDate, formatPercent, formatRelativeOrDate, formatShortDate } from '@/lib/format'
import { usePageTitle } from '@/lib/use-page-title'
import { cn } from '@/lib/utils'

// --- Sources table sorting (same interaction pattern as DomainsPage) ---

type SourceSortKey =
  | 'ip'
  | 'messages'
  | 'failed'
  | 'compliance'
  | 'dkim'
  | 'spf'
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
  // Null when DNS publishes no usable DMARC record. Don't collapse it to 'none' —
  // that reads as "this domain publishes p=none", which is a different claim.
  const policy = domain.publishedPolicy
  const atQuarantine = policy === 'quarantine' || policy === 'reject'
  // Set when the policy is the organisational domain's, because this domain publishes no
  // record. Saying "DNS publishes p=reject" here would name the wrong domain.
  const inheritedFrom = domain.policyInheritedFrom
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
      title: policy === null
        ? 'No DMARC record published'
        : atQuarantine
          ? 'Policy at quarantine or stronger'
          : 'Policy still monitoring',
      detail: policy === null
        ? 'No DMARC record found for this domain or any parent domain'
        : inheritedFrom
          ? `Inherits p=${policy} from ${inheritedFrom} — this domain publishes no record of its own`
          : `DNS publishes p=${policy}`,
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
  RecordInspection['dmarc']['status'],
  { label: string; badge: 'success' | 'danger' | 'warning' }
> = {
  found: { label: 'Published', badge: 'success' },
  missing: { label: 'Missing', badge: 'danger' },
  lookup_failed: { label: 'Lookup failed', badge: 'warning' },
  // DMARC only — no record of its own, but an ancestor's policy applies (the tree
  // walk in DmarcPolicyResolver). Distinct from 'missing': the domain isn't
  // unprotected, so this isn't a danger badge.
  inherited: { label: 'Inherited', badge: 'warning' },
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
  const requestSeq = useRef(0)

  // Loader idiom matches the other panels in this file: state is set inside a
  // callback (not synchronously in the effect body), and a request sequence
  // guards against out-of-order responses when domainId changes.
  const loadInspection = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const payload = await fetchJson<RecordInspection>(
        `/api/v1/analytics/domains/${domainId}/records`,
      )
      if (seq === requestSeq.current) setInspection(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to inspect records')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [domainId])

  useEffect(() => {
    void loadInspection()
  }, [loadInspection])

  // Only 'differs' counts. A tag DNS never published has nothing to disagree with,
  // and a tag the reporter omitted says nothing about the record either.
  const mismatches = inspection?.comparison.filter((c) => c.status === 'differs') ?? []
  // inherited / not_reported carry an explanation; render it as visible text.
  const annotations = inspection?.comparison.filter((c) => c.note) ?? []

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
          {inspection.externalDestinations.length > 0 ? (
            <div className="lg:col-span-2">
              <PanelSectionTitle>External report destinations</PanelSectionTitle>
              <ul className="mt-2 space-y-1.5">
                {inspection.externalDestinations.map((dest) => (
                  <li
                    key={dest.destination}
                    className={cn(
                      'flex items-start gap-1.5 text-xs leading-relaxed',
                      dest.status === 'not_authorized'
                        ? 'text-[var(--status-danger-fg)]'
                        : dest.status === 'lookup_failed'
                          ? 'text-[var(--status-warn-fg)]'
                          : 'text-secondary',
                    )}
                  >
                    <Icon
                      name={dest.status === 'authorized' ? 'circle-check' : 'triangle-alert'}
                      size={13}
                      className="mt-px shrink-0"
                    />
                    <span>
                      <span className="font-mono font-semibold">{dest.destination}</span>: {dest.detail}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
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
                {inspection.comparison.map((row) => {
                  const differs = row.status === 'differs'
                  return (
                    <div
                      key={row.field}
                      className={cn(
                        'flex items-center gap-2 rounded-md border px-3 py-1.5 font-mono text-xs',
                        differs
                          ? 'border-[var(--status-warn-bg)] bg-[var(--status-warn-bg)] text-[var(--status-warn-fg)]'
                          : 'border-border bg-surface-card text-secondary',
                      )}
                      aria-label={describeComparison(row)}
                    >
                      <span className="font-semibold">{row.field}=</span>
                      {row.status === 'inherited' ? (
                        <span className="font-sans italic opacity-70">inherits p</span>
                      ) : (
                        <>
                          <span>{row.published ?? '—'}</span>
                          {differs ? (
                            <>
                              <Icon name="arrow-right" size={12} aria-hidden />
                              <span>observed {row.observed ?? '—'}</span>
                            </>
                          ) : null}
                          {row.status === 'not_reported' ? (
                            <span className="font-sans italic opacity-70">not reported</span>
                          ) : null}
                        </>
                      )}
                    </div>
                  )
                })}
              </div>
              {/* Visible prose, not a title= tooltip: these states explain an
                  *absence*, and a hover-only explanation is unreachable by
                  keyboard and touch users — and undiscoverable for everyone,
                  since no other chip is hoverable. */}
              {annotations.length > 0 ? (
                <ul className="mt-2 space-y-1">
                  {annotations.map((row) => (
                    <li key={row.field} className="text-xs leading-relaxed text-secondary">
                      <span className="font-mono font-semibold">{row.field}</span>: {row.note}
                    </li>
                  ))}
                </ul>
              ) : null}
              {mismatches.length > 0 ? (
                <p className="mt-2 text-xs leading-relaxed text-secondary">
                  DNS and the last report disagree — the record may have changed since that report.
                </p>
              ) : null}
            </div>
          ) : null}
        </div>
      ) : null}
    </Card>
  )
}

/** Full sentence for assistive tech: the chips are terse by design. */
function describeComparison(row: RecordComparison): string {
  switch (row.status) {
    case 'inherited':
      return `${row.field}: ${row.note ?? 'not published'}`
    case 'not_reported':
      return `${row.field} published as ${row.published}, but ${row.note ?? 'not reported'}`
    case 'differs':
      return `${row.field}: DNS publishes ${row.published ?? 'nothing'}, reporters observed ${row.observed ?? 'nothing'}`
    default:
      return `${row.field}: ${row.published ?? 'nothing'}, matching reports`
  }
}

// --- Transport security (MTA-STS) ---

const MTA_STS_RECORD_META: Record<
  Exclude<MtaStsState['dnsRecordStatus'], null>,
  { label: string; badge: 'success' | 'danger' | 'warning' | 'neutral' }
> = {
  found: { label: 'Published', badge: 'success' },
  // Deliberately neutral, not danger: publishing MTA-STS is optional, and most
  // domains don't. The card renders this state quietly.
  missing: { label: 'Not configured', badge: 'neutral' },
  lookup_failed: { label: 'Lookup failed', badge: 'warning' },
  // Two or more STSv1 records, or one senders can't parse — RFC 8461 makes
  // both read as "no available policy", so this is worse than it sounds.
  invalid: { label: 'Invalid', badge: 'danger' },
}

const MTA_STS_MODE_META: Record<Exclude<MtaStsState['mode'], null>, { badge: 'success' | 'warning' | 'neutral' }> = {
  enforce: { badge: 'success' },
  testing: { badge: 'warning' },
  none: { badge: 'neutral' },
}

const MTA_STS_FETCH_LABEL: Record<Exclude<MtaStsState['fetchStatus'], null | 'ok'>, string> = {
  redirected: 'Redirected',
  http_error: 'HTTP error',
  tls_failed: 'TLS failed',
  connect_failed: 'Unreachable',
  timeout: 'Timed out',
  too_large: 'Too large',
}

/** Seconds as the round unit an operator would say: 86400 → "1 day". */
function formatMaxAge(seconds: number): string {
  if (seconds % 604800 === 0 && seconds >= 604800) {
    const weeks = seconds / 604800
    return `${weeks} week${weeks === 1 ? '' : 's'}`
  }
  if (seconds % 86400 === 0 && seconds >= 86400) {
    const days = seconds / 86400
    return `${days} day${days === 1 ? '' : 's'}`
  }
  return `${seconds}s`
}

/**
 * The domain's MTA-STS posture, from the state the worker pass persists — a
 * plain database read, so unlike the record inspection card nothing here waits
 * on live DNS or an HTTPS fetch. Recheck (staff only) runs those on demand.
 */
function TransportSecurityCard({ domainId, days }: { domainId: string; days: AnalyticsDays }) {
  const { user } = useAuth()
  const staff = isStaff(user)
  const admin = isAdmin(user)
  const [state, setState] = useState<MtaStsState | null>(null)
  const [policyResponse, setPolicyResponse] = useState<MtaStsPolicyResponse | null>(null)
  const [tlsSummary, setTlsSummary] = useState<TlsRptSummary | null>(null)
  const [busy, setBusy] = useState(true)
  const [rechecking, setRechecking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const requestSeq = useRef(0)

  const load = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const [statePayload, policyPayload, tlsPayload] = await Promise.all([
        fetchJson<MtaStsState>(`/api/v1/analytics/domains/${domainId}/mta-sts`),
        fetchJson<MtaStsPolicyResponse>(`/api/v1/domains/${domainId}/mta-sts-policy`),
        fetchJson<TlsRptSummary>(`/api/v1/analytics/domains/${domainId}/tls-rpt?days=${days}`),
      ])
      if (seq === requestSeq.current) {
        setState(statePayload)
        setPolicyResponse(policyPayload)
        setTlsSummary(tlsPayload)
      }
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load MTA-STS state')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [domainId, days])

  useEffect(() => {
    void load()
  }, [load])

  const recheck = useCallback(async () => {
    const seq = ++requestSeq.current
    setRechecking(true)
    setError(null)
    try {
      const payload = await fetchJson<MtaStsState>(
        `/api/v1/analytics/domains/${domainId}/mta-sts/recheck`,
        { method: 'POST' },
      )
      if (seq === requestSeq.current) setState(payload)
    } catch (recheckError) {
      if (seq === requestSeq.current) {
        setError(recheckError instanceof Error ? recheckError.message : 'Recheck failed')
      }
    } finally {
      if (seq === requestSeq.current) setRechecking(false)
    }
  }, [domainId])

  const status = state?.dnsRecordStatus
  const statusMeta = status ? MTA_STS_RECORD_META[status] : null
  const fetchFailed = state?.fetchStatus != null && state.fetchStatus !== 'ok'

  return (
    <Card pad>
      <div className="flex items-start justify-between gap-3">
        <CardHeader
          title="Transport security (MTA-STS)"
          description="Whether senders are told to require verified TLS when delivering to this domain — the _mta-sts record, the policy file, and its MX coverage"
        />
        {staff && state?.checked !== undefined ? (
          <Button variant="outline" size="sm" onClick={() => void recheck()} disabled={rechecking || busy}>
            {rechecking ? (
              <Icon name="loader-circle" size={14} className="animate-spin" />
            ) : (
              <Icon name="refresh-cw" size={14} />
            )}
            Recheck now
          </Button>
        ) : null}
      </div>
      {busy ? (
        <div className="flex items-center gap-2 py-4 text-sm text-secondary">
          <Icon name="loader-circle" size={16} className="animate-spin" />
          Loading MTA-STS state…
        </div>
      ) : error ? (
        <p className="rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </p>
      ) : !state ? null : !state.checked ? (
        <p className="py-2 text-sm text-secondary">
          Not checked yet — the worker&apos;s MTA-STS pass hasn&apos;t reached this domain.
        </p>
      ) : status === 'missing' ? (
        <div className="flex items-center gap-2 py-2">
          <Badge variant="neutral">Not configured</Badge>
          <p className="text-sm text-secondary">
            This domain doesn&apos;t publish an MTA-STS policy. Optional — it tells senders to require
            verified TLS when delivering here.
          </p>
        </div>
      ) : (
        <div className="space-y-5">
          <div>
            <div className="flex flex-wrap items-center gap-2">
              <PanelSectionTitle>MTA-STS record</PanelSectionTitle>
              {statusMeta ? <Badge variant={statusMeta.badge}>{statusMeta.label}</Badge> : null}
              {state.policyId ? (
                <span className="font-mono text-xs text-secondary">id {state.policyId}</span>
              ) : null}
              {status === 'lookup_failed' && state.lastChangedAtUtc ? (
                <span className="text-xs text-secondary">
                  showing last known state · verified {formatRelativeOrDate(state.lastChangedAtUtc)}
                </span>
              ) : null}
            </div>
            {state.rawRecord ? (
              <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-all rounded-md border border-border bg-surface-sunken px-3 py-2 font-mono text-xs leading-relaxed text-body">
                {state.rawRecord}
              </pre>
            ) : null}
          </div>

          {state.fetchStatus ? (
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <PanelSectionTitle>Policy file</PanelSectionTitle>
                {state.fetchStatus === 'ok' ? (
                  state.policyValid === false ? (
                    <Badge variant="danger">Invalid</Badge>
                  ) : (
                    <Badge variant="success">Fetched</Badge>
                  )
                ) : (
                  <Badge variant="danger">{MTA_STS_FETCH_LABEL[state.fetchStatus]}</Badge>
                )}
                {state.mode ? (
                  <Badge variant={MTA_STS_MODE_META[state.mode].badge}>mode: {state.mode}</Badge>
                ) : null}
                {state.maxAgeSeconds != null ? (
                  <span className="font-mono text-xs text-secondary">
                    max_age {formatMaxAge(state.maxAgeSeconds)}
                  </span>
                ) : null}
                {fetchFailed && state.lastFetchOkAtUtc ? (
                  <span className="text-xs text-secondary">
                    last fetched ok {formatRelativeOrDate(state.lastFetchOkAtUtc)}
                  </span>
                ) : null}
              </div>
              {state.policyBody ? (
                <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-all rounded-md border border-border bg-surface-sunken px-3 py-2 font-mono text-xs leading-relaxed text-body">
                  {state.policyBody}
                </pre>
              ) : null}
            </div>
          ) : null}

          {state.mxHosts.length > 0 ? (
            <div>
              <PanelSectionTitle>MX coverage</PanelSectionTitle>
              <ul className="mt-2 space-y-1.5">
                {state.mxHosts.map((mx) => (
                  <li
                    key={`${mx.preference}-${mx.host}`}
                    className={cn(
                      'flex items-start gap-1.5 text-xs leading-relaxed',
                      mx.matched === false ? 'text-[var(--status-danger-fg)]' : 'text-secondary',
                    )}
                  >
                    <Icon
                      name={mx.matched === false ? 'triangle-alert' : mx.matched === true ? 'circle-check' : 'info'}
                      size={13}
                      className="mt-px shrink-0"
                    />
                    <span>
                      <span className="font-mono font-semibold">{mx.host}</span>
                      <span className="font-mono"> · {mx.preference}</span>
                      {mx.matched === false ? ' — not covered by any mx pattern' : null}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          ) : state.mxLookupStatus === 'lookup_failed' ? (
            <p className="text-xs text-[var(--status-warn-fg)]">
              MX lookup failed — the policy&apos;s mx patterns could not be cross-checked.
            </p>
          ) : null}

          {state.issues.length > 0 ? (
            <ul className="space-y-1">
              {state.issues.map((issue) => (
                <li
                  key={issue}
                  className="flex items-start gap-1.5 text-xs leading-relaxed text-[var(--status-warn-fg)]"
                >
                  <Icon name="triangle-alert" size={13} className="mt-px shrink-0" />
                  {issue}
                </li>
              ))}
            </ul>
          ) : null}

          <p className="text-xs text-secondary">
            {state.lastCheckedAtUtc ? `Checked ${formatRelativeOrDate(state.lastCheckedAtUtc)}` : null}
            {state.previousPolicyId && state.policyIdChangedAtUtc
              ? ` · id changed ${formatRelativeOrDate(state.policyIdChangedAtUtc)} (${state.previousPolicyId} → ${state.policyId ?? '—'})`
              : null}
          </p>
        </div>
      )}
      {!busy && !error && policyResponse ? (
        <HostedPolicySection
          response={policyResponse}
          monitoring={state}
          readiness={state?.readiness ?? null}
          admin={admin}
          onChanged={() => void load()}
        />
      ) : null}
      {!busy && !error && tlsSummary ? <TlsRptSection summary={tlsSummary} /> : null}
    </Card>
  )
}

const MAX_AGE_PRESETS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '86400', label: '1 day' },
  { value: '604800', label: '1 week' },
  { value: '1209600', label: '2 weeks' },
  { value: '2592000', label: '30 days' },
  { value: 'custom', label: 'Custom (seconds)' },
]

function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      aria-label={`Copy ${label}`}
      onClick={() => {
        void navigator.clipboard?.writeText(value).then(() => {
          setCopied(true)
          window.setTimeout(() => setCopied(false), 1500)
        })
      }}
    >
      <Icon name={copied ? 'circle-check' : 'copy'} size={13} />
    </Button>
  )
}

/** A labeled DNS record with its value in mono and a copy affordance. */
function PublishRow({ label, name, value }: { label: string; name: string; value: string }) {
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
      <span className="w-16 shrink-0 text-secondary">{label}</span>
      <span className="font-mono font-semibold text-body">{name}</span>
      <Icon name="arrow-right" size={12} className="shrink-0 text-secondary" aria-hidden />
      <span className="break-all font-mono text-body">{value}</span>
      <CopyButton value={value} label={label} />
    </div>
  )
}

/**
 * The hosted-policy half of the card: this instance serving the policy file for
 * the domain. Deliberately renders in every monitoring state — a domain with no
 * MTA-STS record yet is exactly the one worth hosting a policy for.
 */
function HostedPolicySection({
  response,
  monitoring,
  readiness,
  admin,
  onChanged,
}: {
  response: MtaStsPolicyResponse
  monitoring: MtaStsState | null
  readiness: MtaStsReadiness | null
  admin: boolean
  onChanged: () => void
}) {
  const [editorOpen, setEditorOpen] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [promoting, setPromoting] = useState(false)
  const policy = response.policy

  const promote = async () => {
    if (!policy) return
    if (!window.confirm(
      `Move ${response.domainName} to enforce? Conforming senders will refuse delivery via any MX ` +
      'the policy does not cover, or when TLS fails. The record id changes — the TXT record needs updating.',
    )) {
      return
    }

    setPromoting(true)
    setDeleteError(null)
    try {
      await fetchJson<MtaStsPolicyResponse>(`/api/v1/domains/${response.domainId}/mta-sts-policy`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          enabled: true,
          mode: 'enforce',
          maxAgeSeconds: policy.maxAgeSeconds,
          mxPatterns: policy.mxPatterns,
        }),
      })
      setNotice('Promoted to enforce — the id changed; update the TXT record above.')
      onChanged()
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : 'Failed to promote the policy')
    } finally {
      setPromoting(false)
    }
  }

  // Hosted and enabled, but the checker has never once fetched it: that is a
  // setup window (CNAME or proxy not wired yet), rendered as guidance rather
  // than failure. The alert evaluator suppresses mta_sts_broken on the same rule.
  const waitingForDns =
    policy?.enabled === true && monitoring?.checked === true && monitoring.lastFetchOkAtUtc === null

  const removePolicy = async () => {
    if (!window.confirm(
      `Stop hosting the MTA-STS policy for ${response.domainName}? The client's mta-sts CNAME ` +
      'and _mta-sts TXT records should be removed too, or senders will see a broken policy host.',
    )) {
      return
    }

    setDeleteError(null)
    try {
      await fetchJson<void>(`/api/v1/domains/${response.domainId}/mta-sts-policy`, { method: 'DELETE' })
      setNotice(null)
      onChanged()
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : 'Failed to delete the policy')
    }
  }

  return (
    <div className="mt-5 border-t border-border pt-4">
      <div className="flex flex-wrap items-center gap-2">
        <PanelSectionTitle>Hosted policy</PanelSectionTitle>
        {policy ? (
          policy.enabled ? (
            <Badge variant={MTA_STS_MODE_META[policy.mode].badge}>mode: {policy.mode}</Badge>
          ) : (
            <Badge variant="neutral">Hosting off</Badge>
          )
        ) : null}
        {policy ? (
          <span className="font-mono text-xs text-secondary">
            id {policy.policyId} · max_age {formatMaxAge(policy.maxAgeSeconds)}
          </span>
        ) : null}
        {admin ? (
          <span className="ml-auto flex gap-1.5">
            {policy ? (
              <>
                <Button variant="outline" size="sm" onClick={() => setEditorOpen(true)}>
                  <Icon name="pencil" size={13} />
                  Edit
                </Button>
                <Button variant="ghost" size="sm" onClick={() => void removePolicy()}>
                  <Icon name="trash-2" size={13} />
                </Button>
              </>
            ) : (
              <Button variant="outline" size="sm" onClick={() => setEditorOpen(true)}>
                <Icon name="plus" size={13} />
                Host MTA-STS policy
              </Button>
            )}
          </span>
        ) : null}
      </div>

      {!policy ? (
        <p className="mt-2 text-xs leading-relaxed text-secondary">
          Not hosted here. This instance can serve the policy file for {response.domainName} — onboarding
          is one CNAME plus one TXT record, no per-domain web hosting.
        </p>
      ) : (
        <div className="mt-3 space-y-3">
          {readiness && readiness.status !== 'not_applicable' ? (
            <Notice
              tone={readiness.status === 'ready' ? 'ok' : readiness.status === 'not_ready' ? 'danger' : 'warn'}
            >
              <span className="flex flex-wrap items-center gap-2">
                {readiness.status === 'ready' ? (
                  <>
                    Ready to enforce
                    {readiness.evidenceBasis === 'tls_rpt'
                      ? ` — reporters saw no STS failures across ${readiness.totalSessions.toLocaleString()} sessions in the last ${readiness.gateWindowDays} days.`
                      : ` — no TLS reporter covers this domain; based on ${readiness.daysInTesting} clean days in testing.`}
                    {admin ? (
                      <Button size="sm" onClick={() => void promote()} disabled={promoting}>
                        Promote to enforce
                      </Button>
                    ) : null}
                  </>
                ) : (
                  (readiness.blockedReason ?? 'Not ready to enforce yet.')
                )}
              </span>
            </Notice>
          ) : null}
          {notice ? <Notice tone="warn">{notice}</Notice> : null}
          {deleteError ? <Notice tone="danger">{deleteError}</Notice> : null}
          {waitingForDns ? (
            <Notice tone="warn">
              Waiting for DNS — the policy file has never been fetched from the public endpoint yet.
              Create the records below (and make sure the reverse proxy routes {response.cnameRecordName}),
              then use Recheck now.
            </Notice>
          ) : null}
          {!policy.enabled ? (
            <p className="text-xs text-secondary">
              Hosting is off — the settings are kept, but the public endpoint answers 404 for this domain.
            </p>
          ) : null}
          <div>
            <div className="text-xs font-semibold text-body">Records to publish on {response.domainName}</div>
            <div className="mt-1.5 space-y-1.5">
              <PublishRow
                label="CNAME"
                name={response.cnameRecordName}
                value={response.cnameTarget ?? 'set MtaSts__PolicyHost to show the target'}
              />
              <PublishRow label="TXT" name={policy.txtRecordName} value={policy.txtRecordValue} />
            </div>
            <p className="mt-1.5 text-xs text-secondary">
              Served at{' '}
              <a className="font-mono underline" href={policy.policyUrl} target="_blank" rel="noreferrer">
                {policy.policyUrl}
              </a>
            </p>
          </div>
          {policy.mxPatterns.length > 0 ? (
            <div className="text-xs text-secondary">
              mx patterns:{' '}
              <span className="font-mono text-body">{policy.mxPatterns.join(', ')}</span>
            </div>
          ) : null}
        </div>
      )}

      {editorOpen ? (
        <MtaStsPolicyEditor
          response={response}
          onClose={() => setEditorOpen(false)}
          onSaved={(idChanged) => {
            setEditorOpen(false)
            setNotice(idChanged
              ? 'Policy content changed, so the id moved — update the TXT record above or senders keep the old policy until max_age expires.'
              : null)
            onChanged()
          }}
        />
      ) : null}
    </div>
  )
}

const TLS_CATEGORY_BADGE: Record<string, 'danger' | 'warning' | 'neutral'> = {
  sts: 'danger',       // this policy breaking delivery — the gate's blocker
  dane: 'warning',
  transport: 'warning', // a receiving MX misconfigured — not this policy's fault
  other: 'neutral',
}

/**
 * Encryption in transit, as reporters saw it. Empty is the norm — TLS-RPT has
 * far fewer reporters than DMARC — so no data renders quietly, not as an error.
 */
function TlsRptSection({ summary }: { summary: TlsRptSummary }) {
  if (summary.totalSessions === 0) {
    return (
      <div className="mt-5 border-t border-border pt-4">
        <PanelSectionTitle>Encryption in transit (TLS-RPT)</PanelSectionTitle>
        <p className="mt-2 text-xs leading-relaxed text-secondary">
          No TLS reports received for this domain in this window. Reporters are opt-in on the
          sender side (a `_smtp._tls` record invites them), and most domains never attract any.
        </p>
      </div>
    )
  }

  return (
    <div className="mt-5 border-t border-border pt-4">
      <div className="flex flex-wrap items-center gap-2">
        <PanelSectionTitle>Encryption in transit (TLS-RPT)</PanelSectionTitle>
        <Badge variant={summary.failedSessions === 0 ? 'success' : 'warning'}>
          {formatPercent(summary.successRate)} encrypted
        </Badge>
        <span className="text-xs text-secondary">
          {formatCompact(summary.totalSessions)} sessions · {summary.reporterCount} reporter
          {summary.reporterCount === 1 ? '' : 's'} · {formatShortDate(summary.window.beginUtc)} –{' '}
          {formatShortDate(summary.window.endUtc)}
        </span>
      </div>

      {summary.failuresByType.length > 0 ? (
        <div className="mt-3">
          <div className="text-xs font-semibold text-body">
            {formatCompact(summary.failedSessions)} failed session
            {summary.failedSessions === 1 ? '' : 's'}
          </div>
          <ul className="mt-1.5 space-y-1">
            {summary.failuresByType.map((failure) => (
              <li key={failure.resultType} className="flex flex-wrap items-center gap-2 text-xs">
                <Badge variant={TLS_CATEGORY_BADGE[failure.category] ?? 'neutral'}>
                  {failure.category}
                </Badge>
                <span className="font-mono text-body">{failure.resultType}</span>
                <span className="text-secondary">
                  {formatCompact(failure.failedSessions)} session{failure.failedSessions === 1 ? '' : 's'}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {summary.byReceivingMx.length > 0 ? (
        <div className="mt-3">
          <div className="text-xs font-semibold text-body">Failures by receiving MX</div>
          <ul className="mt-1.5 space-y-1">
            {summary.byReceivingMx.map((mx) => (
              <li key={mx.receivingMxHostname} className="flex flex-wrap items-center gap-2 text-xs">
                <span className="font-mono text-body">{mx.receivingMxHostname}</span>
                <span className="text-secondary">
                  {formatCompact(mx.failedSessions)} · {mx.resultTypes.join(', ')}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  )
}

/**
 * Create/edit dialog, with the bulk expander for same-provider fleets: apply
 * the same shape to sibling domains in one save. Only domains whose rendered
 * policy actually changes get a new id, and the results view lists exactly the
 * TXT records that now need updating.
 */
function MtaStsPolicyEditor({
  response,
  onClose,
  onSaved,
}: {
  response: MtaStsPolicyResponse
  onClose: () => void
  onSaved: (idChanged: boolean) => void
}) {
  const existing = response.policy
  const presetValue = existing && MAX_AGE_PRESETS.some((p) => p.value === String(existing.maxAgeSeconds))
    ? String(existing.maxAgeSeconds)
    : existing
      ? 'custom'
      : '604800'

  const [enabled, setEnabled] = useState(existing?.enabled ?? true)
  const [mode, setMode] = useState<MtaStsPolicyMode>(existing?.mode ?? 'testing')
  const [maxAgePreset, setMaxAgePreset] = useState(presetValue)
  const [maxAgeCustom, setMaxAgeCustom] = useState(String(existing?.maxAgeSeconds ?? 604800))
  const [patternsText, setPatternsText] = useState(existing?.mxPatterns.join('\n') ?? '')
  const [bulkOpen, setBulkOpen] = useState(false)
  const [siblings, setSiblings] = useState<Domain[] | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [bulkResults, setBulkResults] = useState<MtaStsPolicyApplyOutcome[] | null>(null)

  const toggleBulk = async (open: boolean) => {
    setBulkOpen(open)
    if (open && siblings === null) {
      try {
        const domains = await fetchJson<Domain[]>(`/api/v1/domains?clientId=${response.clientId}`)
        setSiblings(domains.filter((d) => d.isActive && d.id !== response.domainId))
      } catch {
        setSiblings([])
      }
    }
  }

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    setSaving(true)
    const body = {
      enabled,
      mode,
      maxAgeSeconds: maxAgePreset === 'custom' ? Number(maxAgeCustom) : Number(maxAgePreset),
      mxPatterns: patternsText.split('\n').map((line) => line.trim()).filter(Boolean),
    }

    try {
      if (selected.size > 0) {
        const bulk = await fetchJson<MtaStsPolicyBulkApplyResponse>(
          `/api/v1/clients/${response.clientId}/mta-sts-policy/apply`,
          {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ...body, domainIds: [response.domainId, ...selected] }),
          },
        )
        setBulkResults(bulk.results)
      } else {
        const updated = await fetchJson<MtaStsPolicyResponse>(
          `/api/v1/domains/${response.domainId}/mta-sts-policy`,
          { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) },
        )
        onSaved(existing !== null && updated.policy !== null && updated.policy.policyId !== existing.policyId)
      }
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save the policy')
    } finally {
      setSaving(false)
    }
  }

  const thisDomainResult = bulkResults?.find((r) => r.domainId === response.domainId)

  return (
    <Dialog open onOpenChange={(open) => (!open ? onClose() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {existing ? `Edit hosted policy — ${response.domainName}` : `Host MTA-STS policy — ${response.domainName}`}
          </DialogTitle>
          <DialogDescription>
            Served at the well-known URL for senders that honor MTA-STS. Start in testing; move to
            enforce once TLS-RPT (or time) shows it clean.
          </DialogDescription>
        </DialogHeader>
        {bulkResults ? (
          <div className="space-y-3">
            <Notice tone="ok">
              Applied to {bulkResults.length} domain{bulkResults.length === 1 ? '' : 's'}. Each changed
              domain has its own TXT record to publish or update:
            </Notice>
            <ul className="max-h-64 space-y-1.5 overflow-y-auto">
              {bulkResults.map((result) => (
                <li key={result.domainId} className="flex flex-wrap items-center gap-2 text-xs">
                  <span className="font-mono font-semibold">{result.domainName}</span>
                  <Badge variant={result.outcome === 'unchanged' ? 'neutral' : 'success'}>
                    {result.outcome}
                  </Badge>
                  {result.outcome !== 'unchanged' ? (
                    <>
                      <span className="break-all font-mono text-secondary">
                        {result.txtRecordName} → {result.txtRecordValue}
                      </span>
                      <CopyButton value={result.txtRecordValue} label={`TXT for ${result.domainName}`} />
                    </>
                  ) : null}
                </li>
              ))}
            </ul>
            <div className="flex justify-end">
              <Button
                type="button"
                onClick={() => onSaved(thisDomainResult !== undefined && thisDomainResult.outcome !== 'unchanged' && existing !== null)}
              >
                Done
              </Button>
            </div>
          </div>
        ) : (
          <form className="grid gap-3" onSubmit={save}>
            <label className="grid gap-1 text-xs text-secondary">
              Mode
              <Select value={mode} onChange={(e) => setMode(e.target.value as MtaStsPolicyMode)}>
                <option value="testing">Testing — receivers report failures but still deliver</option>
                <option value="enforce">Enforce — senders refuse delivery when MX or TLS does not match</option>
                <option value="none">None — publish an explicit opt-out</option>
              </Select>
            </label>
            <label className="grid gap-1 text-xs text-secondary">
              Max age
              <span className="flex gap-2">
                <Select
                  className="flex-1"
                  value={maxAgePreset}
                  onChange={(e) => setMaxAgePreset(e.target.value)}
                >
                  {MAX_AGE_PRESETS.map((preset) => (
                    <option key={preset.value} value={preset.value}>
                      {preset.label}
                    </option>
                  ))}
                </Select>
                {maxAgePreset === 'custom' ? (
                  <Input
                    mono
                    className="w-32"
                    value={maxAgeCustom}
                    onChange={(e) => setMaxAgeCustom(e.target.value)}
                    placeholder="seconds"
                  />
                ) : null}
              </span>
            </label>
            <label className="grid gap-1 text-xs text-secondary">
              mx patterns — one per line{mode === 'none' ? ' (optional for mode none)' : ''}
              <textarea
                className="min-h-20 rounded-md border border-border bg-surface-card px-3 py-2 font-mono text-xs text-body focus-visible:outline-none focus-visible:shadow-[var(--focus-ring)]"
                value={patternsText}
                onChange={(e) => setPatternsText(e.target.value)}
                placeholder={'mx1.example.com\n*.mail.example.com'}
              />
            </label>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
              Serve this policy (off keeps the settings but answers 404)
            </label>

            <div className="rounded-md border border-border px-3 py-2">
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input type="checkbox" checked={bulkOpen} onChange={(e) => void toggleBulk(e.target.checked)} />
                Also apply to other domains in this client
              </label>
              {bulkOpen ? (
                siblings === null ? (
                  <p className="mt-2 text-xs text-secondary">Loading domains…</p>
                ) : siblings.length === 0 ? (
                  <p className="mt-2 text-xs text-secondary">No other active domains in this client.</p>
                ) : (
                  <div className="mt-2 space-y-1">
                    <label className="flex items-center gap-2 text-xs text-secondary">
                      <input
                        type="checkbox"
                        checked={selected.size === siblings.length}
                        onChange={(e) =>
                          setSelected(e.target.checked ? new Set(siblings.map((d) => d.id)) : new Set())
                        }
                      />
                      Select all ({siblings.length})
                    </label>
                    <div className="max-h-40 space-y-1 overflow-y-auto pl-5">
                      {siblings.map((domain) => (
                        <label key={domain.id} className="flex items-center gap-2 text-xs text-secondary">
                          <input
                            type="checkbox"
                            checked={selected.has(domain.id)}
                            onChange={(e) => {
                              const next = new Set(selected)
                              if (e.target.checked) next.add(domain.id)
                              else next.delete(domain.id)
                              setSelected(next)
                            }}
                          />
                          <span className="font-mono">{domain.name}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                )
              ) : null}
            </div>

            {error ? <Notice tone="danger">{error}</Notice> : null}
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="secondary" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" disabled={saving}>
                {selected.size > 0 ? `Save and apply to ${selected.size + 1} domains` : 'Save'}
              </Button>
            </div>
          </form>
        )}
      </DialogContent>
    </Dialog>
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

/**
 * Renders a source IP, allowing IPv6 to wrap at its colons.
 *
 * IPv6 is 38-40 characters and 316px wide in mono, against 119px for IPv4. Once the
 * hostname moved to its own spanning row, this became the only thing setting the
 * column's width — worth stating precisely, because the hostname is the intuitive
 * culprit and it is not the one. Shortening every hostname in the rendered table moved
 * the column not at all (348px before and after); shortening every IP took it from
 * 348px to 119px and the table from 1138px to 1038px, exactly its container.
 *
 * A <wbr> after each colon lets a long address fold at a group boundary and never
 * mid-hextet, so nothing is truncated and shorter addresses stay on one line. IPv4 has
 * no colons and is returned untouched.
 */
function SourceIpText({ ip }: { ip: string }) {
  if (!ip.includes(':')) return <>{ip}</>
  const groups = ip.split(':')
  return (
    <>
      {groups.map((group, i) => (
        <span key={i}>
          {group}
          {i < groups.length - 1 ? (
            <>
              :<wbr />
            </>
          ) : null}
        </span>
      ))}
    </>
  )
}

// Quarantined and Rejected moved into the expanded row, so 8 remain in the table.
const SOURCE_COLUMN_COUNT = 8

// The reverse-DNS hostname sits on its own row spanning the first four columns
// (Source IP, Messages, Failed, Compliance) rather than inside the Source IP cell.
// A 64-character AWS/Outlook hostname is 375px wide and was forcing that one column
// to 419px, which is most of why this table needed 1425px in a 1038px container.
// Spanning four columns gives it ~531px to use without widening anything.
const SOURCE_HOSTNAME_COLSPAN = 4

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

  usePageTitle(drilldown?.domain?.name ?? 'Domain')
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

  // When a source is selected (e.g. by clicking a blocking IP in the
  // enforcement card), bring its row into view — the sources table can be far
  // below the fold, so expanding it silently looked like nothing happened.
  useEffect(() => {
    if (!selectedSource) return
    const row = document.getElementById(`source-row-${selectedSource}`)
    row?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }, [selectedSource, sources])

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
            {/* No report has ever named a policy, so asserting p=none beside a
                "No data" badge would be a claim we cannot support — the domain
                may well publish p=reject. Matches the Domains list. */}
            {domain.publishedPolicy ? (
              <PolicyBadge policy={domain.publishedPolicy} />
            ) : (
              <span className="text-xs text-faint">policy unknown</span>
            )}
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
                  {!guidance.readyToAdvance && guidance.blockingSources.length > 0 ? (
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

          <TransportSecurityCard domainId={domainId} days={days} />

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
                      {/* Bounded so a 316px IPv6 address folds at a colon rather than
                          making this the widest column in the table again. */}
                      <TableHead className="w-[190px]" aria-sort={sortKey === 'ip' ? ariaSort : undefined}>
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
                      <TableHead className="text-right" aria-sort={sortKey === 'reporters' ? ariaSort : undefined}>
                        <SortHeader label="Reporters" column="reporters" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                      <TableHead aria-sort={sortKey === 'lastSeen' ? ariaSort : undefined}>
                        <SortHeader label="Last seen" column="lastSeen" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      </TableHead>
                    </TableRow>
                  </TableHeader>
                  {/* One tbody per source rather than one for the whole table. A source
                      occupies up to three rows — the values, the hostname beneath them,
                      and the expanded panel — and grouping them lets the divider and the
                      hover highlight belong to the source instead of to each row, which
                      is what makes the hostname read as part of the row above it. */}
                  {sortedSources.map((source) => {
                    const expanded = selectedSource === source.sourceIp
                    const hostname = hostnames[source.sourceIp]
                    return (
                      <tbody
                        key={source.sourceIp}
                        className={cn(
                          'border-b border-[var(--gray-100)] transition-colors duration-[120ms] ease-out hover:bg-gray-50',
                          expanded && 'bg-gray-50',
                        )}
                      >
                        <TableRow
                          id={`source-row-${source.sourceIp}`}
                          // The tbody owns the divider and the hover, so the rows inside
                          // must not draw their own or the group looks like two rows.
                          className="border-0 hover:bg-transparent"
                          onClick={() => toggleSource(source.sourceIp)}
                        >
                          <TableCell>
                            {/* IP only. The hostname used to live here and, at 375px for a
                                64-character Outlook name, it alone set this column's width. */}
                            <button
                              type="button"
                              aria-expanded={expanded}
                              onClick={(event) => {
                                event.stopPropagation()
                                toggleSource(source.sourceIp)
                              }}
                              className="inline-flex items-start gap-1.5 rounded-xs text-left font-mono text-sm font-medium text-body transition-colors hover:text-brand focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none"
                            >
                              <Icon
                                name="chevron-right"
                                size={14}
                                className={cn(
                                  'mt-0.5 shrink-0 text-secondary transition-transform',
                                  expanded && 'rotate-90',
                                )}
                              />
                              <span>
                                <SourceIpText ip={source.sourceIp} />
                              </span>
                            </button>
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
                            <ComplianceBar value={source.complianceRate * 100} width={110} />
                          </TableCell>
                          <TableCell align="right" className="tabular-nums">
                            {formatPercent(source.dkimPassRate)}
                          </TableCell>
                          <TableCell align="right" className="tabular-nums">
                            {formatPercent(source.spfPassRate)}
                          </TableCell>
                          <TableCell align="right" className="tabular-nums">
                            {source.reporters.toLocaleString('en-US')}
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-secondary">
                            {formatRelativeOrDate(source.lastSeenUtc)}
                          </TableCell>
                        </TableRow>
                        {hostname ? (
                          <TableRow
                            className="border-0 hover:bg-transparent"
                            onClick={() => toggleSource(source.sourceIp)}
                          >
                            <TableCell
                              colSpan={SOURCE_HOSTNAME_COLSPAN}
                              // pt-0 closes the gap to the IP above; pl-9 lines the hostname
                              // up under the IP rather than under the chevron.
                              className="pt-0 pb-3 pl-9 text-xs text-secondary"
                            >
                              {hostname}
                            </TableCell>
                          </TableRow>
                        ) : null}
                        {expanded ? (
                          <TableRow className="border-0 hover:bg-transparent">
                            <TableCell colSpan={SOURCE_COLUMN_COUNT} className="bg-gray-50 p-0">
                              {/* Quarantined and Rejected live here rather than as columns:
                                  the two of them cost 216px of table width, which is what
                                  kept this table wider than its container. */}
                              <dl className="flex gap-6 border-b border-[var(--gray-100)] px-4 py-2.5 text-xs">
                                <div className="flex gap-1.5">
                                  <dt className="text-secondary">Quarantined</dt>
                                  <dd className="tabular-nums text-body">{formatCompact(source.quarantined)}</dd>
                                </div>
                                <div className="flex gap-1.5">
                                  <dt className="text-secondary">Rejected</dt>
                                  <dd className="tabular-nums text-body">{formatCompact(source.rejected)}</dd>
                                </div>
                              </dl>
                              <SourceDetailPanel
                                domainId={domainId}
                                sourceIp={source.sourceIp}
                                days={days}
                              />
                            </TableCell>
                          </TableRow>
                        ) : null}
                      </tbody>
                    )
                  })}
                </Table>
              </div>
            )}
          </Card>
        </div>
      ) : null}
    </>
  )
}
