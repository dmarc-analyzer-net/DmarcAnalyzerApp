import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { PolicyBadge } from '@/components/data/PolicyBadge'
import { StatCard } from '@/components/data/StatCard'
import { DaysSelector } from '@/components/DaysSelector'
import { Badge } from '@/components/ui/badge'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { parseAnalyticsDays, type AnalyticsDays, type ThreatFeed } from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { formatCompact, formatFullDate, formatPercent, formatRelativeOrDate } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * Threat feed: every sending source with fully unauthenticated volume (both
 * DKIM and SPF failed) across the visible domains — spoofing candidates and
 * forgotten senders, worst first. Rows link into the domain drill-down with
 * the source pre-expanded (`?source=`).
 */
export function ThreatsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))

  const [feed, setFeed] = useState<ThreatFeed | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [hostnames, setHostnames] = useState<Record<string, string | null>>({})
  const requestSeq = useRef(0)

  const loadData = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const payload = await fetchJson<ThreatFeed>(`/api/v1/analytics/threats?days=${days}&limit=100`)
      if (seq === requestSeq.current) setFeed(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load threat feed')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [days])

  useEffect(() => {
    void loadData()
  }, [loadData])

  // Reverse-DNS enrichment, same best-effort pattern as the domain drill-down.
  useEffect(() => {
    const sources = feed?.sources ?? []
    if (sources.length === 0) return
    let cancelled = false
    const ips = [...new Set(sources.slice(0, 100).map((s) => s.sourceIp))]
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
  }, [feed])

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

  const windowLabel = feed
    ? feed.window.anchoredToLatestData
      ? `data through ${formatFullDate(feed.window.endUtc)}`
      : `last ${feed.window.days} days`
    : null

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight text-body">Threats</h1>
          <p className="mt-1 text-sm text-secondary">
            Sources sending fully unauthenticated mail as your domains
            {windowLabel ? ` · ${windowLabel}` : ''}
          </p>
        </div>
        <DaysSelector value={days} onChange={setDays} disabled={busy} />
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {!feed && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {feed ? (
        <div className={cn('space-y-3.5 transition-opacity', busy && 'opacity-60')}>
          <div className="grid grid-cols-2 gap-3.5 sm:grid-cols-3">
            <StatCard label="Unauthenticated messages" value={formatCompact(feed.totalFailedMessages)} />
            <StatCard label="Failing sources" value={feed.totalSources.toLocaleString('en-US')} />
            <StatCard
              label="Showing"
              value={
                feed.totalSources > feed.sources.length
                  ? `top ${feed.sources.length}`
                  : String(feed.sources.length)
              }
            />
          </div>

          <Card>
            <div className="flex items-start justify-between gap-3 px-5 pt-5">
              <CardHeader
                title="Failing sources"
                description="Every source whose mail failed both DKIM and SPF — spoofing candidates and forgotten senders, worst first"
              />
              <Badge variant="danger">{feed.totalSources} sources</Badge>
            </div>
            {feed.sources.length === 0 ? (
              <div className="flex flex-col items-center gap-2 px-5 pb-10 pt-4 text-center">
                <Icon name="circle-check" size={32} className="text-[var(--status-ok-dot)]" />
                <p className="text-sm font-semibold text-body">No unauthenticated sources</p>
                <p className="max-w-md text-sm text-secondary">
                  Every message in this window passed DKIM or SPF. Nothing looks like spoofing.
                </p>
              </div>
            ) : (
              <div className="overflow-x-auto">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Source</TableHead>
                      <TableHead>Domain</TableHead>
                      <TableHead>Client</TableHead>
                      <TableHead className="text-right">Failed</TableHead>
                      <TableHead className="text-right">Messages</TableHead>
                      <TableHead className="text-right">Compliance</TableHead>
                      <TableHead>Policy</TableHead>
                      <TableHead>First seen</TableHead>
                      <TableHead>Last seen</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {feed.sources.map((source) => {
                      const hostname = hostnames[source.sourceIp]
                      return (
                        <TableRow key={`${source.sourceIp}-${source.domainId}`} className="cursor-default">
                          <TableCell>
                            <div className="font-mono text-xs text-body">{source.sourceIp}</div>
                            {hostname ? (
                              <div className="mt-0.5 max-w-[220px] truncate font-mono text-[11px] text-secondary">
                                {hostname}
                              </div>
                            ) : null}
                          </TableCell>
                          <TableCell>
                            <Link
                              to={`/domains/${source.domainId}?source=${encodeURIComponent(source.sourceIp)}${days !== 30 ? `&days=${days}` : ''}`}
                              className="font-mono text-xs text-body underline decoration-dotted underline-offset-2 hover:text-brand"
                              title="Open the domain drill-down with this source expanded"
                            >
                              {source.domain}
                            </Link>
                          </TableCell>
                          <TableCell className="text-sm text-secondary">{source.clientName}</TableCell>
                          <TableCell className="text-right font-semibold tabular-nums text-[var(--status-danger-fg)]">
                            {formatCompact(source.failedMessages)}
                          </TableCell>
                          <TableCell className="text-right tabular-nums text-secondary">
                            {formatCompact(source.messages)}
                          </TableCell>
                          <TableCell className="text-right tabular-nums text-secondary">
                            {formatPercent(source.complianceRate)}
                          </TableCell>
                          <TableCell>
                            <PolicyBadge policy={source.publishedPolicy ?? 'none'} />
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-xs text-secondary">
                            {formatRelativeOrDate(source.firstSeenUtc)}
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-xs text-secondary">
                            {formatRelativeOrDate(source.lastSeenUtc)}
                          </TableCell>
                        </TableRow>
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
