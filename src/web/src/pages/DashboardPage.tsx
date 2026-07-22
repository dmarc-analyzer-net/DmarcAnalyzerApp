import { Inbox, Loader2, RefreshCw } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { DaysSelector } from '@/components/DaysSelector'
import { TrendChart } from '@/components/TrendChart'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { parseAnalyticsDays, type AnalyticsDays, type AnalyticsSummary } from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { formatCompact, formatFullDate, formatPercent } from '@/lib/format'
import { useSystemStatus } from '@/lib/use-system-status'
import { cn } from '@/lib/utils'

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

export function DashboardPage() {
  const status = useSystemStatus()
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

  return (
    <>
      <Card>
        <CardHeader>
          <div>
            <CardTitle>Dashboard</CardTitle>
            <CardDescription className="mt-1">{status}</CardDescription>
            {summary && (
              <p className="mt-1 text-xs text-muted-foreground">
                {summary.window.anchoredToLatestData
                  ? `Data through ${formatFullDate(summary.window.endUtc)} — window anchored to the latest report data`
                  : `Last ${summary.window.days} days`}
              </p>
            )}
          </div>
          <div className="flex items-center gap-2">
            <DaysSelector value={days} onChange={setDays} disabled={busy} />
            <Button variant="outline" onClick={() => void loadData()} disabled={busy}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
          </div>
        </CardHeader>
        {!!error && (
          <CardContent>
            <p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {error}
            </p>
          </CardContent>
        )}
      </Card>

      {!summary && busy && (
        <div className="flex justify-center py-20">
          <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" aria-label="Loading" />
        </div>
      )}

      {isEmpty && summary && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-16 text-center">
            <Inbox className="h-10 w-10 text-muted-foreground" />
            <div>
              <p className="text-base font-semibold">No DMARC reports yet</p>
              <p className="mt-1 max-w-md text-sm text-muted-foreground">
                Analytics will appear once aggregate reports have been ingested. Mailboxes
                connected: {summary.mailboxes.healthy}/{summary.mailboxes.total} healthy.
              </p>
            </div>
            <Button asChild variant="outline" size="sm">
              <Link to="/mailbox-sources">Review mailbox sources</Link>
            </Button>
          </CardContent>
        </Card>
      )}

      {summary && totals && !isEmpty && (
        <div className={cn('space-y-4 transition-opacity', busy && 'opacity-60')}>
          {/* Hero + primary stat tiles */}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Card>
              <CardContent className="pt-4">
                <p className="text-xs font-medium text-muted-foreground">DMARC compliance</p>
                <p className="mt-1 text-5xl font-semibold leading-tight text-primary">
                  {formatPercent(totals.complianceRate)}
                </p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  {formatCompact(totals.compliantMessages)} of {formatCompact(totals.messages)}{' '}
                  messages compliant
                </p>
              </CardContent>
            </Card>
            <StatTile
              label="Total messages"
              value={formatCompact(totals.messages)}
              sub={`across ${formatCompact(totals.reports)} reports`}
            />
            <StatTile
              label="Active domains"
              value={`${totals.activeDomains}/${totals.domains}`}
              sub="active / total"
            />
            <StatTile
              label="Reports received"
              value={formatCompact(totals.reports)}
              sub={`${formatCompact(totals.failingSources)} failing sources`}
            />
          </div>

          {/* Secondary tiles */}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
            <StatTile label="DKIM pass rate" value={formatPercent(totals.dkimPassRate)} />
            <StatTile label="SPF pass rate" value={formatPercent(totals.spfPassRate)} />
            <StatTile
              label="Mailboxes"
              value={`${summary.mailboxes.healthy}/${summary.mailboxes.total}`}
              sub={
                summary.mailboxes.failing > 0
                  ? `${summary.mailboxes.failing} failing`
                  : 'All healthy'
              }
              subClassName={
                summary.mailboxes.failing > 0 ? 'font-medium text-destructive' : undefined
              }
            />
          </div>

          {/* Main chart */}
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
                trend={summary.trend}
                beginUtc={summary.window.beginUtc}
                endUtc={summary.window.endUtc}
              />
            </CardContent>
          </Card>

          {/* Side-by-side tables */}
          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <div>
                  <CardTitle>Domains needing attention</CardTitle>
                  <CardDescription className="mt-1">
                    Highest failing volume in this window
                  </CardDescription>
                </div>
              </CardHeader>
              <CardContent>
                {summary.topFailingDomains.length === 0 ? (
                  <p className="py-6 text-center text-sm text-muted-foreground">
                    No failing domains — everything is aligned.
                  </p>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Domain</TableHead>
                        <TableHead className="text-right">Messages</TableHead>
                        <TableHead className="text-right">Failed</TableHead>
                        <TableHead className="text-right">Compliance</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {summary.topFailingDomains.map((row) => (
                        <TableRow key={row.domainId}>
                          <TableCell className="font-medium">
                            <Link
                              to={`/domains/${row.domainId}${days === 30 ? '' : `?days=${days}`}`}
                              className="hover:text-primary hover:underline"
                            >
                              {row.domain}
                            </Link>
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {formatCompact(row.messages)}
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {formatCompact(row.failedMessages)}
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {formatPercent(row.complianceRate)}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <div>
                  <CardTitle>Top reporters</CardTitle>
                  <CardDescription className="mt-1">
                    Organizations sending aggregate reports
                  </CardDescription>
                </div>
              </CardHeader>
              <CardContent>
                {summary.topReporters.length === 0 ? (
                  <p className="py-6 text-center text-sm text-muted-foreground">
                    No reports received in this window.
                  </p>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Organization</TableHead>
                        <TableHead className="text-right">Reports</TableHead>
                        <TableHead className="text-right">Messages</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {summary.topReporters.map((row) => (
                        <TableRow key={row.organizationName}>
                          <TableCell className="font-medium">{row.organizationName}</TableCell>
                          <TableCell className="text-right tabular-nums">
                            {formatCompact(row.reports)}
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {formatCompact(row.messages)}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>
          </div>

          {/* Disposition summary */}
          <Card>
            <CardContent className="flex flex-wrap items-center gap-3 py-4">
              <p className="text-sm font-medium text-muted-foreground">Dispositions</p>
              <Badge variant="muted">none · {formatCompact(summary.dispositions.none)}</Badge>
              <Badge variant="warning">
                quarantine · {formatCompact(summary.dispositions.quarantine)}
              </Badge>
              <Badge variant="danger">reject · {formatCompact(summary.dispositions.reject)}</Badge>
            </CardContent>
          </Card>
        </div>
      )}
    </>
  )
}
