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
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type { Client } from '@/lib/entities'

const initialClientForm = {
  name: '',
  slug: '',
  timezone: 'UTC',
  retentionMonths: 27,
  isActive: true,
}

export function ClientsPage() {
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingClientId, setEditingClientId] = useState<string | null>(null)
  const [clientForm, setClientForm] = useState(initialClientForm)

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
      })
    } else {
      setEditingClientId(null)
      setClientForm(initialClientForm)
    }
  }

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

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save client')
    }
  }

  const subtitle = `${clients.length} ${clients.length === 1 ? 'client' : 'clients'}`

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Clients</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex shrink-0 items-center gap-2.5">
          <Input
            icon="search"
            placeholder="Search clients"
            className="w-56"
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
        <Card pad={false} className="overflow-hidden">
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
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Slug
              <Input
                mono
                value={clientForm.slug}
                onChange={(e) => setClientForm((x) => ({ ...x, slug: e.target.value }))}
                required
              />
            </label>
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
