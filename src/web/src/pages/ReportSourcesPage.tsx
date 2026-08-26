import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

import { ApiCredentialsCard } from '@/components/ApiCredentialsCard'
import { Notice } from '@/components/Notice'
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
  ReportSource,
  MailboxSyncRun,
  SyncRunStatus,
} from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'
import { usePageTitle } from '@/lib/use-page-title'

type MailboxOpsFilter = 'all' | 'failed' | 'parse-failures' | 'stale-success'

type Protocol = 'imap' | 'pop3' | 's3' | 'api'

/**
 * What each protocol listens on by default, so choosing one does not leave the previous
 * protocol's port behind — 993 on a POP3 mailbox connects to nothing and fails at sync time,
 * which is a long way from where the mistake was made. Zero for the two that have no port.
 */
const defaultPort: Record<Protocol, number> = { imap: 993, pop3: 995, s3: 0, api: 0 }

const initialMailboxForm = {
  name: '',
  protocol: 'imap' as Protocol,
  host: '',
  port: defaultPort.imap,
  useTls: true,
  username: '',
  password: '',
  defaultClientId: '',
  isActive: true,
  deleteAfterRetention: false,
  allowForeignDomains: true,
  s3Bucket: '',
  s3Prefix: '',
  s3Region: '',
  s3Endpoint: '',
  s3ForcePathStyle: true,
}

/**
 * Whether the worker goes and fetches from this source, and therefore whether sync health
 * says anything about it. `api` sources are written to by their caller: no sync run, no
 * checkpoint, nothing to be healthy or unhealthy about.
 */
const sourceIsPolled = (source: Pick<ReportSource, 'protocol'>) => source.protocol !== 'api'

/**
 * Whether it is a mailbox specifically — reached over a host and a port with a login.
 * <p>
 * Narrower than polled since S3 arrived, and the two were one predicate until then. Keeping
 * them apart is what stops a bucket being rendered as `s3:0` or asked for a hostname: it is
 * polled like a mailbox and addressed nothing like one.
 */
const sourceHasMailbox = (source: Pick<ReportSource, 'protocol'>) =>
  source.protocol === 'imap' || source.protocol === 'pop3'

/** Where the reports come from, in the terms that protocol uses. */
const sourceLocation = (source: ReportSource) => {
  if (sourceHasMailbox(source)) return source.host || '—'
  if (source.protocol === 's3') {
    return source.s3Bucket ? `${source.s3Bucket}/${source.s3Prefix ?? ''}` : '—'
  }
  return '—'
}

/**
 * Status pill for a source that is never polled, where sync health cannot say anything.
 * <p>
 * `api` is working as designed and simply has nothing to sync, so it says so. The fallback
 * is for a protocol this build does not poll — there is none today, now that `pop3` is
 * implemented — and it is deliberately alarming rather than neutral, because a source that
 * is neither pushed nor polled ingests nothing and looks identical to an empty mailbox.
 */
const getUnpolledBadge = (
  protocol: string,
): { label: string; variant: 'success' | 'warning' | 'danger' | 'neutral' } =>
  protocol === 'api'
    ? { label: 'Pushed', variant: 'neutral' }
    : { label: 'Not polled', variant: 'warning' }

/** Status pill in the sources table: healthy/running/failing (health-driven). */
const getHealthBadge = (
  status: SyncRunStatus | null | undefined,
): { label: string; variant: 'success' | 'warning' | 'danger' | 'neutral' } => {
  if (status === 'success') return { label: 'Healthy', variant: 'success' }
  if (status === 'running') return { label: 'Running', variant: 'warning' }
  if (status === 'partial') return { label: 'Catching up', variant: 'warning' }
  if (status === 'failed') return { label: 'Failing', variant: 'danger' }
  return { label: 'No data', variant: 'neutral' }
}

/** Raw status pill used in the health + sync-run detail tables. */
const getStatusBadgeVariant = (status: SyncRunStatus | null) => {
  if (status === 'success') return 'success' as const
  if (status === 'failed') return 'danger' as const
  if (status === 'running' || status === 'partial') return 'warning' as const
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

export function ReportSourcesPage() {
  usePageTitle('Report sources')
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [reportSources, setReportSources] = useState<ReportSource[]>([])
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
  // Three shapes, not two, since S3 arrived. A pushed source has nothing to describe; a
  // bucket has a bucket and a region where a mailbox has a host and a port, and its
  // credential is optional because an instance role can supply it. The API refuses the
  // fields that do not belong outright, so the form has to send exactly the right set.
  const isPushedSource = mailboxForm.protocol === 'api'
  const isBucketSource = mailboxForm.protocol === 's3'
  const isMailboxSource = !isPushedSource && !isBucketSource

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, mailboxData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<ReportSource[]>('/api/v1/report-sources'),
      ])

      const [healthData, syncRunData] = await Promise.all([
        fetchJson<MailboxHealth[]>('/api/v1/mailbox-health'),
        fetchJson<MailboxSyncRun[]>('/api/v1/mailbox-sync-runs?limit=40'),
      ])

      setClients(clientData)
      setReportSources(mailboxData)
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
    () => new Map(mailboxHealth.map((health) => [health.reportSourceId, health])),
    [mailboxHealth],
  )

  const sourceById = useMemo(
    () => new Map(reportSources.map((source) => [source.id, source])),
    [reportSources],
  )

  const filteredReportSources = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return reportSources
    return reportSources.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        x.host.toLowerCase().includes(q) ||
        x.username.toLowerCase().includes(q),
    )
  }, [search, reportSources])

  const failingMailboxes = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'failed'),
    [mailboxHealth],
  )

  const healthyCount = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'success').length,
    [mailboxHealth],
  )

  // Only a polled source can be counted against sync health. Counting every source made an
  // install with nothing but pushed sources read "0/N healthy" forever, while the health card
  // below it — which filters on the same thing the API does — correctly showed nothing at all.
  const mailboxSourceCount = useMemo(
    () => reportSources.filter((source) => sourceIsPolled(source)).length,
    [reportSources],
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

  const filteredReportSourcesForOps = useMemo(() => {
    const ids = new Set(filteredMailboxHealth.map((x) => x.reportSourceId))
    return filteredReportSources.filter((source) => ids.has(source.id))
  }, [filteredReportSources, filteredMailboxHealth])

  const recentSyncRunsByMailbox = useMemo(() => {
    const grouped = new Map<string, MailboxSyncRun[]>()
    for (const run of syncRuns) {
      const current = grouped.get(run.reportSourceId) ?? []
      if (current.length < 3) {
        current.push(run)
        grouped.set(run.reportSourceId, current)
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

  const openMailboxDialog = (source?: ReportSource) => {
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
        deleteAfterRetention: source.deleteAfterRetention,
        allowForeignDomains: source.allowForeignDomains,
        s3Bucket: source.s3Bucket ?? '',
        s3Prefix: source.s3Prefix ?? '',
        s3Region: source.s3Region ?? '',
        s3Endpoint: source.s3Endpoint ?? '',
        s3ForcePathStyle: source.s3ForcePathStyle,
      })
    } else {
      setEditingMailboxId(null)
      setMailboxForm({
        ...initialMailboxForm,
        defaultClientId: sortedClients[0]?.id ?? '',
      })
    }
  }

  const createOrUpdateReportSource = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      const payload = { ...mailboxForm }
      if (editingMailboxId && !payload.password) {
        delete (payload as { password?: string }).password
      }

      // The API refuses settings that do not belong to the chosen protocol rather than
      // storing a password nothing will ever use, so anything the form still carries from a
      // previous protocol choice has to be dropped before it is sent.
      const fields = payload as Partial<typeof mailboxForm>

      if (!isMailboxSource) {
        delete fields.host
        delete fields.port
      }

      if (isPushedSource) {
        delete fields.username
        delete fields.password
      }

      if (!isBucketSource) {
        delete fields.s3Bucket
        delete fields.s3Prefix
        delete fields.s3Region
        delete fields.s3Endpoint
        delete fields.s3ForcePathStyle
      } else if (!fields.username && !fields.password) {
        // Both blank means the ambient credential chain. Sent explicitly rather than
        // omitted — omitting means "leave unchanged," and a stored credential would
        // silently survive a field the operator thought they had cleared. A half
        // credential (only one blank) is sent as typed instead, so the API's own pairing
        // check is what catches it, not a guess at intent made here.
        fields.username = ''
        fields.password = ''
      }

      if (editingMailboxId) {
        await fetchJson(`/api/v1/report-sources/${editingMailboxId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      } else {
        await fetchJson('/api/v1/report-sources', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save report source')
    }
  }

  const syncNow = async (id: string) => {
    setSyncingId(id)
    setError(null)
    try {
      await fetchJson(`/api/v1/report-sources/${id}/sync`, { method: 'POST' })
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

  const count = reportSources.length
  const subtitle = [
    `${count} ${count === 1 ? 'source' : 'sources'}`,
    mailboxSourceCount > 0
      ? `${healthyCount}/${mailboxSourceCount} ${mailboxSourceCount === 1 ? 'mailbox' : 'mailboxes'} healthy`
      : null,
  ]
    .filter(Boolean)
    .join(' · ')

  return (
    <>
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Report sources</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2.5 sm:flex-nowrap sm:shrink-0">
          <Input
            icon="search"
            placeholder="Search sources"
            className="w-full sm:w-56"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          {canManage && (
            <Button icon="plus" onClick={() => openMailboxDialog()}>
              Add source
            </Button>
          )}
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {busy && reportSources.length === 0 ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : (
        <>
          <Card pad={false}>
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Source</TableHead>
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
                  {filteredReportSources.map((source, index) => {
                    const health = healthBySourceId.get(source.id)
                    const hasMailbox = sourceHasMailbox(source)
                    const isPolled = sourceIsPolled(source)
                    const badge = !source.isActive
                      ? { label: 'Inactive', variant: 'neutral' as const }
                      : isPolled
                        ? getHealthBadge(health?.lastRunStatus)
                        : getUnpolledBadge(source.protocol)
                    const isSyncing = syncingId === source.id
                    return (
                      <TableRow key={source.id} last={index === filteredReportSources.length - 1}>
                        <TableCell mono>{source.name}</TableCell>
                        <TableCell mono>
                          {/* Port is a mailbox fact. A pushed source stores 0 for it, and
                              rendering that verbatim produced "api:0"; a bucket has no port
                              either. */}
                          {hasMailbox ? `${source.protocol}:${source.port}` : source.protocol}
                        </TableCell>
                        <TableCell mono>{sourceLocation(source)}</TableCell>
                        <TableCell>
                          <span className="text-sm text-secondary">
                            {isPolled ? lastSyncLabel(health) : '—'}
                          </span>
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
                          <div className="flex justify-end gap-2">
                            {canManage && (
                              <Button
                                variant="secondary"
                                size="sm"
                                icon="pencil"
                                onClick={() => openMailboxDialog(source)}
                              >
                                Edit
                              </Button>
                            )}
                            {/* Manual sync refuses a source the worker does not poll, so
                                offering the button on a pushed source only ever produced an
                                error. */}
                            {isPolled && (
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
                            )}
                          </div>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
            {filteredReportSources.length === 0 ? (
              <p className="px-5 py-10 text-center text-sm text-secondary">
                No report sources found{search ? ' for the current search' : ''}.
              </p>
            ) : null}
          </Card>

          {failingMailboxes.length > 0 ? (
            <div className="mt-3 space-y-1.5">
              {failingMailboxes.map((health) => {
                const source = sourceById.get(health.reportSourceId)
                return (
                  <div
                    key={health.reportSourceId}
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

          <Card pad={false} className="mt-3.5">
            <div className="px-5 pt-4 pb-2">
              <CardHeader
                title="Mailbox health"
                description="Operational view of sync outcomes, checkpoints, and latest issues."
                actions={
                  <Select
                    className="w-full sm:w-56"
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
                    <TableHead>Checkpoint</TableHead>
                    <TableHead>Last run metrics</TableHead>
                    <TableHead>Last error</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredMailboxHealth.map((health, index) => (
                    <TableRow
                      key={health.reportSourceId}
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
                      <TableCell mono>
                        {/* Three protocols, three kinds of checkpoint, one column. Showing
                            the UID field alone read as "never synced" for every POP3 and S3
                            source, which is the state this column exists to rule out. */}
                        {health.lastProcessedUid ??
                          health.lastProcessedUidl ??
                          health.lastProcessedObjectKey ??
                          'n/a'}
                      </TableCell>
                      <TableCell>
                        <div className="text-xs leading-5 text-secondary">
                          <div>Scanned: {health.lastRunMessagesScanned ?? 0}</div>
                          <div>Attachments: {health.lastRunAttachmentsProcessed ?? 0}</div>
                          <div>Inserted: {health.lastRunReportsInserted ?? 0}</div>
                          <div>Dupes: {health.lastRunReportsSkippedAsDuplicate ?? 0}</div>
                          <div>
                            TLS: {health.lastRunTlsReportsInserted ?? 0} inserted /{' '}
                            {health.lastRunTlsReportsSkippedAsDuplicate ?? 0} dupes
                          </div>
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
                {mailboxSourceCount === 0
                  ? 'No polled mailboxes. Sources that receive pushed reports have nothing to sync.'
                  : 'No mailboxes match the selected filter.'}
              </p>
            ) : null}
          </Card>

          <ApiCredentialsCard sources={reportSources} />

          <Card pad={false} className="mt-3.5">
            <div className="px-5 pt-4 pb-2">
              <CardHeader title="Recent sync runs" description="Last three runs per report source." />
            </div>
            <CardContent className="space-y-4 pt-2">
              {filteredReportSourcesForOps.length === 0 ? (
                <p className="text-sm text-secondary">No sync runs to show for the selected filter.</p>
              ) : (
                filteredReportSourcesForOps.map((source) => {
                  const runs = recentSyncRunsByMailbox.get(source.id) ?? []
                  return (
                    <div key={source.id} className="rounded-md border border-border p-3">
                      <div className="mb-2 flex items-center justify-between gap-3">
                        <p className="text-sm font-semibold text-body">{source.name}</p>
                        <p className="font-mono text-xs text-secondary">
                          {/* Host and port are mailbox facts. A bucket has neither, and
                              rendering them verbatim produced a bare ":0". */}
                          {sourceHasMailbox(source)
                            ? `${source.host}:${source.port}`
                            : sourceLocation(source)}
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
                                    {run.tlsReportsInserted > 0 || run.tlsReportsSkippedAsDuplicate > 0
                                      ? ` · tls ${run.tlsReportsInserted}/${run.tlsReportsSkippedAsDuplicate}`
                                      : null}
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
            <DialogTitle>{editingMailboxId ? 'Edit report source' : 'Add report source'}</DialogTitle>
            <DialogDescription>
              {isPushedSource
                ? 'Choose the client this source routes to. A pushed source has no mailbox to configure.'
                : isBucketSource
                  ? 'Point at a bucket and prefix, and choose the client its reports route to.'
                  : 'Configure mailbox transport and default routing client.'}
            </DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={createOrUpdateReportSource}>
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
                  onChange={(e) => {
                    const protocol = e.target.value as Protocol
                    // The port moves with the protocol, but only while it is still the
                    // previous protocol's default — an operator who typed 1100 for a
                    // non-standard POP3 server should not have it overwritten.
                    setMailboxForm((x) => ({
                      ...x,
                      protocol,
                      port:
                        x.port === defaultPort[x.protocol] ? defaultPort[protocol] : x.port,
                    }))
                  }}
                >
                  <option value="imap">IMAP (polled)</option>
                  <option value="pop3">POP3 (polled)</option>
                  <option value="s3">S3 bucket (polled)</option>
                  <option value="api">API (pushed)</option>
                </Select>
              </label>
              {isMailboxSource ? (
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Port
                  <Input
                    type="number"
                    min={1}
                    mono
                    value={mailboxForm.port}
                    onChange={(e) =>
                      setMailboxForm((x) => ({ ...x, port: Number(e.target.value) || defaultPort[x.protocol] }))
                    }
                    required
                  />
                </label>
              ) : null}
            </div>
            {isMailboxSource ? (
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Host
                <Input
                  mono
                  value={mailboxForm.host}
                  onChange={(e) => setMailboxForm((x) => ({ ...x, host: e.target.value }))}
                  required
                />
              </label>
            ) : null}
            {isBucketSource ? (
              <>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Bucket
                  <Input
                    mono
                    value={mailboxForm.s3Bucket}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, s3Bucket: e.target.value }))}
                    required
                  />
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Key prefix (optional)
                  <Input
                    mono
                    value={mailboxForm.s3Prefix}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, s3Prefix: e.target.value }))}
                  />
                  <span className="text-xs font-normal text-secondary">
                    Every pass lists all keys under the prefix, so this is also what bounds how
                    much work a poll costs on a bucket that holds more than reports.
                  </span>
                </label>
                <div className="grid grid-cols-2 gap-4">
                  <label className="grid gap-1.5 text-sm font-medium text-body">
                    Region
                    <Input
                      mono
                      placeholder="us-east-1"
                      value={mailboxForm.s3Region}
                      onChange={(e) => setMailboxForm((x) => ({ ...x, s3Region: e.target.value }))}
                    />
                  </label>
                  <label className="grid gap-1.5 text-sm font-medium text-body">
                    Endpoint (optional)
                    <Input
                      mono
                      placeholder="https://minio.internal:9000"
                      value={mailboxForm.s3Endpoint}
                      onChange={(e) =>
                        setMailboxForm((x) => ({ ...x, s3Endpoint: e.target.value }))
                      }
                    />
                  </label>
                </div>
              </>
            ) : null}
            {!isPushedSource ? (
              <>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  {isBucketSource ? 'Access key ID (optional)' : 'Username'}
                  <Input
                    value={mailboxForm.username}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, username: e.target.value }))}
                    required={isMailboxSource}
                  />
                  {isBucketSource ? (
                    <span className="text-xs font-normal text-secondary">
                      Leave both this and the secret empty to use the ambient credential chain
                      — an instance role or IRSA, which is preferable to a stored key.
                    </span>
                  ) : null}
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  {isBucketSource
                    ? editingMailboxId
                      ? 'New secret access key (optional)'
                      : 'Secret access key'
                    : editingMailboxId
                      ? 'New password (optional)'
                      : 'Password'}
                  <Input
                    type="password"
                    value={mailboxForm.password}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, password: e.target.value }))}
                    required={!editingMailboxId && isMailboxSource}
                  />
                </label>
              </>
            ) : null}
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
            {isMailboxSource ? (
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={mailboxForm.useTls}
                  onChange={(e) => setMailboxForm((x) => ({ ...x, useTls: e.target.checked }))}
                />
                Use TLS
              </label>
            ) : null}
            {/* No TLS checkbox for a bucket: the SDK speaks HTTPS to AWS, and to a custom
                endpoint it does whatever that endpoint's scheme says, so it would be a
                control that changes nothing. Path-style is the setting that does matter. */}
            {isBucketSource ? (
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={mailboxForm.s3ForcePathStyle}
                  onChange={(e) =>
                    setMailboxForm((x) => ({ ...x, s3ForcePathStyle: e.target.checked }))
                  }
                />
                Path-style addressing (required by MinIO and most S3-compatible services)
              </label>
            ) : null}
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.isActive}
                onChange={(e) => setMailboxForm((x) => ({ ...x, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.allowForeignDomains}
                onChange={(e) =>
                  setMailboxForm((x) => ({ ...x, allowForeignDomains: e.target.checked }))
                }
              />
              Accept reports for other clients' domains
            </label>
            {!mailboxForm.allowForeignDomains ? (
              <Notice tone="warn" title="This source will only accept its own client's domains.">
                Domains are globally unique and reports are routed by policy domain, so a
                shared mailbox normally delivers to whichever client owns the domain — which
                is what makes one mailbox usable for many clients. With this off, a report
                for a domain another client owns is refused and counted as a failure rather
                than stored. Worth doing for a pushed source whose reports should only ever
                concern one client, so a leaked credential cannot put reports under a client
                it has no other relationship with.
              </Notice>
            ) : null}
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.deleteAfterRetention}
                onChange={(e) =>
                  setMailboxForm((x) => ({ ...x, deleteAfterRetention: e.target.checked }))
                }
              />
              Delete mail past the retention window
            </label>
            {mailboxForm.deleteAfterRetention ? (
              <Notice tone="warn" title="This deletes mail from the mailbox.">
                Report mail older than the widest retention window of the clients this source
                serves, plus a grace margin, is expunged on a daily pass. It gives the system
                one retention window instead of two — without it, reports purged from the
                database return on the next sync — but the mailbox is also what a report
                replay reads from, so anything deleted is gone unless the object-storage
                archive is on. Deletion is suspended entirely while any client this source
                serves is under legal hold. Check{' '}
                <span className="font-mono text-xs">/api/v1/admin/mailbox-retention/preview</span>{' '}
                to see the cutoff before the pass runs.
              </Notice>
            ) : null}
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
