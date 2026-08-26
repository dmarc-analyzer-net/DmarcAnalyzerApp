import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import type { NotificationKind, NotificationRecipient } from '@/lib/analytics'
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

/**
 * Who gets emailed. A recipient with no client is agency-wide and receives
 * notifications for every client — useful for an internal ops address.
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
  const [kind, setKind] = useState<NotificationKind>('both')
  const [saving, setSaving] = useState(false)

  const [testTo, setTestTo] = useState('')
  const [testing, setTesting] = useState(false)

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
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load recipients')
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
      await fetchJson('/api/v1/notification-recipients', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: email.trim(), clientId: clientId || null, kind }),
      })
      setEmail('')
      setNotice('Recipient added.')
      await loadData()
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

  return (
    <>
      <div className="mb-5">
        <h1 className="text-xl font-semibold tracking-tight text-body">Notifications</h1>
        <p className="mt-1 text-sm text-secondary">
          Who receives alert emails and the monthly digest
        </p>
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
                description="Leave the client blank to send notifications for every client to this address."
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
                  <span className="text-xs font-medium text-secondary">Client</span>
                  <Select value={clientId} onChange={(e) => setClientId(e.target.value)}>
                    <option value="">All clients (agency-wide)</option>
                    {clients.map((client) => (
                      <option key={client.id} value={client.id}>
                        {client.name}
                      </option>
                    ))}
                  </Select>
                </label>
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
                      <TableHead>Client</TableHead>
                      <TableHead>Receives</TableHead>
                      <TableHead>Status</TableHead>
                      {admin ? <TableHead className="text-right">Actions</TableHead> : null}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {recipients.map((recipient) => (
                      <TableRow key={recipient.id}>
                        <TableCell className="font-mono text-xs text-body">{recipient.email}</TableCell>
                        <TableCell className="text-sm text-secondary">
                          {recipient.clientName ?? <Badge variant="neutral">All clients</Badge>}
                        </TableCell>
                        <TableCell className="text-sm text-secondary">{KIND_LABEL[recipient.kind]}</TableCell>
                        <TableCell>
                          <Badge variant={recipient.isActive ? 'success' : 'neutral'}>
                            {recipient.isActive ? 'Active' : 'Inactive'}
                          </Badge>
                        </TableCell>
                        {admin ? (
                          <TableCell className="text-right">
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => void removeRecipient(recipient.id, recipient.email)}
                            >
                              <Icon name="trash-2" size={14} />
                              Remove
                            </Button>
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
    </>
  )
}
