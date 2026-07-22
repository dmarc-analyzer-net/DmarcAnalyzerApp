import { ArrowDown, ArrowUp, ArrowUpDown, RefreshCw } from 'lucide-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'

import { DaysSelector } from '@/components/DaysSelector'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  parseAnalyticsDays,
  type AnalyticsDays,
  type DomainAnalytics,
  type DomainStatus,
} from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import type { Client, Domain } from '@/lib/entities'
import { formatCompact, formatPercent, formatRelativeOrDate } from '@/lib/format'
import { useSystemStatus } from '@/lib/use-system-status'
import { cn } from '@/lib/utils'

const initialDomainForm = {
  name: '',
  clientId: '',
  isActive: true,
}

const statusMeta: Record<
  DomainStatus,
  { label: string; badge: 'success' | 'warning' | 'danger' | 'muted'; fill: string; track: string }
> = {
  aligned: { label: 'Aligned', badge: 'success', fill: '#059669', track: '#d1fae5' },
  issues: { label: 'Issues', badge: 'warning', fill: '#d97706', track: '#fef3c7' },
  critical: { label: 'Critical', badge: 'danger', fill: '#e11d48', track: '#ffe4e6' },
  no_data: { label: 'No data', badge: 'muted', fill: '#94a3b8', track: '#e2e8f0' },
}

const statusRank: Record<DomainStatus, number> = {
  critical: 0,
  issues: 1,
  aligned: 2,
  no_data: 3,
}

type DomainRow = Omit<DomainAnalytics, 'clientName' | 'clientSlug'> & {
  clientName: string | null
  clientSlug: string | null
  crud: Domain | null
}

type SortKey =
  | 'name'
  | 'status'
  | 'compliance'
  | 'messages'
  | 'dkim'
  | 'spf'
  | 'sources'
  | 'lastReport'
  | 'client'
type SortDir = 'asc' | 'desc'

/** Direction applied when a column first becomes the active sort. */
const defaultSortDir: Record<SortKey, SortDir> = {
  name: 'asc',
  status: 'asc',
  compliance: 'asc',
  messages: 'desc',
  dkim: 'asc',
  spf: 'asc',
  sources: 'desc',
  lastReport: 'desc',
  client: 'asc',
}

/** Metric columns render "—" for no_data rows, so those rows always sort last. */
const metricSortKeys: ReadonlySet<SortKey> = new Set(['compliance', 'messages', 'dkim', 'spf', 'sources'])

function compareRows(a: DomainRow, b: DomainRow, key: SortKey, dir: SortDir): number {
  const flip = dir === 'asc' ? 1 : -1

  if (metricSortKeys.has(key) && (a.status === 'no_data') !== (b.status === 'no_data')) {
    return a.status === 'no_data' ? 1 : -1
  }

  let cmp = 0
  switch (key) {
    case 'name':
      cmp = a.name.localeCompare(b.name)
      break
    case 'status':
      cmp = statusRank[a.status] - statusRank[b.status]
      break
    case 'compliance':
      cmp = a.complianceRate - b.complianceRate
      break
    case 'messages':
      cmp = a.messages - b.messages
      break
    case 'dkim':
      cmp = a.dkimPassRate - b.dkimPassRate
      break
    case 'spf':
      cmp = a.spfPassRate - b.spfPassRate
      break
    case 'sources':
      cmp = a.sources - b.sources
      break
    case 'lastReport': {
      const aTime = a.lastReportEndUtc ? Date.parse(a.lastReportEndUtc) : null
      const bTime = b.lastReportEndUtc ? Date.parse(b.lastReportEndUtc) : null
      if ((aTime === null) !== (bTime === null)) return aTime === null ? 1 : -1
      cmp = (aTime ?? 0) - (bTime ?? 0)
      break
    }
    case 'client': {
      const aName = a.clientName ?? ''
      const bName = b.clientName ?? ''
      if (!aName !== !bName) return aName ? -1 : 1
      cmp = aName.localeCompare(bName)
      break
    }
  }
  if (cmp !== 0) return cmp * flip

  // Stable tie-breakers: higher volume first, then name.
  if (a.messages !== b.messages) return b.messages - a.messages
  return a.name.localeCompare(b.name)
}

type SortButtonProps = {
  label: string
  column: SortKey
  sortKey: SortKey
  sortDir: SortDir
  onSort: (key: SortKey) => void
}

function SortButton({ label, column, sortKey, sortDir, onSort }: SortButtonProps) {
  const active = column === sortKey
  const Arrow = active ? (sortDir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown
  return (
    <button
      type="button"
      onClick={() => onSort(column)}
      className={cn(
        'group inline-flex items-center gap-1 rounded-sm font-medium transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        active && 'text-foreground',
      )}
    >
      {label}
      <Arrow
        aria-hidden
        className={cn(
          'h-3 w-3 shrink-0',
          !active && 'opacity-0 transition-opacity group-hover:opacity-60 group-focus-visible:opacity-60',
        )}
      />
    </button>
  )
}

function ComplianceMeter({ row }: { row: DomainRow }) {
  if (row.status === 'no_data') return <span className="text-muted-foreground">—</span>
  const meta = statusMeta[row.status]
  const pct = Math.max(0, Math.min(1, row.complianceRate)) * 100
  return (
    <div className="flex items-center gap-2">
      <span className="w-12 text-right tabular-nums">{formatPercent(row.complianceRate)}</span>
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

export function DomainsPage() {
  const status = useSystemStatus()
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))
  const clientFilter = searchParams.get('client') ?? ''

  const [clients, setClients] = useState<Client[]>([])
  const [domains, setDomains] = useState<Domain[]>([])
  const [analytics, setAnalytics] = useState<DomainAnalytics[]>([])
  const [search, setSearch] = useState('')
  const [sortKey, setSortKey] = useState<SortKey>('compliance')
  const [sortDir, setSortDir] = useState<SortDir>('asc')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingDomainId, setEditingDomainId] = useState<string | null>(null)
  const [domainForm, setDomainForm] = useState(initialDomainForm)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, domainData, analyticsData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<Domain[]>('/api/v1/domains'),
        fetchJson<DomainAnalytics[]>(`/api/v1/analytics/domains?days=${days}`),
      ])
      setClients(clientData)
      setDomains(domainData)
      setAnalytics(analyticsData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load domains')
    } finally {
      setBusy(false)
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

  const setClientFilter = (clientId: string) => {
    setSearchParams(
      (prev) => {
        const params = new URLSearchParams(prev)
        if (clientId) params.set('client', clientId)
        else params.delete('client')
        return params
      },
      { replace: true },
    )
  }

  const handleSort = (key: SortKey) => {
    if (key === sortKey) {
      setSortDir((dir) => (dir === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortKey(key)
      setSortDir(defaultSortDir[key])
    }
  }

  const ariaSort: 'ascending' | 'descending' = sortDir === 'asc' ? 'ascending' : 'descending'

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  )

  const rows = useMemo<DomainRow[]>(() => {
    const clientById = new Map(clients.map((c) => [c.id, c]))
    const crudById = new Map(domains.map((d) => [d.id, d]))
    const merged: DomainRow[] = analytics.map((row) => ({
      ...row,
      crud: crudById.get(row.domainId) ?? null,
    }))

    // Defensive: include CRUD domains the analytics endpoint did not return.
    const seen = new Set(analytics.map((row) => row.domainId))
    for (const domain of domains) {
      if (seen.has(domain.id)) continue
      const client = clientById.get(domain.clientId)
      merged.push({
        domainId: domain.id,
        name: domain.name,
        isActive: domain.isActive,
        clientId: domain.clientId,
        clientName: domain.clientName ?? client?.name ?? null,
        clientSlug: client?.slug ?? null,
        messages: 0,
        compliantMessages: 0,
        complianceRate: 0,
        dkimPassRate: 0,
        spfPassRate: 0,
        reports: 0,
        sources: 0,
        reporters: 0,
        quarantined: 0,
        rejected: 0,
        lastReportEndUtc: null,
        status: 'no_data',
        crud: domain,
      })
    }

    return merged
  }, [analytics, domains, clients])

  const filteredRows = useMemo(() => {
    const q = search.toLowerCase().trim()
    return rows.filter((row) => {
      if (clientFilter && row.clientId !== clientFilter) return false
      if (!q) return true
      return row.name.toLowerCase().includes(q) || (row.clientName ?? '').toLowerCase().includes(q)
    })
  }, [search, clientFilter, rows])

  // Default: worst compliance first, no_data last; ties broken by volume, then name.
  const sortedRows = useMemo(
    () => [...filteredRows].sort((a, b) => compareRows(a, b, sortKey, sortDir)),
    [filteredRows, sortKey, sortDir],
  )

  const resetDialog = () => {
    setDialogOpen(false)
    setEditingDomainId(null)
    setDomainForm((x) => ({ ...initialDomainForm, clientId: x.clientId }))
    setError(null)
  }

  const openDomainDialog = (row?: DomainRow) => {
    setError(null)
    setDialogOpen(true)
    if (row) {
      setEditingDomainId(row.domainId)
      setDomainForm({
        name: row.name,
        clientId: row.crud?.clientId ?? row.clientId,
        isActive: row.isActive,
      })
    } else {
      setEditingDomainId(null)
      setDomainForm({
        ...initialDomainForm,
        clientId: sortedClients[0]?.id ?? '',
      })
    }
  }

  const createOrUpdateDomain = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      if (editingDomainId) {
        await fetchJson(`/api/v1/domains/${editingDomainId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(domainForm),
        })
      } else {
        await fetchJson('/api/v1/domains', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(domainForm),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save domain')
    }
  }

  return (
    <>
      <Card>
        <CardHeader>
          <div>
            <CardTitle>Operations Console</CardTitle>
            <CardDescription className="mt-1">{status}</CardDescription>
          </div>
          <div className="flex flex-wrap items-center justify-end gap-2">
            <DaysSelector value={days} onChange={setDays} disabled={busy} />
            <Select
              aria-label="Filter by client"
              className="w-44"
              value={clientFilter}
              onChange={(e) => setClientFilter(e.target.value)}
            >
              <option value="">All clients</option>
              {sortedClients.map((client) => (
                <option key={client.id} value={client.id}>
                  {client.name}
                </option>
              ))}
            </Select>
            <Input
              placeholder="Search current list..."
              className="w-64"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            <Button variant="outline" onClick={() => void loadData()} disabled={busy}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            <Button onClick={() => openDomainDialog()}>New Domain</Button>
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

      <Card>
        <CardHeader>
          <div>
            <CardTitle>Domains</CardTitle>
            <CardDescription className="mt-1">
              DMARC posture per domain over the last {days} days
            </CardDescription>
          </div>
          <Badge variant="muted">{filteredRows.length} records</Badge>
        </CardHeader>
        <CardContent>
          <div className={cn('overflow-x-auto transition-opacity', busy && 'opacity-60')}>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead aria-sort={sortKey === 'name' || sortKey === 'client' ? ariaSort : undefined}>
                    <span className="inline-flex items-center gap-1.5">
                      <SortButton label="Domain" column="name" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                      <span aria-hidden className="text-muted-foreground/50">/</span>
                      <SortButton label="Client" column="client" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                    </span>
                  </TableHead>
                  <TableHead aria-sort={sortKey === 'status' ? ariaSort : undefined}>
                    <SortButton label="Status" column="status" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead aria-sort={sortKey === 'compliance' ? ariaSort : undefined}>
                    <SortButton label="Compliance" column="compliance" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead className="text-right" aria-sort={sortKey === 'messages' ? ariaSort : undefined}>
                    <SortButton label="Messages" column="messages" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead className="text-right" aria-sort={sortKey === 'dkim' ? ariaSort : undefined}>
                    <SortButton label="DKIM" column="dkim" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead className="text-right" aria-sort={sortKey === 'spf' ? ariaSort : undefined}>
                    <SortButton label="SPF" column="spf" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead className="text-right" aria-sort={sortKey === 'sources' ? ariaSort : undefined}>
                    <SortButton label="Sources" column="sources" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead aria-sort={sortKey === 'lastReport' ? ariaSort : undefined}>
                    <SortButton label="Last report" column="lastReport" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                  </TableHead>
                  <TableHead>Active</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {sortedRows.map((row) => {
                  const meta = statusMeta[row.status]
                  const noData = row.status === 'no_data'
                  return (
                    <TableRow key={row.domainId}>
                      <TableCell>
                        <p className="font-medium">{row.name}</p>
                        {row.clientSlug === 'default' ? (
                          <Badge variant="warning" className="mt-0.5">
                            Default — needs client
                          </Badge>
                        ) : (
                          row.clientName && (
                            <p className="text-xs text-muted-foreground">{row.clientName}</p>
                          )
                        )}
                      </TableCell>
                      <TableCell>
                        <Badge variant={meta.badge}>{meta.label}</Badge>
                      </TableCell>
                      <TableCell>
                        <ComplianceMeter row={row} />
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {noData ? '—' : formatCompact(row.messages)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {noData ? '—' : formatPercent(row.dkimPassRate)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {noData ? '—' : formatPercent(row.spfPassRate)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {noData ? '—' : row.sources.toLocaleString('en-US')}
                      </TableCell>
                      <TableCell className="whitespace-nowrap text-muted-foreground">
                        {formatRelativeOrDate(row.lastReportEndUtc)}
                      </TableCell>
                      <TableCell>
                        <Badge variant={row.isActive ? 'success' : 'muted'}>
                          {row.isActive ? 'Active' : 'Inactive'}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-right">
                        <Button variant="outline" size="sm" onClick={() => openDomainDialog(row)}>
                          Edit
                        </Button>
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
            {sortedRows.length === 0 && !busy && (
              <p className="py-6 text-center text-sm text-muted-foreground">
                No domains found{search || clientFilter ? ' for the current filters' : ''}.
              </p>
            )}
          </div>
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingDomainId ? 'Edit Domain' : 'Create Domain'}</DialogTitle>
            <DialogDescription>Assign domain ownership and active state.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-3" onSubmit={createOrUpdateDomain}>
            <Input placeholder="Domain" value={domainForm.name} onChange={(e) => setDomainForm((x) => ({ ...x, name: e.target.value }))} required />
            <Select value={domainForm.clientId} onChange={(e) => setDomainForm((x) => ({ ...x, clientId: e.target.value }))} required>
              <option value="">Select client</option>
              {sortedClients.map((client) => (
                <option key={client.id} value={client.id}>{client.name}</option>
              ))}
            </Select>
            <label className="text-sm text-muted-foreground">
              <input className="mr-2" type="checkbox" checked={domainForm.isActive} onChange={(e) => setDomainForm((x) => ({ ...x, isActive: e.target.checked }))} />
              Active
            </label>
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="outline" onClick={resetDialog}>Cancel</Button>
              <Button type="submit">{editingDomainId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
