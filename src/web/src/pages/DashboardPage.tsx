import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'

import { DaysSelector } from '@/components/DaysSelector'
import { StatCard } from '@/components/data/StatCard'
import { TrendChart } from '@/components/data/TrendChart'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table'
import { parseAnalyticsDays, type AnalyticsDays, type AnalyticsSummary } from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isStaff } from '@/lib/authz'
import { formatCompact, formatFullDate, formatPercent, formatShortDate } from '@/lib/format'

type BadgeVariant = 'success' | 'warning' | 'danger'

/** Severity for a per-domain compliance fraction (0..1). */
function complianceVariant(rate: number): BadgeVariant {
  if (rate < 0.75) return 'danger'
  if (rate < 0.95) return 'warning'
  return 'success'
}

export function DashboardPage() {
  const { user } = useAuth()
  const staff = isStaff(user)
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))

  const [summary, setSummary] = useState<AnalyticsSummary | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const requestSeq = useRef(0)

  const loadData = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const payload = await fetchJson<AnalyticsSummary>(`/api/v1/analytics/summary?days=${days}`)
      if (seq === requestSeq.current) setSummary(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load analytics')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [days])

  useEffect(() => {
    void loadData()
  }, [loadData])

  const setDays = (next: AnalyticsDays) => {
    setSearchParams(next === 30 ? {} : { days: String(next) }, { replace: true })
  }

  const totals = summary?.totals
  const isEmpty = !!summary && totals?.reports === 0

  const subtitle = (() => {
    if (!summary || !totals) return 'Monitoring across all domains'
    const parts = [`${formatCompact(totals.domains)} monitored`]
    parts.push(
      summary.window.anchoredToLatestData
        ? `data through ${formatFullDate(summary.window.endUtc)}`
        : `last ${summary.window.days} days`,
    )
    if (summary.mailboxes) {
      parts.push(`${summary.mailboxes.healthy}/${summary.mailboxes.total} mailboxes healthy`)
    }
    parts.push(`${summary.topFailingDomains.length} need attention`)
    return parts.join(' · ')
  })()

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Dashboard</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex shrink-0 gap-2.5">
          <DaysSelector value={days} onChange={setDays} disabled={busy} />
          <Button variant="secondary" icon="refresh-cw" onClick={() => void loadData()} disabled={busy}>
            Refresh
          </Button>
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {!summary && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {isEmpty && summary ? (
        <Card pad>
          <div className="flex flex-col items-center gap-3 py-16 text-center">
            <Icon name="inbox" size={40} className="text-faint" />
            <div>
              <p className="text-base font-semibold text-body">No DMARC reports yet</p>
              <p className="mt-1 max-w-md text-sm text-secondary">
                Analytics will appear once aggregate reports have been ingested.
                {summary.mailboxes
                  ? ` Mailboxes connected: ${summary.mailboxes.healthy}/${summary.mailboxes.total} healthy.`
                  : ''}
              </p>
            </div>
            {staff ? (
              <Button asChild variant="secondary" size="sm">
                <Link to="/mailbox-sources">Review mailbox sources</Link>
              </Button>
            ) : null}
          </div>
        </Card>
      ) : null}

      {summary && totals && !isEmpty ? (
        <div className={busy ? 'opacity-60 transition-opacity' : 'transition-opacity'}>
          <div className="mb-3.5 grid grid-cols-2 gap-3.5 sm:grid-cols-4">
            <StatCard label="Domains monitored" value={formatCompact(totals.domains)} />
            <StatCard label="DMARC compliance" value={formatPercent(totals.complianceRate)} />
            <StatCard label="Messages analyzed" value={formatCompact(totals.messages)} />
            <StatCard
              label="Spoofing blocked"
              value={formatCompact(summary.dispositions.quarantine + summary.dispositions.reject)}
              extra={
                totals.failingSources > 0 ? (
                  <Badge variant="danger">{formatCompact(totals.failingSources)} sources</Badge>
                ) : undefined
              }
            />
          </div>

          <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-[1.6fr_1fr]">
            <Card pad>
              <CardHeader title="Messages by day" description="Pass vs fail across all domains" />
              <TrendChart
                height={170}
                data={summary.trend.map((point) => ({
                  label: formatShortDate(point.date),
                  pass: point.compliant,
                  fail: point.failed,
                }))}
              />
            </Card>

            <Card pad={false}>
              <div className="px-5 pt-4 pb-2">
                <CardHeader
                  title="Needs attention"
                  description={
                    summary.topFailingDomains.length === 1
                      ? '1 domain below target'
                      : `${summary.topFailingDomains.length} domains below target`
                  }
                />
              </div>
              {summary.topFailingDomains.length === 0 ? (
                <p className="px-5 pb-5 text-sm text-secondary">
                  No domains below target — everything is aligned.
                </p>
              ) : (
                <Table>
                  <TableBody>
                    {summary.topFailingDomains.map((row, index) => (
                      <TableRow
                        key={row.domainId}
                        last={index === summary.topFailingDomains.length - 1}
                        onClick={() =>
                          navigate(
                            `/domains/${row.domainId}${days === 30 ? '' : `?days=${days}`}`,
                          )
                        }
                      >
                        <TableCell mono>{row.domain}</TableCell>
                        <TableCell align="right">
                          <Badge variant={complianceVariant(row.complianceRate)}>
                            {formatPercent(row.complianceRate)}
                          </Badge>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </Card>
          </div>
        </div>
      ) : null}
    </>
  )
}
