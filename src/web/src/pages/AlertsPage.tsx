import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { StatCard } from '@/components/data/StatCard'
import { DaysSelector } from '@/components/DaysSelector'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  ALERT_RULE_LABEL,
  ALERT_SEVERITY_META,
  ALERT_STATUS_META,
  parseAnalyticsDays,
  type AlertEvent,
  type AlertStatus,
  type AnalyticsDays,
} from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin, isStaff } from '@/lib/authz'
import { formatRelativeOrDate } from '@/lib/format'
import { usePageTitle } from '@/lib/use-page-title'
import { cn } from '@/lib/utils'

/**
 * Alert history. Alerts are raised by the worker on its own schedule; admins can
 * force an evaluation here rather than waiting for the next pass.
 */
export function AlertsPage() {
  usePageTitle('Alerts')
  const { user } = useAuth()
  const admin = isAdmin(user)
  const staff = isStaff(user)
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))

  const [alerts, setAlerts] = useState<AlertEvent[] | null>(null)
  const [busy, setBusy] = useState(true)
  const [evaluating, setEvaluating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const requestSeq = useRef(0)

  const loadData = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const payload = await fetchJson<AlertEvent[]>(`/api/v1/alerts?days=${days}`)
      if (seq === requestSeq.current) setAlerts(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load alerts')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [days])

  useEffect(() => {
    void loadData()
  }, [loadData])

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

  const evaluateNow = async () => {
    setEvaluating(true)
    setNotice(null)
    setError(null)
    try {
      const result = await fetchJson<{ alertsRaised: number; suppressed: number; emailsSent: number }>(
        '/api/v1/admin/alerts/evaluate',
        { method: 'POST' },
      )
      setNotice(
        result.alertsRaised === 0
          ? `No new alerts. ${result.suppressed} suppressed by cooldown.`
          : `${result.alertsRaised} new alert(s), ${result.emailsSent} email(s) sent.`,
      )
      await loadData()
    } catch (evaluateError) {
      setError(evaluateError instanceof Error ? evaluateError.message : 'Evaluation failed')
    } finally {
      setEvaluating(false)
    }
  }

  const setStatus = async (id: string, status: AlertStatus) => {
    setError(null)
    // Optimistic: triage should feel instant, and a failure re-syncs below.
    setAlerts((prev) => prev?.map((a) => (a.id === id ? { ...a, status } : a)) ?? prev)
    try {
      await fetchJson(`/api/v1/alerts/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status }),
      })
    } catch (patchError) {
      setError(patchError instanceof Error ? patchError.message : 'Could not update that alert')
      await loadData()
    }
  }

  const critical = alerts?.filter((a) => a.severity === 'critical').length ?? 0
  const unnotified = alerts?.filter((a) => a.notifiedAtUtc === null).length ?? 0
  const open = alerts?.filter((a) => a.status === 'open').length ?? 0

  return (
    <>
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight text-body">Alerts</h1>
          <p className="mt-1 text-sm text-secondary">
            Compliance drops and weakened policies, raised automatically
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2.5">
          {admin ? (
            <Button variant="secondary" size="sm" onClick={() => void evaluateNow()} disabled={evaluating}>
              {evaluating ? (
                <Icon name="loader-circle" size={14} className="animate-spin" />
              ) : (
                <Icon name="refresh-cw" size={14} />
              )}
              Evaluate now
            </Button>
          ) : null}
          <DaysSelector value={days} onChange={setDays} disabled={busy} />
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}
      {notice ? (
        <div className="mb-3.5 rounded-md border border-border bg-surface-sunken px-3 py-2 text-sm text-secondary">
          {notice}
        </div>
      ) : null}

      {alerts === null && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {alerts ? (
        <div className={cn('space-y-3.5 transition-opacity', busy && 'opacity-60')}>
          <div className="grid grid-cols-2 gap-3.5 sm:grid-cols-4">
            <StatCard label={`Alerts ${days}d`} value={alerts.length.toLocaleString('en-US')} />
            <StatCard
              label="Open"
              value={open.toLocaleString('en-US')}
              extra={open > 0 ? <Badge variant="warning">needs triage</Badge> : undefined}
            />
            <StatCard
              label="Critical"
              value={critical.toLocaleString('en-US')}
              extra={critical > 0 ? <Badge variant="danger">attention</Badge> : undefined}
            />
            <StatCard
              label="Not emailed"
              value={unnotified.toLocaleString('en-US')}
              extra={unnotified > 0 ? <Badge variant="warning">no recipient</Badge> : undefined}
            />
          </div>

          <Card>
            <div className="px-5 pt-5">
              <CardHeader
                title="Recent alerts"
                description="Newest first. Repeat alerts for the same domain are suppressed by a cooldown."
              />
            </div>
            {alerts.length === 0 ? (
              <div className="flex flex-col items-center gap-2 px-5 pb-10 pt-4 text-center">
                <Icon name="circle-check" size={32} className="text-[var(--status-ok-dot)]" />
                <p className="text-sm font-semibold text-body">Nothing to report</p>
                <p className="max-w-md text-sm text-secondary">
                  No compliance drops or policy regressions in this window.
                </p>
              </div>
            ) : (
              <div className="overflow-x-auto">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Severity</TableHead>
                      <TableHead>Type</TableHead>
                      <TableHead>What happened</TableHead>
                      <TableHead>Client</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead>Detected</TableHead>
                      <TableHead>Emailed</TableHead>
                      {staff ? <TableHead className="text-right">Triage</TableHead> : null}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {alerts.map((alert) => {
                      const meta = ALERT_SEVERITY_META[alert.severity]
                      return (
                        <TableRow key={alert.id}>
                          <TableCell>
                            <Badge variant={meta.badge}>{meta.label}</Badge>
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-sm text-secondary">
                            {ALERT_RULE_LABEL[alert.ruleType] ?? alert.ruleType}
                          </TableCell>
                          <TableCell>
                            {alert.domainId ? (
                              <Link
                                to={`/domains/${alert.domainId}`}
                                className="text-sm font-medium text-body underline decoration-dotted underline-offset-2 hover:text-brand"
                              >
                                {alert.title}
                              </Link>
                            ) : (
                              <span className="text-sm font-medium text-body">{alert.title}</span>
                            )}
                            <div className="mt-0.5 max-w-[60ch] text-xs leading-relaxed text-secondary">
                              {alert.details}
                            </div>
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-sm text-secondary">
                            {alert.clientName}
                          </TableCell>
                          <TableCell>
                            <Badge variant={ALERT_STATUS_META[alert.status].badge}>
                              {ALERT_STATUS_META[alert.status].label}
                            </Badge>
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-xs text-secondary">
                            {formatRelativeOrDate(alert.detectedAtUtc)}
                          </TableCell>
                          <TableCell className="whitespace-nowrap text-xs text-secondary">
                            {alert.notifiedAtUtc ? (
                              formatRelativeOrDate(alert.notifiedAtUtc)
                            ) : (
                              <span className="text-[var(--status-warn-fg)]">not sent</span>
                            )}
                          </TableCell>
                          {staff ? (
                            <TableCell className="whitespace-nowrap text-right">
                              {alert.status === 'open' ? (
                                <Button variant="ghost" size="sm" onClick={() => void setStatus(alert.id, 'acknowledged')}>
                                  <Icon name="check" size={14} />
                                  Acknowledge
                                </Button>
                              ) : alert.status === 'acknowledged' ? (
                                <Button variant="ghost" size="sm" onClick={() => void setStatus(alert.id, 'closed')}>
                                  <Icon name="circle-check" size={14} />
                                  Close
                                </Button>
                              ) : (
                                <Button variant="ghost" size="sm" onClick={() => void setStatus(alert.id, 'open')}>
                                  <Icon name="refresh-cw" size={14} />
                                  Reopen
                                </Button>
                              )}
                            </TableCell>
                          ) : null}
                        </TableRow>
                      )
                    })}
                  </TableBody>
                </Table>
              </div>
            )}
          </Card>

          {unnotified > 0 ? (
            <p className="text-xs text-secondary">
              Alerts show as “not sent” when no recipient is configured for the client, or when SMTP
              isn’t set up. Add recipients under <Link to="/notifications" className="underline">Notifications</Link>.
            </p>
          ) : null}
        </div>
      ) : null}
    </>
  )
}
