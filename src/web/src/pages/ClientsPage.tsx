import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

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
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type { Client, DigestDefaults, DigestThresholds } from '@/lib/entities'
import { usePageTitle } from '@/lib/use-page-title'

const initialClientForm = {
  name: '',
  slug: '',
  timezone: 'UTC',
  retentionMonths: 27,
  isActive: true,
  legalHold: false,
  alertsEnabled: true,
  // Empty string means "use the server default" — the API stores null.
  alertComplianceDropPercent: '',
  alertMinMessages: '',
  // Digest overrides, same convention: blank inherits the instance default.
  digestLowCompliancePercent: '',
  digestComplianceDropPoints: '',
  digestMinMessages: '',
  digestNoReports: '' as '' | 'on' | 'off',
  digestTightenAfterDays: '',
  digestTightenCompliancePercent: '',
}

const blankOr = (value: number | null | undefined) => (value == null ? '' : String(value))
const numberOrNull = (value: string) => (value.trim() === '' ? null : Number(value))

export function ClientsPage() {
  usePageTitle('Clients')
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingClientId, setEditingClientId] = useState<string | null>(null)
  const [clientForm, setClientForm] = useState(initialClientForm)
  const [digestDefaults, setDigestDefaults] = useState<DigestDefaults | null>(null)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      setClients(await fetchJson<Client[]>('/api/v1/clients'))
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load clients')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void loadData()
  }, [loadData])

  // Only for the placeholders; the form works without them.
  useEffect(() => {
    if (!canManage) return
    fetchJson<DigestDefaults>('/api/v1/admin/digest/defaults')
      .then(setDigestDefaults)
      .catch(() => setDigestDefaults(null))
  }, [canManage])

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

  const resetDialog = () => {
    setDialogOpen(false)
    setEditingClientId(null)
    setClientForm(initialClientForm)
    setError(null)
  }

  const openClientDialog = (client?: Client) => {
    setError(null)
    setDialogOpen(true)
    if (client) {
      setEditingClientId(client.id)
      setClientForm({
        name: client.name,
        slug: client.slug,
        timezone: client.timezone,
        retentionMonths: client.retentionMonths,
        isActive: client.isActive,
        legalHold: client.legalHold,
        alertsEnabled: client.alertsEnabled,
        alertComplianceDropPercent: client.alertComplianceDropPercent?.toString() ?? '',
        alertMinMessages: client.alertMinMessages?.toString() ?? '',
        digestLowCompliancePercent: blankOr(client.digestThresholds?.lowCompliancePercent),
        digestComplianceDropPoints: blankOr(client.digestThresholds?.complianceDropPoints),
        digestMinMessages: blankOr(client.digestThresholds?.minMessages),
        digestNoReports:
          client.digestThresholds?.noReports == null ? '' : client.digestThresholds.noReports ? 'on' : 'off',
        digestTightenAfterDays: blankOr(client.digestThresholds?.tightenAfterDays),
        digestTightenCompliancePercent: blankOr(client.digestThresholds?.tightenCompliancePercent),
      })
    } else {
      setEditingClientId(null)
      setClientForm(initialClientForm)
    }
  }

  // The API can't distinguish an omitted threshold from "clear it", so a blank
  // field sends the explicit clear flag instead of a bare null. Slug is absent
  // here and added only on create — it is immutable once the client exists.
  const clientPayload = () => {
    const drop = clientForm.alertComplianceDropPercent.trim()
    const min = clientForm.alertMinMessages.trim()
    return {
      name: clientForm.name,
      timezone: clientForm.timezone,
      retentionMonths: clientForm.retentionMonths,
      isActive: clientForm.isActive,
      legalHold: clientForm.legalHold,
      alertsEnabled: clientForm.alertsEnabled,
      alertComplianceDropPercent: drop === '' ? null : Number(drop),
      alertMinMessages: min === '' ? null : Number(min),
      clearAlertThresholds: drop === '' && min === '',
    }
  }

  // Sent whole on every save: the API replaces the overrides wholesale, and a field
  // left null inherits the instance default.
  const digestPayload = (): DigestThresholds => ({
    lowCompliancePercent: numberOrNull(clientForm.digestLowCompliancePercent),
    complianceDropPoints: numberOrNull(clientForm.digestComplianceDropPoints),
    minMessages: numberOrNull(clientForm.digestMinMessages),
    noReports: clientForm.digestNoReports === '' ? null : clientForm.digestNoReports === 'on',
    tightenAfterDays: numberOrNull(clientForm.digestTightenAfterDays),
    tightenCompliancePercent: numberOrNull(clientForm.digestTightenCompliancePercent),
  })

  const createOrUpdateClient = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      if (editingClientId) {
        await fetchJson(`/api/v1/clients/${editingClientId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ...clientPayload(), digestThresholds: digestPayload() }),
        })
      } else {
        await fetchJson('/api/v1/clients', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ...clientPayload(), slug: clientForm.slug }),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save client')
    }
  }

  const subtitle = `${clients.length} ${clients.length === 1 ? 'client' : 'clients'}`

  return (
    <>
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Clients</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2.5 sm:flex-nowrap sm:shrink-0">
          <Input
            icon="search"
            placeholder="Search clients"
            className="w-full sm:w-56"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          {canManage && (
            <Button icon="plus" onClick={() => openClientDialog()}>
              Add client
            </Button>
          )}
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {busy && clients.length === 0 ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : (
        <Card pad={false}>
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Client</TableHead>
                  <TableHead>Slug</TableHead>
                  <TableHead className="text-right">Retention</TableHead>
                  <TableHead>Timezone</TableHead>
                  <TableHead className={canManage ? undefined : 'text-right'}>Status</TableHead>
                  {canManage && <TableHead className="text-right">Actions</TableHead>}
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredClients.map((client, index) => (
                  <TableRow key={client.id} last={index === filteredClients.length - 1}>
                    <TableCell className="font-semibold">{client.name}</TableCell>
                    <TableCell mono>{client.slug}</TableCell>
                    <TableCell mono align="right">
                      {client.retentionMonths} mo
                    </TableCell>
                    <TableCell>
                      <span className="text-sm text-secondary">{client.timezone}</span>
                    </TableCell>
                    <TableCell align={canManage ? 'left' : 'right'}>
                      <Badge variant={client.isActive ? 'success' : 'neutral'}>
                        {client.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </TableCell>
                    {canManage && (
                      <TableCell align="right">
                        <Button
                          variant="secondary"
                          size="sm"
                          icon="pencil"
                          onClick={() => openClientDialog(client)}
                        >
                          Edit
                        </Button>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          {filteredClients.length === 0 ? (
            <p className="px-5 py-10 text-center text-sm text-secondary">
              No clients found{search ? ' for the current search' : ''}.
            </p>
          ) : null}
        </Card>
      )}

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingClientId ? 'Edit client' : 'Add client'}</DialogTitle>
            <DialogDescription>
              Manage the agency client account profile and settings.
            </DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={createOrUpdateClient}>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Name
              <Input
                value={clientForm.name}
                onChange={(e) => setClientForm((x) => ({ ...x, name: e.target.value }))}
                required
              />
            </label>
            {editingClientId ? (
              <div className="grid gap-1.5 text-sm font-medium text-body">
                Slug
                <span className="font-mono text-sm text-secondary">{clientForm.slug}</span>
                <span className="text-xs text-faint">
                  Fixed at creation. It is what a configuration import matches this client on.
                </span>
              </div>
            ) : (
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Slug
                <Input
                  mono
                  value={clientForm.slug}
                  onChange={(e) => setClientForm((x) => ({ ...x, slug: e.target.value }))}
                  required
                />
              </label>
            )}
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Timezone
              <Input
                value={clientForm.timezone}
                onChange={(e) => setClientForm((x) => ({ ...x, timezone: e.target.value }))}
                required
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Retention (months)
              <Input
                type="number"
                min={1}
                value={clientForm.retentionMonths}
                onChange={(e) =>
                  setClientForm((x) => ({ ...x, retentionMonths: Number(e.target.value || 27) }))
                }
                required
              />
            </label>
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={clientForm.isActive}
                onChange={(e) => setClientForm((x) => ({ ...x, isActive: e.target.checked }))}
              />
              Active
            </label>
            <label className="flex items-start gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                className="mt-1"
                checked={clientForm.legalHold}
                onChange={(e) => setClientForm((x) => ({ ...x, legalHold: e.target.checked }))}
              />
              <span>
                Legal hold
                <span className="mt-0.5 block text-xs text-faint">
                  Exempts this client from retention purging entirely, whatever the window above.
                </span>
              </span>
            </label>

            <div className="mt-1 border-t border-border pt-3">
              <div className="text-xs font-semibold uppercase tracking-wide text-secondary">Alerting</div>
              <label className="mt-2.5 flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={clientForm.alertsEnabled}
                  onChange={(e) => setClientForm((x) => ({ ...x, alertsEnabled: e.target.checked }))}
                />
                Raise alerts for this client
              </label>
              <div className="mt-2.5 grid grid-cols-1 gap-3 sm:grid-cols-2">
                <label className="flex flex-col gap-1 text-sm text-secondary">
                  Compliance drop (points)
                  <Input
                    type="number"
                    min={1}
                    max={100}
                    placeholder="default"
                    value={clientForm.alertComplianceDropPercent}
                    onChange={(e) =>
                      setClientForm((x) => ({ ...x, alertComplianceDropPercent: e.target.value }))
                    }
                  />
                </label>
                <label className="flex flex-col gap-1 text-sm text-secondary">
                  Minimum messages
                  <Input
                    type="number"
                    min={0}
                    placeholder="default"
                    value={clientForm.alertMinMessages}
                    onChange={(e) => setClientForm((x) => ({ ...x, alertMinMessages: e.target.value }))}
                  />
                </label>
              </div>
              <p className="mt-1.5 text-xs text-faint">
                Leave blank to use the server defaults.
              </p>
            </div>

            {editingClientId ? (
              <div className="border-t border-border pt-3">
                <div className="text-xs font-semibold uppercase tracking-wide text-secondary">
                  Monthly digest
                </div>
                <p className="mt-1 text-xs text-faint">
                  What the digest flags as needing attention for this client. Blank uses the server
                  default shown; 0 turns a trigger off.
                </p>
                <div className="mt-2.5 grid grid-cols-1 gap-3 sm:grid-cols-2">
                  <DigestNumberField
                    label="Pass rate below (%)"
                    max={100}
                    step="any"
                    fallback={digestDefaults?.lowCompliancePercent}
                    value={clientForm.digestLowCompliancePercent}
                    onChange={(v) => setClientForm((x) => ({ ...x, digestLowCompliancePercent: v }))}
                  />
                  <DigestNumberField
                    label="Drop of at least (points)"
                    max={100}
                    step="any"
                    fallback={digestDefaults?.complianceDropPoints}
                    value={clientForm.digestComplianceDropPoints}
                    onChange={(v) => setClientForm((x) => ({ ...x, digestComplianceDropPoints: v }))}
                  />
                  <DigestNumberField
                    label="Minimum messages"
                    fallback={digestDefaults?.minMessages}
                    value={clientForm.digestMinMessages}
                    onChange={(v) => setClientForm((x) => ({ ...x, digestMinMessages: v }))}
                  />
                  <label className="flex flex-col gap-1 text-sm text-secondary">
                    Reports that stop
                    <Select
                      value={clientForm.digestNoReports}
                      onChange={(e) =>
                        setClientForm((x) => ({ ...x, digestNoReports: e.target.value as '' | 'on' | 'off' }))
                      }
                    >
                      <option value="">
                        Default{digestDefaults ? ` (${digestDefaults.noReports ? 'flag' : 'ignore'})` : ''}
                      </option>
                      <option value="on">Flag</option>
                      <option value="off">Ignore</option>
                    </Select>
                  </label>
                  <DigestNumberField
                    label="Suggest a stricter policy after (days)"
                    max={365}
                    fallback={digestDefaults?.tightenAfterDays}
                    value={clientForm.digestTightenAfterDays}
                    onChange={(v) => setClientForm((x) => ({ ...x, digestTightenAfterDays: v }))}
                  />
                  <DigestNumberField
                    label="… at a pass rate of at least (%)"
                    max={100}
                    step="any"
                    fallback={digestDefaults?.tightenCompliancePercent}
                    value={clientForm.digestTightenCompliancePercent}
                    onChange={(v) => setClientForm((x) => ({ ...x, digestTightenCompliancePercent: v }))}
                  />
                </div>
              </div>
            ) : null}
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="secondary" onClick={resetDialog}>
                Cancel
              </Button>
              <Button type="submit">{editingClientId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}

function DigestNumberField({
  label,
  value,
  onChange,
  fallback,
  max,
  step,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  fallback: number | undefined
  max?: number
  step?: string
}) {
  return (
    <label className="flex flex-col gap-1 text-sm text-secondary">
      {label}
      <Input
        type="number"
        min={0}
        max={max}
        step={step}
        placeholder={fallback === undefined ? 'default' : `${fallback} (default)`}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}
