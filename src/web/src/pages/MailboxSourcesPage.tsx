import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

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
import { useSystemStatus } from '@/lib/use-system-status'

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

const getStatusBadgeVariant = (status: SyncRunStatus | null) => {
  if (status === 'success') return 'success' as const
  if (status === 'failed') return 'danger' as const
  if (status === 'running') return 'warning' as const
  return 'muted' as const
}

const formatWhen = (value: string | null) => {
  if (!value) return 'n/a'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function MailboxSourcesPage() {
  const status = useSystemStatus()
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [mailboxSources, setMailboxSources] = useState<MailboxSource[]>([])
  const [mailboxHealth, setMailboxHealth] = useState<MailboxHealth[]>([])
  const [syncRuns, setSyncRuns] = useState<MailboxSyncRun[]>([])

  const [search, setSearch] = useState('')
  const [mailboxOpsFilter, setMailboxOpsFilter] = useState<MailboxOpsFilter>('all')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

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

  return (
    <>
      <Card>
        <CardHeader>
          <div>
            <CardTitle>Operations Console</CardTitle>
            <CardDescription className="mt-1">{status}</CardDescription>
          </div>
          <div className="flex items-center gap-2">
            <Input
              placeholder="Search current list..."
              className="w-64"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
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
          <CardTitle>Mailbox Sources</CardTitle>
          <div className="flex items-center gap-3">
            <Badge variant="muted">{filteredMailboxSources.length} records</Badge>
            {canManage && (
              <Button onClick={() => openMailboxDialog()} disabled={busy}>
                New Mailbox
              </Button>
            )}
          </div>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Protocol</TableHead>
                <TableHead>Host</TableHead>
                <TableHead>Client</TableHead>
                <TableHead>Status</TableHead>
                {canManage && <TableHead className="text-right">Actions</TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {filteredMailboxSources.map((source) => (
                <TableRow key={source.id}>
                  <TableCell className="font-medium">{source.name}</TableCell>
                  <TableCell>{source.protocol.toUpperCase()}</TableCell>
                  <TableCell>
                    {source.host}:{source.port}
                  </TableCell>
                  <TableCell>{source.defaultClientName ?? source.defaultClientId}</TableCell>
                  <TableCell>
                    <Badge variant={source.isActive ? 'success' : 'muted'}>
                      {source.isActive ? 'Active' : 'Inactive'}
                    </Badge>
                  </TableCell>
                  {canManage && (
                    <TableCell className="text-right">
                      <Button variant="outline" size="sm" onClick={() => openMailboxDialog(source)}>
                        Edit
                      </Button>
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div>
              <CardTitle>Mailbox Health</CardTitle>
              <CardDescription>Operational view of sync outcomes, checkpoints, and latest issues.</CardDescription>
            </div>
            <Select
              className="w-56"
              value={mailboxOpsFilter}
              onChange={(e) => setMailboxOpsFilter(e.target.value as MailboxOpsFilter)}
            >
              <option value="all">All Mailboxes</option>
              <option value="failed">Failed Mailboxes</option>
              <option value="parse-failures">Parse Failures &gt; 0</option>
              <option value="stale-success">Stale Last Success (&gt;24h)</option>
            </Select>
          </div>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Mailbox</TableHead>
                <TableHead>Last Status</TableHead>
                <TableHead>Last Success</TableHead>
                <TableHead>Checkpoint UID</TableHead>
                <TableHead>Last Run Metrics</TableHead>
                <TableHead>Last Error</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filteredMailboxHealth.map((health) => (
                <TableRow key={health.mailboxSourceId}>
                  <TableCell className="font-medium">{health.name}</TableCell>
                  <TableCell>
                    <Badge variant={getStatusBadgeVariant(health.lastRunStatus)}>
                      {health.lastRunStatus ?? 'unknown'}
                    </Badge>
                  </TableCell>
                  <TableCell>{formatWhen(health.lastSuccessSyncAtUtc)}</TableCell>
                  <TableCell>{health.lastProcessedUid ?? 'n/a'}</TableCell>
                  <TableCell>
                    <div className="text-xs leading-5 text-muted-foreground">
                      <div>Scanned: {health.lastRunMessagesScanned ?? 0}</div>
                      <div>Attachments: {health.lastRunAttachmentsProcessed ?? 0}</div>
                      <div>Inserted: {health.lastRunReportsInserted ?? 0}</div>
                      <div>Dupes: {health.lastRunReportsSkippedAsDuplicate ?? 0}</div>
                      <div>Parse Failures: {health.lastRunParseFailures ?? 0}</div>
                    </div>
                  </TableCell>
                  <TableCell className="max-w-[420px]">
                    <p className="truncate text-xs text-muted-foreground" title={health.lastRunError ?? ''}>
                      {health.lastRunError ?? 'none'}
                    </p>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Recent Sync Runs</CardTitle>
          <CardDescription>Last three runs per mailbox source.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {filteredMailboxSourcesForOps.map((source) => {
            const runs = recentSyncRunsByMailbox.get(source.id) ?? []
            return (
              <div key={source.id} className="rounded-lg border border-border p-3">
                <div className="mb-2 flex items-center justify-between">
                  <p className="text-sm font-medium">{source.name}</p>
                  <p className="text-xs text-muted-foreground">{source.host}:{source.port}</p>
                </div>
                {runs.length === 0 ? (
                  <p className="text-xs text-muted-foreground">No sync runs yet.</p>
                ) : (
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
                      {runs.map((run) => (
                        <TableRow key={run.id}>
                          <TableCell>
                            <Badge variant={getStatusBadgeVariant(run.status)}>{run.status}</Badge>
                          </TableCell>
                          <TableCell>{formatWhen(run.startedAtUtc)}</TableCell>
                          <TableCell>{formatWhen(run.finishedAtUtc)}</TableCell>
                          <TableCell>
                            <span className="text-xs text-muted-foreground">
                              {run.messagesScanned}/{run.attachmentsProcessed}/{run.reportsInserted}/{run.reportsSkippedAsDuplicate}/{run.parseFailures}
                            </span>
                          </TableCell>
                          <TableCell className="max-w-[260px]">
                            <p className="truncate text-xs text-muted-foreground" title={run.error ?? ''}>
                              {run.error ?? 'none'}
                            </p>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </div>
            )
          })}
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingMailboxId ? 'Edit Mailbox Source' : 'Create Mailbox Source'}</DialogTitle>
            <DialogDescription>Configure mailbox transport and default routing client.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-3" onSubmit={createOrUpdateMailboxSource}>
            <Input placeholder="Source name" value={mailboxForm.name} onChange={(e) => setMailboxForm((x) => ({ ...x, name: e.target.value }))} required />
            <Select value={mailboxForm.protocol} onChange={(e) => setMailboxForm((x) => ({ ...x, protocol: e.target.value as 'imap' | 'pop3' }))}>
              <option value="imap">IMAP</option>
              <option value="pop3">POP3</option>
            </Select>
            <Input placeholder="Host" value={mailboxForm.host} onChange={(e) => setMailboxForm((x) => ({ ...x, host: e.target.value }))} required />
            <Input type="number" min={1} value={mailboxForm.port} onChange={(e) => setMailboxForm((x) => ({ ...x, port: Number(e.target.value || 993) }))} required />
            <Input placeholder="Username" value={mailboxForm.username} onChange={(e) => setMailboxForm((x) => ({ ...x, username: e.target.value }))} required />
            <Input type="password" placeholder={editingMailboxId ? 'New password (optional)' : 'Password'} value={mailboxForm.password} onChange={(e) => setMailboxForm((x) => ({ ...x, password: e.target.value }))} required={!editingMailboxId} />
            <Select value={mailboxForm.defaultClientId} onChange={(e) => setMailboxForm((x) => ({ ...x, defaultClientId: e.target.value }))} required>
              <option value="">Select default client</option>
              {sortedClients.map((client) => (
                <option key={client.id} value={client.id}>{client.name}</option>
              ))}
            </Select>
            <label className="text-sm text-muted-foreground">
              <input className="mr-2" type="checkbox" checked={mailboxForm.useTls} onChange={(e) => setMailboxForm((x) => ({ ...x, useTls: e.target.checked }))} />
              Use TLS
            </label>
            <label className="text-sm text-muted-foreground">
              <input className="mr-2" type="checkbox" checked={mailboxForm.isActive} onChange={(e) => setMailboxForm((x) => ({ ...x, isActive: e.target.checked }))} />
              Active
            </label>
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="outline" onClick={resetDialog}>Cancel</Button>
              <Button type="submit">{editingMailboxId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
