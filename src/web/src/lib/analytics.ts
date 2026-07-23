export type AnalyticsDays = 7 | 30 | 90

export const ANALYTICS_DAYS_OPTIONS: AnalyticsDays[] = [7, 30, 90]

export function parseAnalyticsDays(value: string | null): AnalyticsDays {
  if (value === '7') return 7
  if (value === '90') return 90
  return 30
}

export type AnalyticsWindow = {
  days: number
  beginUtc: string
  endUtc: string
  anchoredToLatestData: boolean
}

export type AnalyticsTotals = {
  domains: number
  activeDomains: number
  reports: number
  messages: number
  compliantMessages: number
  complianceRate: number
  dkimPassRate: number
  spfPassRate: number
  failingSources: number
}

export type TrendPoint = {
  date: string
  messages: number
  compliant: number
  failed: number
}

export type TopFailingDomain = {
  domainId: string
  domain: string
  messages: number
  failedMessages: number
  complianceRate: number
}

export type TopReporter = {
  organizationName: string
  reports: number
  messages: number
}

export type AnalyticsSummary = {
  window: AnalyticsWindow
  totals: AnalyticsTotals
  trend: TrendPoint[]
  topFailingDomains: TopFailingDomain[]
  topReporters: TopReporter[]
  dispositions: { none: number; quarantine: number; reject: number }
  /** Mailbox ops rollup; null for client_viewer users, who have no mailbox visibility. */
  mailboxes: { total: number; healthy: number; failing: number } | null
}

export type DomainStatus = 'aligned' | 'issues' | 'critical' | 'no_data'

export type DomainStatusMeta = {
  label: string
  badge: 'success' | 'warning' | 'danger' | 'muted'
  /** Meter bar color. */
  fill: string
  /** Meter track (background) color. */
  track: string
}

/** Status pill + compliance meter styling shared by the domain pages. */
export const DOMAIN_STATUS_META: Record<DomainStatus, DomainStatusMeta> = {
  aligned: { label: 'Aligned', badge: 'success', fill: '#059669', track: '#d1fae5' },
  issues: { label: 'Issues', badge: 'warning', fill: '#d97706', track: '#fef3c7' },
  critical: { label: 'Critical', badge: 'danger', fill: '#e11d48', track: '#ffe4e6' },
  no_data: { label: 'No data', badge: 'muted', fill: '#94a3b8', track: '#e2e8f0' },
}

/** DMARC policy published in DNS (`p=` tag). */
export type DmarcPublishedPolicy = 'none' | 'quarantine' | 'reject'

/** Policy-aware enforcement posture derived from published policy + compliance. */
export type EnforcementStatus = 'no_data' | 'enforced' | 'ramping' | 'spoofing' | 'monitoring'

export type EnforcementStatusMeta = {
  label: string
  badge: 'success' | 'warning' | 'danger' | 'neutral'
  /** Status-dot color (CSS var) matching the badge variant. */
  dot: string
}

/** Status Badge label/variant + dot color for each enforcement posture. */
export const ENFORCEMENT_STATUS_META: Record<EnforcementStatus, EnforcementStatusMeta> = {
  enforced: { label: 'Enforced', badge: 'success', dot: 'var(--status-ok-dot)' },
  ramping: { label: 'Ramping', badge: 'warning', dot: 'var(--status-warn-dot)' },
  spoofing: { label: 'Spoofing', badge: 'danger', dot: 'var(--status-danger-dot)' },
  monitoring: { label: 'Monitoring', badge: 'neutral', dot: 'var(--status-neutral-dot)' },
  no_data: { label: 'No data', badge: 'neutral', dot: 'var(--status-neutral-dot)' },
}

/**
 * Mirrors the server's enforcement resolution (see AnalyticsDtos EnforcementStatus).
 * The drilldown endpoint returns the raw published policy but not the derived
 * status, so the detail page recomputes it from totals + policy.
 */
export function resolveEnforcementStatus(
  messages: number,
  complianceRate: number,
  publishedPolicy: DmarcPublishedPolicy | null | undefined,
): EnforcementStatus {
  if (messages === 0) return 'no_data'
  if (publishedPolicy === 'reject') return 'enforced'
  if (publishedPolicy === 'quarantine') return 'ramping'
  return complianceRate < 0.98 ? 'spoofing' : 'monitoring'
}

export type DomainAnalytics = {
  domainId: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string
  clientSlug: string
  messages: number
  compliantMessages: number
  complianceRate: number
  dkimPassRate: number
  spfPassRate: number
  reports: number
  sources: number
  reporters: number
  quarantined: number
  rejected: number
  lastReportEndUtc: string | null
  status: DomainStatus
  /** DMARC policy published in DNS; null when unknown. */
  publishedPolicy: DmarcPublishedPolicy | null
  subdomainPolicy: string | null
  /** Rollout percentage (`pct=`); null when unknown. */
  publishedPct: number | null
  dkimAlignment: string | null
  spfAlignment: string | null
  /** Policy-aware posture used by the Status column. */
  enforcementStatus: EnforcementStatus
}

// --- Per-domain drill-down (GET /api/v1/analytics/domains/{domainId}/drilldown) ---

export type DrilldownDomain = {
  domainId: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string
  clientSlug: string
  /** DMARC policy published in DNS; null when unknown. */
  publishedPolicy: DmarcPublishedPolicy | null
  subdomainPolicy: string | null
  /** Rollout percentage (`pct=`); null when unknown. */
  publishedPct: number | null
  dkimAlignment: string | null
  spfAlignment: string | null
}

export type DrilldownTotals = {
  messages: number
  compliantMessages: number
  complianceRate: number
  dkimPassRate: number
  spfPassRate: number
  reports: number
  sources: number
  reporters: number
  quarantined: number
  rejected: number
  status: DomainStatus
}

export type DomainDrilldown = {
  domain: DrilldownDomain
  window: AnalyticsWindow
  totals: DrilldownTotals
  trend: TrendPoint[]
}

// --- Per-source rows (GET /api/v1/analytics/domains/{domainId}/sources) ---

export type DomainSourceAnalytics = {
  sourceIp: string
  messages: number
  compliantMessages: number
  failedMessages: number
  complianceRate: number
  dkimPassRate: number
  spfPassRate: number
  quarantined: number
  rejected: number
  reporters: number
  headerFroms: number
  firstSeenUtc: string
  lastSeenUtc: string
}

// --- Source detail (GET /api/v1/analytics/domains/{domainId}/source-detail) ---

/** Policy-evaluated DKIM x SPF combo; a message is DMARC-compliant when either passes. */
export type EvaluatedCombo = {
  dkim: 'pass' | 'fail'
  spf: 'pass' | 'fail'
  messages: number
}

export type ValueCount = {
  value: string
  messages: number
}

/** Raw DKIM auth result: which domain/selector signed and what the verdict was. */
export type DkimAuthResult = {
  domain: string
  selector: string | null
  result: string
  messages: number
}

/** Raw SPF auth result: which domain was checked in which scope and the verdict. */
export type SpfAuthResult = {
  domain: string
  scope: string | null
  result: string
  messages: number
}

export type SourceDetail = {
  sourceIp: string
  messages: number
  compliantMessages: number
  complianceRate: number
  dispositions: { none: number; quarantine: number; reject: number }
  evaluated: EvaluatedCombo[]
  headerFroms: ValueCount[]
  envelopeFroms: ValueCount[]
  dkimAuth: DkimAuthResult[]
  spfAuth: SpfAuthResult[]
  reporters: TopReporter[]
  trend: TrendPoint[]
}
