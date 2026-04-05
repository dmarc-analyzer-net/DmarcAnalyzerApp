import { LayoutDashboard, Mail, RefreshCw, ShieldCheck } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
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

type ViewKey = 'clients' | 'domains' | 'mailbox-sources'

type SyncRunStatus = 'success' | 'failed' | 'running' | 'unknown'
type MailboxOpsFilter = 'all' | 'failed' | 'parse-failures' | 'stale-success'

type Client = {
  id: string
  name: string
  slug: string
  isActive: boolean
  retentionMonths: number
  timezone: string
}

type Domain = {
  id: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string | null
}

type MailboxSource = {
  id: string
  name: string
  protocol: 'imap' | 'pop3'
  host: string
  port: number
  useTls: boolean
  username: string
  defaultClientId: string
  defaultClientName: string | null
  isActive: boolean
}

type MailboxHealth = {
  mailboxSourceId: string
  name: string
  isActive: boolean
  lastSuccessSyncAtUtc: string | null
  lastProcessedUid: number | null
  lastProcessedUidValidity: number | null
  lastRunStatus: SyncRunStatus | null
  lastRunStartedAtUtc: string | null
  lastRunFinishedAtUtc: string | null
  lastRunError: string | null
  lastRunMessagesScanned: number | null
  lastRunAttachmentsProcessed: number | null
  lastRunReportsInserted: number | null
  lastRunReportsSkippedAsDuplicate: number | null
  lastRunParseFailures: number | null
}

type MailboxSyncRun = {
  id: string
  mailboxSourceId: string
  trigger: string
  status: SyncRunStatus
  startedAtUtc: string
  finishedAtUtc: string | null
  messagesScanned: number
  attachmentsProcessed: number
  reportsInserted: number
  reportsSkippedAsDuplicate: number
  parseFailures: number
  error: string | null
  createdAtUtc: string
}

const sections: Array<{ key: ViewKey; title: string; icon: typeof ShieldCheck }> = [
  { key: 'clients', title: 'Clients', icon: ShieldCheck },
  { key: 'domains', title: 'Domains', icon: LayoutDashboard },
  { key: 'mailbox-sources', title: 'Mailbox Sources', icon: Mail },
]

const initialClientForm = {
  name: '',
  slug: '',
  timezone: 'UTC',
  retentionMonths: 27,
  isActive: true,
}

const initialDomainForm = {
  name: '',
  clientId: '',
  isActive: true,
}

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

function BrandLogo({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 248 73"
      xmlns="http://www.w3.org/2000/svg"
      preserveAspectRatio="xMinYMid meet"
      className={className}
      aria-label="DMARC Analyzer .NET"
      role="img"
    >
      <rect x="1" y="0" width="70" height="70" rx="14" fill="#0D9488" />
      <rect
        x="14"
        y="20"
        width="44"
        height="30"
        rx="3"
        fill="none"
        stroke="white"
        strokeWidth="1.8"
      />
      <path
        d="M15 21.25L36 38L57 21.25"
        fill="none"
        stroke="white"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <text
        x="88"
        y="48"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="44"
        fontWeight="700"
        fill="#0F172A"
        letterSpacing="-1"
      >
        DMARC
      </text>
      <text
        x="90"
        y="69"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="13"
        fontWeight="400"
        fill="#64748B"
        letterSpacing="4.5"
      >
        ANALYZER
      </text>
      <rect x="198" y="56" width="35" height="17" rx="3" fill="#0D9488" />
      <text
        x="215.5"
        y="68"
        fontFamily="'Helvetica Neue',Helvetica,Arial,sans-serif"
        fontSize="10.5"
        fontWeight="500"
        fill="white"
        textAnchor="middle"
      >
        .NET
      </text>
    </svg>
  )
}

function App() {
  const [view, setView] = useState<ViewKey>('clients')
  const [status, setStatus] = useState('Loading API status...')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [clients, setClients] = useState<Client[]>([])
  const [domains, setDomains] = useState<Domain[]>([])
  const [mailboxSources, setMailboxSources] = useState<MailboxSource[]>([])
  const [mailboxHealth, setMailboxHealth] = useState<MailboxHealth[]>([])
  const [syncRuns, setSyncRuns] = useState<MailboxSyncRun[]>([])

  const [search, setSearch] = useState('')
  const [mailboxOpsFilter, setMailboxOpsFilter] = useState<MailboxOpsFilter>('all')

  const [clientDialogOpen, setClientDialogOpen] = useState(false)
  const [domainDialogOpen, setDomainDialogOpen] = useState(false)
  const [mailboxDialogOpen, setMailboxDialogOpen] = useState(false)

  const [editingClientId, setEditingClientId] = useState<string | null>(null)
  const [editingDomainId, setEditingDomainId] = useState<string | null>(null)
  const [editingMailboxId, setEditingMailboxId] = useState<string | null>(null)

  const [clientForm, setClientForm] = useState(initialClientForm)
  const [domainForm, setDomainForm] = useState(initialDomainForm)
  const [mailboxForm, setMailboxForm] = useState(initialMailboxForm)

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  )

  const filteredClients = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return sortedClients
    return sortedClients.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        x.slug.toLowerCase().includes(q) ||
        x.timezone.toLowerCase().includes(q),
    )
  }, [search, sortedClients])

  const filteredDomains = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return domains
    return domains.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        (x.clientName ?? '').toLowerCase().includes(q) ||
        x.clientId.toLowerCase().includes(q),
    )
  }, [search, domains])

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

  const fetchJson = async <T,>(url: string, init?: RequestInit): Promise<T> => {
    const response = await fetch(url, init)
    if (!response.ok) {
      let message = `Request failed (${response.status})`
      try {
        const payload = (await response.json()) as { error?: string }
        if (payload.error) message = payload.error
      } catch {
        // ignore json parse errors
      }
      throw new Error(message)
    }
    return (await response.json()) as T
  }

  const resetDialogs = () => {
    setClientDialogOpen(false)
    setDomainDialogOpen(false)
    setMailboxDialogOpen(false)
    setEditingClientId(null)
    setEditingDomainId(null)
    setEditingMailboxId(null)
    setClientForm(initialClientForm)
    setDomainForm((x) => ({ ...initialDomainForm, clientId: x.clientId }))
    setMailboxForm((x) => ({ ...initialMailboxForm, defaultClientId: x.defaultClientId }))
    setError(null)
  }

  const loadData = async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, domainData, mailboxData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<Domain[]>('/api/v1/domains'),
        fetchJson<MailboxSource[]>('/api/v1/mailbox-sources'),
      ])

      const [healthData, syncRunData] = await Promise.all([
        fetchJson<MailboxHealth[]>('/api/v1/mailbox-health'),
        fetchJson<MailboxSyncRun[]>('/api/v1/mailbox-sync-runs?limit=40'),
      ])

      setClients(clientData)
      setDomains(domainData)
      setMailboxSources(mailboxData)
      setMailboxHealth(healthData)
      setSyncRuns(syncRunData)

      if (!domainForm.clientId && clientData[0]) {
        setDomainForm((x) => ({ ...x, clientId: clientData[0].id }))
      }
      if (!mailboxForm.defaultClientId && clientData[0]) {
        setMailboxForm((x) => ({ ...x, defaultClientId: clientData[0].id }))
      }
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load data')
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    const boot = async () => {
      try {
        const payload = await fetchJson<{ service: string; mode: string; timestampUtc: string }>(
          '/api/v1/system/status',
        )
        setStatus(`${payload.service} (${payload.mode}) at ${payload.timestampUtc}`)
      } catch (statusError) {
        setStatus(
          statusError instanceof Error ? `API unavailable: ${statusError.message}` : 'API unavailable',
        )
      }

      await loadData()
    }

    void boot()
  }, [])

  const createOrUpdateClient = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      if (editingClientId) {
        await fetchJson(`/api/v1/clients/${editingClientId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(clientForm),
        })
      } else {
        await fetchJson('/api/v1/clients', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(clientForm),
        })
      }

      resetDialogs()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save client')
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

      resetDialogs()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save domain')
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

      resetDialogs()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save mailbox source')
    }
  }

  const openClientDialog = (client?: Client) => {
    setError(null)
    setView('clients')
    setClientDialogOpen(true)
    if (client) {
      setEditingClientId(client.id)
      setClientForm({
        name: client.name,
        slug: client.slug,
        timezone: client.timezone,
        retentionMonths: client.retentionMonths,
        isActive: client.isActive,
      })
    } else {
      setEditingClientId(null)
      setClientForm(initialClientForm)
    }
  }

  const openDomainDialog = (domain?: Domain) => {
    setError(null)
    setView('domains')
    setDomainDialogOpen(true)
    if (domain) {
      setEditingDomainId(domain.id)
      setDomainForm({
        name: domain.name,
        clientId: domain.clientId,
        isActive: domain.isActive,
      })
    } else {
      setEditingDomainId(null)
      setDomainForm({
        ...initialDomainForm,
        clientId: sortedClients[0]?.id ?? '',
      })
    }
  }

  const openMailboxDialog = (source?: MailboxSource) => {
    setError(null)
    setView('mailbox-sources')
    setMailboxDialogOpen(true)
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

  return (
    <div className="mx-auto grid min-h-screen w-full max-w-[1280px] grid-cols-1 gap-6 px-4 py-6 lg:grid-cols-[250px_1fr]">
      <aside className="rounded-lg border bg-card p-4 shadow-panel">
        <BrandLogo className="mb-4 h-auto w-full max-w-[205px]" />
        <div className="space-y-2">
          {sections.map((section) => {
            const Icon = section.icon
            const active = view === section.key
            return (
              <Button
                key={section.key}
                variant={active ? 'default' : 'secondary'}
                className="w-full justify-start"
                onClick={() => {
                  setView(section.key)
                  setSearch('')
                }}
              >
                <Icon className="h-4 w-4" />
                {section.title}
              </Button>
            )
          })}
        </div>
      </aside>

      <main className="space-y-4">
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
              <Button variant="outline" onClick={() => void loadData()} disabled={busy}>
                <RefreshCw className="h-4 w-4" />
                Refresh
              </Button>
              {view === 'clients' && <Button onClick={() => openClientDialog()}>New Client</Button>}
              {view === 'domains' && <Button onClick={() => openDomainDialog()}>New Domain</Button>}
              {view === 'mailbox-sources' && (
                <Button onClick={() => openMailboxDialog()}>New Mailbox</Button>
              )}
            </div>
          </CardHeader>
          {!!error && <CardContent><p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</p></CardContent>}
        </Card>

        {view === 'clients' && (
          <Card>
            <CardHeader>
              <CardTitle>Clients</CardTitle>
              <Badge variant="muted">{filteredClients.length} records</Badge>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Name</TableHead>
                    <TableHead>Slug</TableHead>
                    <TableHead>Timezone</TableHead>
                    <TableHead>Retention</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredClients.map((client) => (
                    <TableRow key={client.id}>
                      <TableCell className="font-medium">{client.name}</TableCell>
                      <TableCell>{client.slug}</TableCell>
                      <TableCell>{client.timezone}</TableCell>
                      <TableCell>{client.retentionMonths} mo</TableCell>
                      <TableCell>
                        <Badge variant={client.isActive ? 'success' : 'muted'}>
                          {client.isActive ? 'Active' : 'Inactive'}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-right">
                        <Button variant="outline" size="sm" onClick={() => openClientDialog(client)}>
                          Edit
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )}

        {view === 'domains' && (
          <Card>
            <CardHeader>
              <CardTitle>Domains</CardTitle>
              <Badge variant="muted">{filteredDomains.length} records</Badge>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Domain</TableHead>
                    <TableHead>Client</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredDomains.map((domain) => (
                    <TableRow key={domain.id}>
                      <TableCell className="font-medium">{domain.name}</TableCell>
                      <TableCell>{domain.clientName ?? domain.clientId}</TableCell>
                      <TableCell>
                        <Badge variant={domain.isActive ? 'success' : 'muted'}>
                          {domain.isActive ? 'Active' : 'Inactive'}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-right">
                        <Button variant="outline" size="sm" onClick={() => openDomainDialog(domain)}>
                          Edit
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        )}

        {view === 'mailbox-sources' && (
          <>
            <Card>
              <CardHeader>
                <CardTitle>Mailbox Sources</CardTitle>
                <Badge variant="muted">{filteredMailboxSources.length} records</Badge>
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
                      <TableHead className="text-right">Actions</TableHead>
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
                        <TableCell className="text-right">
                          <Button variant="outline" size="sm" onClick={() => openMailboxDialog(source)}>
                            Edit
                          </Button>
                        </TableCell>
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
          </>
        )}
      </main>

      <Dialog open={clientDialogOpen} onOpenChange={(open) => (!open ? resetDialogs() : setClientDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingClientId ? 'Edit Client' : 'Create Client'}</DialogTitle>
            <DialogDescription>Manage agency client account profile and settings.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-3" onSubmit={createOrUpdateClient}>
            <Input placeholder="Name" value={clientForm.name} onChange={(e) => setClientForm((x) => ({ ...x, name: e.target.value }))} required />
            <Input placeholder="Slug" value={clientForm.slug} onChange={(e) => setClientForm((x) => ({ ...x, slug: e.target.value }))} required />
            <Input placeholder="Timezone" value={clientForm.timezone} onChange={(e) => setClientForm((x) => ({ ...x, timezone: e.target.value }))} required />
            <Input type="number" min={1} value={clientForm.retentionMonths} onChange={(e) => setClientForm((x) => ({ ...x, retentionMonths: Number(e.target.value || 27) }))} required />
            <label className="text-sm text-muted-foreground">
              <input className="mr-2" type="checkbox" checked={clientForm.isActive} onChange={(e) => setClientForm((x) => ({ ...x, isActive: e.target.checked }))} />
              Active
            </label>
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="outline" onClick={resetDialogs}>Cancel</Button>
              <Button type="submit">{editingClientId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={domainDialogOpen} onOpenChange={(open) => (!open ? resetDialogs() : setDomainDialogOpen(true))}>
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
              <Button type="button" variant="outline" onClick={resetDialogs}>Cancel</Button>
              <Button type="submit">{editingDomainId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={mailboxDialogOpen} onOpenChange={(open) => (!open ? resetDialogs() : setMailboxDialogOpen(true))}>
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
              <Button type="button" variant="outline" onClick={resetDialogs}>Cancel</Button>
              <Button type="submit">{editingMailboxId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  )
}

export default App
