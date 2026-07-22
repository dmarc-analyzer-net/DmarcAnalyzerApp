import { RefreshCw } from 'lucide-react'
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
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import type { Client } from '@/lib/entities'
import { useSystemStatus } from '@/lib/use-system-status'

const initialClientForm = {
  name: '',
  slug: '',
  timezone: 'UTC',
  retentionMonths: 27,
  isActive: true,
}

export function ClientsPage() {
  const status = useSystemStatus()

  const [clients, setClients] = useState<Client[]>([])
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
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
            <Button variant="outline" onClick={() => void loadData()} disabled={busy}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            <Button onClick={() => openClientDialog()}>New Client</Button>
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

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
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
              <Button type="button" variant="outline" onClick={resetDialog}>Cancel</Button>
              <Button type="submit">{editingClientId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
