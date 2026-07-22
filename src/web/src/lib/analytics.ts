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
  mailboxes: { total: number; healthy: number; failing: number }
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
}

// --- Per-domain drill-down (GET /api/v1/analytics/domains/{domainId}/drilldown) ---

export type DrilldownDomain = {
  domainId: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string
  clientSlug: string
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
