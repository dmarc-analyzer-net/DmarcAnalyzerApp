import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
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
  effectiveDigestModes,
  summarizeDigestModes,
  type DigestMode,
  type NotificationKind,
  type NotificationRecipient,
} from '@/lib/analytics'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type { Client } from '@/lib/entities'
import { usePageTitle } from '@/lib/use-page-title'
import { cn } from '@/lib/utils'

const KIND_LABEL: Record<NotificationKind, string> = {
  alert: 'Alerts only',
  digest: 'Digest only',
  both: 'Alerts + digest',
}

const MODE_LABEL: Record<DigestMode, string> = {
  rollup: 'In roll-up',
  separate: 'Own mail',
  off: 'Off',
}

const NEW_CLIENTS_LABEL: Record<DigestMode, string> = {
  rollup: 'New clients join the roll-up',
  separate: 'New clients get their own mail',
  off: 'New clients are not included',
}

type PreviewMail = { subject: string; isRollup: boolean; clientIds: string[] }

/**
 * Who gets emailed. A recipient is either one client's (its digest arrives as a mail of
 * its own) or covers several: each client is in that address's one roll-up, in a mail
 * of its own, or off — and off also stops that client's alerts to it.
 */
export function NotificationsPage() {
  usePageTitle('Notifications')
  const { user } = useAuth()
  const admin = isAdmin(user)

  const [recipients, setRecipients] = useState<NotificationRecipient[] | null>(null)
  const [clients, setClients] = useState<Client[]>([])
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const [email, setEmail] = useState('')
  const [clientId, setClientId] = useState('')
  const [defaultMode, setDefaultMode] = useState<DigestMode>('rollup')
  const [kind, setKind] = useState<NotificationKind>('both')
  const [saving, setSaving] = useState(false)

  const [testTo, setTestTo] = useState('')
  const [testing, setTesting] = useState(false)

  const [routing, setRouting] = useState<NotificationRecipient | null>(null)
  const [preview, setPreview] = useState<{ recipient: NotificationRecipient; mails: PreviewMail[] | null } | null>(
    null,
  )

  const sortedClients = useMemo(() => [...clients].sort((a, b) => a.name.localeCompare(b.name)), [clients])
  const clientIds = useMemo(() => sortedClients.map((c) => c.id), [sortedClients])

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [recipientData, clientData] = await Promise.all([
        fetchJson<NotificationRecipient[]>('/api/v1/notification-recipients'),
        fetchJson<Client[]>('/api/v1/clients'),
      ])
      setRecipients(recipientData)
      setClients(clientData)
      return recipientData
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load recipients')
      return null
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void loadData()
  }, [loadData])

  const addRecipient = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      const several = clientId === ''
      const created = await fetchJson<{ id: string }>('/api/v1/notification-recipients', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email: email.trim(),
          clientId: several ? null : clientId,
          kind,
          digestDefaultMode: several ? defaultMode : undefined,
        }),
      })
      setEmail('')
      const fresh = await loadData()
      // "Only clients I choose" starts with none chosen — go straight to choosing them.
      const row = several && defaultMode === 'off' ? fresh?.find((r) => r.id === created.id) : undefined
      if (row) {
        setRouting(row)
      } else {
        setNotice('Recipient added.')
      }
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Could not add that recipient')
    } finally {
      setSaving(false)
    }
  }

  const removeRecipient = async (id: string, address: string) => {
    if (!window.confirm(`Stop sending notifications to ${address}?`)) return
    setError(null)
    setNotice(null)
    try {
      await fetchJson(`/api/v1/notification-recipients/${id}`, { method: 'DELETE' })
      await loadData()
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : 'Could not remove that recipient')
    }
  }

  const openPreview = async (recipient: NotificationRecipient) => {
    setPreview({ recipient, mails: null })
    try {
      const data = await fetchJson<{ mails: PreviewMail[] }>(
        `/api/v1/admin/digest/preview?recipientId=${recipient.id}`,
      )
      setPreview({ recipient, mails: data.mails })
    } catch (previewError) {
      setPreview(null)
      setError(previewError instanceof Error ? previewError.message : 'Could not build the preview')
    }
  }

  const sendTest = async () => {
    setTesting(true)
    setError(null)
    setNotice(null)
    try {
      await fetchJson(`/api/v1/admin/notifications/test?to=${encodeURIComponent(testTo.trim())}`, {
        method: 'POST',
      })
      setNotice(`Test email sent to ${testTo.trim()}.`)
    } catch (testError) {
      // The API explains what's missing (e.g. Email:Host unset) — surface it as-is.
      setError(testError instanceof Error ? testError.message : 'Test send failed')
    } finally {
      setTesting(false)
    }
  }

  const coverage = (recipient: NotificationRecipient) => {
    if (recipient.clientName) return <span className="text-sm text-body">{recipient.clientName}</span>
    const modes = effectiveDigestModes(recipient, clientIds)
    const everyClient = Object.values(modes).every((m) => m === 'rollup') && recipient.digestDefaultMode === 'rollup'
    return (
      <div className="flex flex-col gap-0.5">
        <span className="text-sm text-body">{everyClient ? 'All clients, one roll-up' : summarizeDigestModes(modes)}</span>
        <span className="text-xs text-faint">{NEW_CLIENTS_LABEL[recipient.digestDefaultMode]}</span>
      </div>
    )
  }

  return (
    <>
      <div className="mb-5">
        <h1 className="text-xl font-semibold tracking-tight text-body">Notifications</h1>
        <p className="mt-1 text-sm text-secondary">Who receives alert emails and the monthly digest</p>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}
      {notice ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-ok-bg)] bg-[var(--status-ok-bg)] px-3 py-2 text-sm text-[var(--status-ok-fg)]">
          {notice}
        </div>
      ) : null}

      {recipients === null && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {recipients ? (
        <div className={cn('space-y-3.5 transition-opacity', busy && 'opacity-60')}>
          {admin ? (
            <Card pad>
              <CardHeader
                title="Add a recipient"
                description="One client's contact gets that client's digest. An address covering several clients gets one roll-up, with any client you choose split into its own mail."
              />
              <form onSubmit={addRecipient} className="flex flex-wrap items-end gap-3">
                <label className="flex min-w-[240px] flex-1 flex-col gap-1.5">
                  <span className="text-xs font-medium text-secondary">Email</span>
                  <Input
                    type="email"
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="ops@client.example"
                  />
                </label>
                <label className="flex min-w-[200px] flex-col gap-1.5">
                  <span className="text-xs font-medium text-secondary">Covers</span>
                  <Select value={clientId} onChange={(e) => setClientId(e.target.value)}>
                    <option value="">Several clients</option>
                    {sortedClients.map((client) => (
                      <option key={client.id} value={client.id}>
                        {client.name} only
                      </option>
                    ))}
                  </Select>
                </label>
                {clientId === '' ? (
                  <label className="flex min-w-[220px] flex-col gap-1.5">
                    <span className="text-xs font-medium text-secondary">Which clients</span>
                    <Select value={defaultMode} onChange={(e) => setDefaultMode(e.target.value as DigestMode)}>
                      <option value="rollup">All, in one roll-up</option>
                      <option value="separate">All, one mail each</option>
                      <option value="off">Only clients I choose</option>
                    </Select>
                  </label>
                ) : null}
                <label className="flex min-w-[170px] flex-col gap-1.5">
                  <span className="text-xs font-medium text-secondary">Receives</span>
                  <Select value={kind} onChange={(e) => setKind(e.target.value as NotificationKind)}>
                    <option value="both">Alerts + digest</option>
                    <option value="alert">Alerts only</option>
                    <option value="digest">Digest only</option>
                  </Select>
                </label>
                <Button type="submit" size="sm" disabled={saving || email.trim().length === 0}>
                  {saving ? <Icon name="loader-circle" size={14} className="animate-spin" /> : <Icon name="plus" size={14} />}
                  Add
                </Button>
              </form>
            </Card>
          ) : null}

          <Card>
            <div className="flex items-start justify-between gap-3 px-5 pt-5">
              <CardHeader title="Recipients" description="Addresses currently receiving notifications" />
              <Badge variant="neutral">{recipients.length}</Badge>
            </div>
            {recipients.length === 0 ? (
              <p className="px-5 pb-6 pt-2 text-sm text-secondary">
                No recipients yet — alerts are still recorded on the Alerts page, they just aren’t emailed.
              </p>
            ) : (
              <div className="overflow-x-auto">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Email</TableHead>
                      <TableHead>Covers</TableHead>
                      <TableHead>Receives</TableHead>
                      <TableHead>Status</TableHead>
                      {admin ? <TableHead className="text-right">Actions</TableHead> : null}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {recipients.map((recipient) => (
                      <TableRow key={recipient.id}>
                        <TableCell className="font-mono text-xs text-body">{recipient.email}</TableCell>
                        <TableCell>{coverage(recipient)}</TableCell>
                        <TableCell className="text-sm text-secondary">{KIND_LABEL[recipient.kind]}</TableCell>
                        <TableCell>
                          <Badge variant={recipient.isActive ? 'success' : 'neutral'}>
                            {recipient.isActive ? 'Active' : 'Inactive'}
                          </Badge>
                        </TableCell>
                        {admin ? (
                          <TableCell className="text-right">
                            <div className="flex justify-end gap-1">
                              {recipient.clientId === null ? (
                                <Button variant="ghost" size="sm" icon="list-filter" onClick={() => setRouting(recipient)}>
                                  Clients
                                </Button>
                              ) : null}
                              {recipient.kind !== 'alert' ? (
                                <Button variant="ghost" size="sm" icon="eye" onClick={() => void openPreview(recipient)}>
                                  Preview
                                </Button>
                              ) : null}
                              <Button
                                variant="ghost"
                                size="sm"
                                icon="trash-2"
                                onClick={() => void removeRecipient(recipient.id, recipient.email)}
                              >
                                Remove
                              </Button>
                            </div>
                          </TableCell>
                        ) : null}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}
          </Card>

          {admin ? (
            <Card pad>
              <CardHeader
                title="Test the mail relay"
                description="Confirms SMTP works now, rather than finding out when something breaks."
              />
              <div className="flex flex-wrap items-end gap-3">
                <label className="flex min-w-[260px] flex-1 flex-col gap-1.5">
                  <span className="text-xs font-medium text-secondary">Send a test to</span>
                  <Input
                    type="email"
                    value={testTo}
                    onChange={(e) => setTestTo(e.target.value)}
                    placeholder="you@example.com"
                  />
                </label>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => void sendTest()}
                  disabled={testing || testTo.trim().length === 0}
                >
                  {testing ? <Icon name="loader-circle" size={14} className="animate-spin" /> : <Icon name="mail" size={14} />}
                  Send test
                </Button>
              </div>
            </Card>
          ) : null}
        </div>
      ) : null}

      {routing ? (
        <RoutingDialog
          recipient={routing}
          clients={sortedClients}
          onClose={() => setRouting(null)}
          onSaved={async () => {
            setRouting(null)
            setNotice('Clients updated.')
            await loadData()
          }}
        />
      ) : null}

      <Dialog open={preview !== null} onOpenChange={(open) => (!open ? setPreview(null) : undefined)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Digest preview</DialogTitle>
            <DialogDescription>
              What <span className="font-mono">{preview?.recipient.email}</span> would get for last month. Nothing is sent.
            </DialogDescription>
          </DialogHeader>
          {preview?.mails === null ? (
            <div className="flex justify-center py-8">
              <Icon name="loader-circle" size={20} className="animate-spin text-secondary" />
            </div>
          ) : preview?.mails.length === 0 ? (
            <p className="text-sm text-secondary">No digest: every client this address covers is off, or has no domains.</p>
          ) : (
            <ul className="divide-y divide-border rounded-md border border-border">
              {preview?.mails.map((mail, index) => (
                <li key={mail.subject} className="flex items-center justify-between gap-3 px-3 py-2.5">
                  <div className="min-w-0">
                    <div className="truncate text-sm text-body">{mail.subject}</div>
                    <div className="text-xs text-faint">
                      {mail.isRollup ? `Roll-up of ${mail.clientIds.length} clients` : 'Single client'}
                    </div>
                  </div>
                  <Button variant="secondary" size="sm" asChild>
                    <a
                      href={`/api/v1/admin/digest/preview.html?recipientId=${preview.recipient.id}&index=${index}`}
                      target="_blank"
                      rel="noreferrer"
                    >
                      <Icon name="external-link" size={14} />
                      Open
                    </a>
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}

function RoutingDialog({
  recipient,
  clients,
  onClose,
  onSaved,
}: {
  recipient: NotificationRecipient
  clients: Client[]
  onClose: () => void
  onSaved: () => Promise<void>
}) {
  const [defaultMode, setDefaultMode] = useState<DigestMode>(recipient.digestDefaultMode)
  const [modes, setModes] = useState<Record<string, DigestMode>>(() =>
    effectiveDigestModes(recipient, clients.map((c) => c.id)),
  )
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const setAll = (mode: DigestMode) => setModes(Object.fromEntries(clients.map((c) => [c.id, mode])))

  const save = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await fetchJson(`/api/v1/notification-recipients/${recipient.id}/routing`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          digestDefaultMode: defaultMode,
          clientModes: Object.entries(modes).map(([clientId, mode]) => ({ clientId, mode })),
        }),
      })
      await onSaved()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Could not save')
      setSaving(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => (!open ? onClose() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Clients</DialogTitle>
          <DialogDescription>
            What <span className="font-mono">{recipient.email}</span> hears about each client. Off stops alerts as well as
            the digest.
          </DialogDescription>
        </DialogHeader>
        <form className="grid gap-4" onSubmit={save}>
          {error ? (
            <div className="rounded-md bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
              {error}
            </div>
          ) : null}
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-medium text-secondary">Set all:</span>
            {(['rollup', 'separate', 'off'] as const).map((mode) => (
              <Button key={mode} type="button" variant="secondary" size="sm" onClick={() => setAll(mode)}>
                {MODE_LABEL[mode]}
              </Button>
            ))}
          </div>
          <ul className="divide-y divide-border rounded-md border border-border">
            {clients.map((client) => (
              <li key={client.id} className="flex items-center justify-between gap-3 px-3 py-2">
                <span className={cn('min-w-0 truncate text-sm', modes[client.id] === 'off' ? 'text-faint' : 'text-body')}>
                  {client.name}
                </span>
                <Select
                  className="w-36 shrink-0"
                  aria-label={`Digest for ${client.name}`}
                  value={modes[client.id]}
                  onChange={(e) => setModes((x) => ({ ...x, [client.id]: e.target.value as DigestMode }))}
                >
                  <option value="rollup">{MODE_LABEL.rollup}</option>
                  <option value="separate">{MODE_LABEL.separate}</option>
                  <option value="off">{MODE_LABEL.off}</option>
                </Select>
              </li>
            ))}
          </ul>
          <label className="flex flex-col gap-1.5">
            <span className="text-xs font-medium text-secondary">Clients added later</span>
            <Select value={defaultMode} onChange={(e) => setDefaultMode(e.target.value as DigestMode)}>
              <option value="rollup">{NEW_CLIENTS_LABEL.rollup}</option>
              <option value="separate">{NEW_CLIENTS_LABEL.separate}</option>
              <option value="off">{NEW_CLIENTS_LABEL.off}</option>
            </Select>
          </label>
          <div className="flex justify-end gap-2 pt-1">
            <Button type="button" variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={saving}>
              Save
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  )
}
