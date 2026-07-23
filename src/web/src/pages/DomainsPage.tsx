import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'

import { ComplianceBar } from '@/components/data/ComplianceBar'
import { PolicyBadge } from '@/components/data/PolicyBadge'
import { SortHeader, type SortDir } from '@/components/data/SortHeader'
import { DaysSelector } from '@/components/DaysSelector'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  ENFORCEMENT_STATUS_META,
  parseAnalyticsDays,
  type AnalyticsDays,
  type DmarcPublishedPolicy,
  type DomainAnalytics,
  type EnforcementStatus,
} from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type { Client, Domain } from '@/lib/entities'
import { formatCompact } from '@/lib/format'
import { cn } from '@/lib/utils'

const initialDomainForm = {
  name: '',
  clientId: '',
  isActive: true,
}

const POLICY_FILTER_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'Any policy' },
  { value: 'reject', label: 'p=reject' },
  { value: 'quarantine', label: 'p=quarantine' },
  { value: 'none', label: 'p=none' },
]

type DomainRow = Omit<DomainAnalytics, 'clientName' | 'clientSlug'> & {
  clientName: string | null
  clientSlug: string | null
  crud: Domain | null
}

type SortKey = 'name' | 'client' | 'policy' | 'compliance' | 'messages' | 'status'

/** Direction applied when a column first becomes the active sort. */
const defaultSortDir: Record<SortKey, SortDir> = {
  name: 'asc',
  client: 'asc',
  policy: 'desc',
  compliance: 'asc',
  messages: 'desc',
  status: 'asc',
}

/** Metric columns render "—" for no-data rows, so those rows always sort last. */
const metricSortKeys: ReadonlySet<SortKey> = new Set(['compliance', 'messages'])

/** Published-policy ordering (strength). Unknown policy sorts as `none`. */
const policyRank: Record<DmarcPublishedPolicy, number> = { none: 0, quarantine: 1, reject: 2 }

/** Enforcement posture ordering, worst first; no-data last. */
const statusRank: Record<EnforcementStatus, number> = {
  spoofing: 0,
  ramping: 1,
  monitoring: 2,
  enforced: 3,
  no_data: 4,
}

function compareRows(a: DomainRow, b: DomainRow, key: SortKey, dir: SortDir): number {
  const flip = dir === 'asc' ? 1 : -1

  const aNoData = a.enforcementStatus === 'no_data'
  const bNoData = b.enforcementStatus === 'no_data'
  if (metricSortKeys.has(key) && aNoData !== bNoData) {
    return aNoData ? 1 : -1
  }

  let cmp = 0
  switch (key) {
    case 'name':
      cmp = a.name.localeCompare(b.name)
      break
    case 'client': {
      const aName = a.clientName ?? ''
      const bName = b.clientName ?? ''
      if (!aName !== !bName) return aName ? -1 : 1
      cmp = aName.localeCompare(bName)
      break
    }
    case 'policy':
      cmp = policyRank[a.publishedPolicy ?? 'none'] - policyRank[b.publishedPolicy ?? 'none']
      break
    case 'compliance':
      cmp = a.complianceRate - b.complianceRate
      break
    case 'messages':
      cmp = a.messages - b.messages
      break
    case 'status':
      cmp = statusRank[a.enforcementStatus] - statusRank[b.enforcementStatus]
      break
  }
  if (cmp !== 0) return cmp * flip

  // Stable tie-breakers: higher volume first, then name.
  if (a.messages !== b.messages) return b.messages - a.messages
  return a.name.localeCompare(b.name)
}

export function DomainsPage() {
  const { user } = useAuth()
  const canManage = isAdmin(user)
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const days = parseAnalyticsDays(searchParams.get('days'))
  const clientFilter = searchParams.get('client') ?? ''

  const [clients, setClients] = useState<Client[]>([])
  const [domains, setDomains] = useState<Domain[]>([])
  const [analytics, setAnalytics] = useState<DomainAnalytics[]>([])
  const [search, setSearch] = useState('')
  const [policyFilter, setPolicyFilter] = useState('')
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
        publishedPolicy: null,
        subdomainPolicy: null,
        publishedPct: null,
        dkimAlignment: null,
        spfAlignment: null,
        enforcementStatus: 'no_data',
        crud: domain,
      })
    }

    return merged
  }, [analytics, domains, clients])

  const clientCount = useMemo(
    () => new Set(rows.map((row) => row.clientId)).size,
    [rows],
  )

  const filteredRows = useMemo(() => {
    const q = search.toLowerCase().trim()
    return rows.filter((row) => {
      if (clientFilter && row.clientId !== clientFilter) return false
      if (policyFilter && (row.publishedPolicy ?? 'none') !== policyFilter) return false
      if (!q) return true
      return row.name.toLowerCase().includes(q) || (row.clientName ?? '').toLowerCase().includes(q)
    })
  }, [search, clientFilter, policyFilter, rows])

  // Default: worst compliance first, no_data last; ties broken by volume, then name.
  const sortedRows = useMemo(
    () => [...filteredRows].sort((a, b) => compareRows(a, b, sortKey, sortDir)),
    [filteredRows, sortKey, sortDir],
  )

  // Carries the current window and client filter into the drill-down page (and its back link).
  const detailSearch = useMemo(() => {
    const params = new URLSearchParams()
    if (days !== 30) params.set('days', String(days))
    if (clientFilter) params.set('client', clientFilter)
    const query = params.toString()
    return query ? `?${query}` : ''
  }, [days, clientFilter])

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

  const subtitle = `${rows.length} ${rows.length === 1 ? 'domain' : 'domains'} across ${clientCount} ${
    clientCount === 1 ? 'client' : 'clients'
  }`

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Domains</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex shrink-0 items-center gap-2.5">
          <DaysSelector value={days} onChange={setDays} disabled={busy} />
          {canManage ? (
            <Button icon="plus" onClick={() => openDomainDialog()}>
              Add domain
            </Button>
          ) : null}
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      <div className="mb-3.5 grid grid-cols-1 gap-2.5 sm:grid-cols-[minmax(0,1fr)_12rem_11rem]">
        <Input
          icon="search"
          placeholder="Filter by domain or client…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <Select
          aria-label="Filter by client"
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
        <Select
          aria-label="Filter by policy"
          options={POLICY_FILTER_OPTIONS}
          value={policyFilter}
          onChange={(e) => setPolicyFilter(e.target.value)}
        />
      </div>

      <Card>
        <div className={cn('overflow-x-auto transition-opacity', busy && 'opacity-60')}>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead aria-sort={sortKey === 'name' ? ariaSort : undefined}>
                  <SortHeader label="Domain" column="name" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                <TableHead aria-sort={sortKey === 'client' ? ariaSort : undefined}>
                  <SortHeader label="Client" column="client" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                <TableHead aria-sort={sortKey === 'policy' ? ariaSort : undefined}>
                  <SortHeader label="Policy" column="policy" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                <TableHead aria-sort={sortKey === 'compliance' ? ariaSort : undefined}>
                  <SortHeader label="Compliance" column="compliance" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                <TableHead className="text-right" aria-sort={sortKey === 'messages' ? ariaSort : undefined}>
                  <SortHeader label={`Volume ${days}d`} column="messages" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                <TableHead className="text-right" aria-sort={sortKey === 'status' ? ariaSort : undefined}>
                  <SortHeader label="Status" column="status" sortKey={sortKey} sortDir={sortDir} onSort={handleSort} />
                </TableHead>
                {canManage ? <TableHead className="text-right">Actions</TableHead> : null}
              </TableRow>
            </TableHeader>
            <TableBody>
              {sortedRows.map((row) => {
                const meta = ENFORCEMENT_STATUS_META[row.enforcementStatus]
                const noData = row.enforcementStatus === 'no_data'
                return (
                  <TableRow
                    key={row.domainId}
                    onClick={() => navigate(`/domains/${row.domainId}${detailSearch}`)}
                  >
                    <TableCell>
                      <span className="inline-flex items-center gap-2.5">
                        <span
                          className="h-2 w-2 shrink-0 rounded-full"
                          style={{ background: meta.dot }}
                          aria-hidden
                        />
                        <span className="font-mono text-sm text-body">{row.name}</span>
                      </span>
                    </TableCell>
                    <TableCell>
                      {row.clientSlug === 'default' ? (
                        <Badge variant="warning">Default — needs client</Badge>
                      ) : (
                        <span className="text-sm text-secondary">{row.clientName ?? '—'}</span>
                      )}
                    </TableCell>
                    <TableCell>
                      <PolicyBadge policy={row.publishedPolicy ?? 'none'} />
                    </TableCell>
                    <TableCell>
                      {noData ? (
                        <span className="text-faint">—</span>
                      ) : (
                        <ComplianceBar value={+(row.complianceRate * 100).toFixed(1)} width={130} />
                      )}
                    </TableCell>
                    <TableCell mono align="right">
                      {noData ? <span className="text-faint">—</span> : formatCompact(row.messages)}
                    </TableCell>
                    <TableCell align="right">
                      <Badge variant={meta.badge}>{meta.label}</Badge>
                    </TableCell>
                    {canManage ? (
                      <TableCell align="right">
                        <Button
                          variant="ghost"
                          size="sm"
                          icon="pencil"
                          onClick={(event) => {
                            event.stopPropagation()
                            openDomainDialog(row)
                          }}
                        >
                          Edit
                        </Button>
                      </TableCell>
                    ) : null}
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
          {sortedRows.length === 0 && !busy ? (
            <div className="flex flex-col items-center gap-2 px-5 py-14 text-center">
              <Icon name="globe" size={32} className="text-faint" />
              <p className="text-sm text-secondary">
                No domains found
                {search || clientFilter || policyFilter ? ' for the current filters' : ''}.
              </p>
            </div>
          ) : null}
        </div>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingDomainId ? 'Edit domain' : 'Create domain'}</DialogTitle>
            <DialogDescription>Assign domain ownership and active state.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-3" onSubmit={createOrUpdateDomain}>
            <Input
              mono
              placeholder="Domain"
              value={domainForm.name}
              onChange={(e) => setDomainForm((x) => ({ ...x, name: e.target.value }))}
              required
            />
            <Select
              value={domainForm.clientId}
              onChange={(e) => setDomainForm((x) => ({ ...x, clientId: e.target.value }))}
              required
            >
              <option value="">Select client</option>
              {sortedClients.map((client) => (
                <option key={client.id} value={client.id}>
                  {client.name}
                </option>
              ))}
            </Select>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={domainForm.isActive}
                onChange={(e) => setDomainForm((x) => ({ ...x, isActive: e.target.checked }))}
              />
              Active
            </label>
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="secondary" onClick={resetDialog}>
                Cancel
              </Button>
              <Button type="submit">{editingDomainId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
