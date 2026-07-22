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

export type DomainAnalytics = {
  domainId: string
  name: string
  isActive: boolean
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
