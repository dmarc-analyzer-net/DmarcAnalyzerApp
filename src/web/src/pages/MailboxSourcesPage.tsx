import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
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
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type {
  Client,
  MailboxHealth,
  MailboxSource,
  MailboxSyncRun,
  SyncRunStatus,
} from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'

type MailboxOpsFilter = 'all' | 'failed' | 'parse-failures' | 'stale-success'

const initialMailboxForm = {
  name: '',
  protocol: 'imap' as 'imap' | 'pop3',
  host: '',
  port: 993,
  useTls: true,
  username: '',
  password: '',
  defaultClientId: '',
  isActive: true,
}

/** Status pill in the sources table: healthy/running/failing (health-driven). */
const getHealthBadge = (
  status: SyncRunStatus | null | undefined,
): { label: string; variant: 'success' | 'warning' | 'danger' | 'neutral' } => {
  if (status === 'success') return { label: 'Healthy', variant: 'success' }
  if (status === 'running') return { label: 'Running', variant: 'warning' }
  if (status === 'failed') return { label: 'Failing', variant: 'danger' }
  return { label: 'No data', variant: 'neutral' }
}

/** Raw status pill used in the health + sync-run detail tables. */
const getStatusBadgeVariant = (status: SyncRunStatus | null) => {
  if (status === 'success') return 'success' as const
  if (status === 'failed') return 'danger' as const
  if (status === 'running') return 'warning' as const
  return 'neutral' as const
}

const numOrDash = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('en-US')

const formatWhen = (value: string | null) => {
  if (!value) return 'n/a'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function MailboxSourcesPage() {
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [mailboxSources, setMailboxSources] = useState<MailboxSource[]>([])
  const [mailboxHealth, setMailboxHealth] = useState<MailboxHealth[]>([])
  const [syncRuns, setSyncRuns] = useState<MailboxSyncRun[]>([])

  const [search, setSearch] = useState('')
  const [mailboxOpsFilter, setMailboxOpsFilter] = useState<MailboxOpsFilter>('all')
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [syncingId, setSyncingId] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingMailboxId, setEditingMailboxId] = useState<string | null>(null)
  const [mailboxForm, setMailboxForm] = useState(initialMailboxForm)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, mailboxData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<MailboxSource[]>('/api/v1/mailbox-sources'),
      ])

      const [healthData, syncRunData] = await Promise.all([
        fetchJson<MailboxHealth[]>('/api/v1/mailbox-health'),
        fetchJson<MailboxSyncRun[]>('/api/v1/mailbox-sync-runs?limit=40'),
      ])

      setClients(clientData)
      setMailboxSources(mailboxData)
      setMailboxHealth(healthData)
      setSyncRuns(syncRunData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load data')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void loadData()
  }, [loadData])

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  )

  const healthBySourceId = useMemo(
    () => new Map(mailboxHealth.map((health) => [health.mailboxSourceId, health])),
    [mailboxHealth],
  )

  const sourceById = useMemo(
    () => new Map(mailboxSources.map((source) => [source.id, source])),
    [mailboxSources],
  )

  const filteredMailboxSources = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return mailboxSources
    return mailboxSources.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        x.host.toLowerCase().includes(q) ||
        x.username.toLowerCase().includes(q),
    )
  }, [search, mailboxSources])

  const failingMailboxes = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'failed'),
    [mailboxHealth],
  )

  const healthyCount = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'success').length,
    [mailboxHealth],
  )

  const filteredMailboxHealth = useMemo(() => {
    const now = Date.now()
    const staleThresholdMs = 24 * 60 * 60 * 1000

    return mailboxHealth.filter((health) => {
      if (mailboxOpsFilter === 'failed') {
        return health.lastRunStatus === 'failed'
      }

      if (mailboxOpsFilter === 'parse-failures') {
        return (health.lastRunParseFailures ?? 0) > 0
      }

      if (mailboxOpsFilter === 'stale-success') {
        if (!health.lastSuccessSyncAtUtc) return true
        const lastSuccessMs = new Date(health.lastSuccessSyncAtUtc).getTime()
        if (Number.isNaN(lastSuccessMs)) return true
        return now - lastSuccessMs > staleThresholdMs
      }

      return true
    })
  }, [mailboxHealth, mailboxOpsFilter])

  const filteredMailboxSourcesForOps = useMemo(() => {
    const ids = new Set(filteredMailboxHealth.map((x) => x.mailboxSourceId))
    return filteredMailboxSources.filter((source) => ids.has(source.id))
  }, [filteredMailboxSources, filteredMailboxHealth])

  const recentSyncRunsByMailbox = useMemo(() => {
    const grouped = new Map<string, MailboxSyncRun[]>()
    for (const run of syncRuns) {
      const current = grouped.get(run.mailboxSourceId) ?? []
      if (current.length < 3) {
        current.push(run)
        grouped.set(run.mailboxSourceId, current)
      }
    }

    return grouped
  }, [syncRuns])

  const resetDialog = () => {
    setDialogOpen(false)
    setEditingMailboxId(null)
    setMailboxForm((x) => ({ ...initialMailboxForm, defaultClientId: x.defaultClientId }))
    setError(null)
  }

  const openMailboxDialog = (source?: MailboxSource) => {
    setError(null)
    setDialogOpen(true)
    if (source) {
      setEditingMailboxId(source.id)
      setMailboxForm({
        name: source.name,
        protocol: source.protocol,
        host: source.host,
        port: source.port,
        useTls: source.useTls,
        username: source.username,
        password: '',
        defaultClientId: source.defaultClientId,
        isActive: source.isActive,
      })
    } else {
      setEditingMailboxId(null)
      setMailboxForm({
        ...initialMailboxForm,
        defaultClientId: sortedClients[0]?.id ?? '',
      })
    }
  }

  const createOrUpdateMailboxSource = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      const payload = { ...mailboxForm }
      if (editingMailboxId && !payload.password) {
        delete (payload as { password?: string }).password
      }

      if (editingMailboxId) {
        await fetchJson(`/api/v1/mailbox-sources/${editingMailboxId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      } else {
        await fetchJson('/api/v1/mailbox-sources', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save mailbox source')
    }
  }

  const syncNow = async (id: string) => {
    setSyncingId(id)
    setError(null)
    try {
      await fetchJson(`/api/v1/mailbox-sources/${id}/sync`, { method: 'POST' })
      await loadData()
    } catch (syncError) {
      setError(syncError instanceof Error ? syncError.message : 'Failed to sync mailbox')
    } finally {
      setSyncingId(null)
    }
  }

  const lastSyncLabel = (health: MailboxHealth | undefined) => {
    if (health?.lastRunStatus === 'running') return 'Running now'
    return formatRelativeOrDate(health?.lastSuccessSyncAtUtc ?? null)
  }

  const count = mailboxSources.length
  const subtitle = `${count} ${count === 1 ? 'mailbox' : 'mailboxes'} · ${healthyCount}/${count} healthy`

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Mailbox sources</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex shrink-0 items-center gap-2.5">
          <Input
            icon="search"
            placeholder="Search mailboxes"
            className="w-56"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          {canManage && (
            <Button icon="plus" onClick={() => openMailboxDialog()}>
              Add mailbox
            </Button>
          )}
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {busy && mailboxSources.length === 0 ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : (
        <>
          <Card pad={false} className="overflow-hidden">
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Mailbox</TableHead>
                    <TableHead>Protocol</TableHead>
                    <TableHead>Host</TableHead>
                    <TableHead>Last sync</TableHead>
                    <TableHead className="text-right">Scanned</TableHead>
                    <TableHead className="text-right">Inserted</TableHead>
                    <TableHead className="text-right">Status</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredMailboxSources.map((source, index) => {
                    const health = healthBySourceId.get(source.id)
                    const badge = source.isActive
                      ? getHealthBadge(health?.lastRunStatus)
                      : { label: 'Inactive', variant: 'neutral' as const }
                    const isSyncing = syncingId === source.id
                    return (
                      <TableRow key={source.id} last={index === filteredMailboxSources.length - 1}>
                        <TableCell mono>{source.name}</TableCell>
                        <TableCell mono>
                          {source.protocol}:{source.port}
                        </TableCell>
                        <TableCell mono>{source.host}</TableCell>
                        <TableCell>
                          <span className="text-sm text-secondary">{lastSyncLabel(health)}</span>
                        </TableCell>
                        <TableCell mono align="right">
                          {numOrDash(health?.lastRunMessagesScanned)}
                        </TableCell>
                        <TableCell mono align="right">
                          {numOrDash(health?.lastRunReportsInserted)}
                        </TableCell>
                        <TableCell align="right">
                          <Badge variant={badge.variant} dot>
                            {badge.label}
                          </Badge>
                        </TableCell>
                        <TableCell align="right">
                          <Button
                            variant="secondary"
                            size="sm"
                            disabled={isSyncing}
                            onClick={() => void syncNow(source.id)}
                          >
                            <Icon
                              name="refresh-cw"
                              size={14}
                              className={isSyncing ? 'animate-spin' : undefined}
                            />
                            {isSyncing ? 'Syncing' : 'Sync now'}
                          </Button>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
            {filteredMailboxSources.length === 0 ? (
              <p className="px-5 py-10 text-center text-sm text-secondary">
                No mailbox sources found{search ? ' for the current search' : ''}.
              </p>
            ) : null}
          </Card>

          {failingMailboxes.length > 0 ? (
            <div className="mt-3 space-y-1.5">
              {failingMailboxes.map((health) => {
                const source = sourceById.get(health.mailboxSourceId)
                return (
                  <div
                    key={health.mailboxSourceId}
                    className="flex items-center gap-2 text-sm text-secondary"
                  >
                    <span className="inline-flex shrink-0 text-[var(--status-danger-dot)]">
                      <Icon name="circle-alert" size={15} />
                    </span>
                    <span className="font-mono text-xs">{source?.host ?? health.name}</span>
                    <span>failed —</span>
                    <span className="min-w-0 truncate font-mono text-xs" title={health.lastRunError ?? ''}>
                      {health.lastRunError ?? 'unknown error'}
                    </span>
                  </div>
                )
              })}
            </div>
          ) : null}

          <Card pad={false} className="mt-3.5 overflow-hidden">
            <div className="px-5 pt-4 pb-2">
              <CardHeader
                title="Mailbox health"
                description="Operational view of sync outcomes, checkpoints, and latest issues."
                actions={
                  <Select
                    className="w-56"
                    value={mailboxOpsFilter}
                    onChange={(e) => setMailboxOpsFilter(e.target.value as MailboxOpsFilter)}
                  >
                    <option value="all">All mailboxes</option>
                    <option value="failed">Failed mailboxes</option>
                    <option value="parse-failures">Parse failures &gt; 0</option>
                    <option value="stale-success">Stale last success (&gt;24h)</option>
                  </Select>
                }
              />
            </div>
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Mailbox</TableHead>
                    <TableHead>Last status</TableHead>
                    <TableHead>Last success</TableHead>
                    <TableHead>Checkpoint UID</TableHead>
                    <TableHead>Last run metrics</TableHead>
                    <TableHead>Last error</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredMailboxHealth.map((health, index) => (
                    <TableRow
                      key={health.mailboxSourceId}
                      last={index === filteredMailboxHealth.length - 1}
                    >
                      <TableCell className="font-semibold">{health.name}</TableCell>
                      <TableCell>
                        <Badge variant={getStatusBadgeVariant(health.lastRunStatus)}>
                          {health.lastRunStatus ?? 'unknown'}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <span className="text-sm text-secondary">
                          {formatWhen(health.lastSuccessSyncAtUtc)}
                        </span>
                      </TableCell>
                      <TableCell mono>{health.lastProcessedUid ?? 'n/a'}</TableCell>
                      <TableCell>
                        <div className="text-xs leading-5 text-secondary">
                          <div>Scanned: {health.lastRunMessagesScanned ?? 0}</div>
                          <div>Attachments: {health.lastRunAttachmentsProcessed ?? 0}</div>
                          <div>Inserted: {health.lastRunReportsInserted ?? 0}</div>
                          <div>Dupes: {health.lastRunReportsSkippedAsDuplicate ?? 0}</div>
                          <div>Parse failures: {health.lastRunParseFailures ?? 0}</div>
                        </div>
                      </TableCell>
                      <TableCell className="max-w-[420px]">
                        <p
                          className="truncate text-xs text-secondary"
                          title={health.lastRunError ?? ''}
                        >
                          {health.lastRunError ?? 'none'}
                        </p>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            {filteredMailboxHealth.length === 0 ? (
              <p className="px-5 py-10 text-center text-sm text-secondary">
                No mailboxes match the selected filter.
              </p>
            ) : null}
          </Card>

          <Card pad={false} className="mt-3.5">
            <div className="px-5 pt-4 pb-2">
              <CardHeader title="Recent sync runs" description="Last three runs per mailbox source." />
            </div>
            <CardContent className="space-y-4 pt-2">
              {filteredMailboxSourcesForOps.length === 0 ? (
                <p className="text-sm text-secondary">No sync runs to show for the selected filter.</p>
              ) : (
                filteredMailboxSourcesForOps.map((source) => {
                  const runs = recentSyncRunsByMailbox.get(source.id) ?? []
                  return (
                    <div key={source.id} className="rounded-md border border-border p-3">
                      <div className="mb-2 flex items-center justify-between gap-3">
                        <p className="text-sm font-semibold text-body">{source.name}</p>
                        <p className="font-mono text-xs text-secondary">
                          {source.host}:{source.port}
                        </p>
                      </div>
                      {runs.length === 0 ? (
                        <p className="text-xs text-secondary">No sync runs yet.</p>
                      ) : (
                        <div className="overflow-x-auto">
                          <Table>
                            <TableHeader>
                              <TableRow>
                                <TableHead>Status</TableHead>
                                <TableHead>Started</TableHead>
                                <TableHead>Finished</TableHead>
                                <TableHead>Counts</TableHead>
                                <TableHead>Error</TableHead>
                              </TableRow>
                            </TableHeader>
                            <TableBody>
                              {runs.map((run, index) => (
                                <TableRow key={run.id} last={index === runs.length - 1}>
                                  <TableCell>
                                    <Badge variant={getStatusBadgeVariant(run.status)}>
                                      {run.status}
                                    </Badge>
                                  </TableCell>
                                  <TableCell>
                                    <span className="text-sm text-secondary">
                                      {formatWhen(run.startedAtUtc)}
                                    </span>
                                  </TableCell>
                                  <TableCell>
                                    <span className="text-sm text-secondary">
                                      {formatWhen(run.finishedAtUtc)}
                                    </span>
                                  </TableCell>
                                  <TableCell mono>
                                    {run.messagesScanned}/{run.attachmentsProcessed}/
                                    {run.reportsInserted}/{run.reportsSkippedAsDuplicate}/
                                    {run.parseFailures}
                                  </TableCell>
                                  <TableCell className="max-w-[260px]">
                                    <p
                                      className="truncate text-xs text-secondary"
                                      title={run.error ?? ''}
                                    >
                                      {run.error ?? 'none'}
                                    </p>
                                  </TableCell>
                                </TableRow>
                              ))}
                            </TableBody>
                          </Table>
                        </div>
                      )}
                    </div>
                  )
                })
              )}
            </CardContent>
          </Card>
        </>
      )}

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingMailboxId ? 'Edit mailbox source' : 'Add mailbox source'}</DialogTitle>
            <DialogDescription>Configure mailbox transport and default routing client.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={createOrUpdateMailboxSource}>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Source name
              <Input
                value={mailboxForm.name}
                onChange={(e) => setMailboxForm((x) => ({ ...x, name: e.target.value }))}
                required
              />
            </label>
            <div className="grid grid-cols-2 gap-4">
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Protocol
                <Select
                  value={mailboxForm.protocol}
                  onChange={(e) =>
                    setMailboxForm((x) => ({ ...x, protocol: e.target.value as 'imap' | 'pop3' }))
                  }
                >
                  <option value="imap">IMAP</option>
                  <option value="pop3">POP3</option>
                </Select>
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Port
                <Input
                  type="number"
                  min={1}
                  mono
                  value={mailboxForm.port}
                  onChange={(e) =>
                    setMailboxForm((x) => ({ ...x, port: Number(e.target.value || 993) }))
                  }
                  required
                />
              </label>
            </div>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Host
              <Input
                mono
                value={mailboxForm.host}
                onChange={(e) => setMailboxForm((x) => ({ ...x, host: e.target.value }))}
                required
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Username
              <Input
                value={mailboxForm.username}
                onChange={(e) => setMailboxForm((x) => ({ ...x, username: e.target.value }))}
                required
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              {editingMailboxId ? 'New password (optional)' : 'Password'}
              <Input
                type="password"
                value={mailboxForm.password}
                onChange={(e) => setMailboxForm((x) => ({ ...x, password: e.target.value }))}
                required={!editingMailboxId}
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Default client
              <Select
                value={mailboxForm.defaultClientId}
                onChange={(e) => setMailboxForm((x) => ({ ...x, defaultClientId: e.target.value }))}
                required
              >
                <option value="">Select default client</option>
                {sortedClients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </Select>
            </label>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.useTls}
                onChange={(e) => setMailboxForm((x) => ({ ...x, useTls: e.target.checked }))}
              />
              Use TLS
            </label>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.isActive}
                onChange={(e) => setMailboxForm((x) => ({ ...x, isActive: e.target.checked }))}
              />
              Active
            </label>
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="secondary" onClick={resetDialog}>
                Cancel
              </Button>
              <Button type="submit">{editingMailboxId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
