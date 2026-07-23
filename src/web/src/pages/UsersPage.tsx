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
import { USER_ROLES, type UserRole } from '@/lib/auth-context'
import type { Client, ManagedUser } from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'
import { useSystemStatus } from '@/lib/use-system-status'

type RoleMeta = {
  label: string
  badgeVariant: 'default' | 'muted'
  /** Extra badge classes; used to give the analyst badge its blue tone. */
  badgeClassName?: string
}

const ROLE_META: Record<UserRole, RoleMeta> = {
  agency_admin: { label: 'Admin', badgeVariant: 'default' },
  agency_analyst: {
    label: 'Analyst',
    badgeVariant: 'default',
    badgeClassName: 'border-sky-200 bg-sky-50 text-sky-700',
  },
  client_viewer: { label: 'Client viewer', badgeVariant: 'muted' },
}

/** Unknown roles from a newer server render as-is with the muted style. */
const getRoleMeta = (role: string): RoleMeta =>
  ROLE_META[role as UserRole] ?? { label: role, badgeVariant: 'muted' as const }

const isKnownRole = (role: string): role is UserRole => (USER_ROLES as readonly string[]).includes(role)

const initialCreateForm = {
  email: '',
  password: '',
  displayName: '',
  role: 'client_viewer' as UserRole,
}

const initialEditForm = {
  displayName: '',
  role: 'client_viewer' as ManagedUser['role'],
  isActive: true,
  password: '',
  clientIds: [] as string[],
}

function RoleOptions({ currentRole }: { currentRole?: string }) {
  return (
    <>
      {/* Keeps an unknown role selectable so opening the dialog never silently rewrites it. */}
      {currentRole !== undefined && !isKnownRole(currentRole) && (
        <option value={currentRole}>{currentRole}</option>
      )}
      <option value="agency_admin">Agency admin</option>
      <option value="agency_analyst">Agency analyst</option>
      <option value="client_viewer">Client viewer</option>
    </>
  )
}

export function UsersPage() {
  const status = useSystemStatus()

  const [users, setUsers] = useState<ManagedUser[]>([])
  const [clients, setClients] = useState<Client[]>([])
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingUserId, setEditingUserId] = useState<string | null>(null)
  const [createForm, setCreateForm] = useState(initialCreateForm)
  const [editForm, setEditForm] = useState(initialEditForm)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [userData, clientData] = await Promise.all([
        fetchJson<ManagedUser[]>('/api/v1/users'),
        fetchJson<Client[]>('/api/v1/clients'),
      ])
      setUsers(userData)
      setClients(clientData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load users')
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

  const clientNameById = useMemo(
    () => new Map(clients.map((client) => [client.id, client.name])),
    [clients],
  )

  const sortedUsers = useMemo(
    () => [...users].sort((a, b) => a.email.localeCompare(b.email)),
    [users],
  )

  const filteredUsers = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return sortedUsers
    return sortedUsers.filter(
      (x) =>
        x.email.toLowerCase().includes(q) ||
        x.displayName.toLowerCase().includes(q) ||
        getRoleMeta(x.role).label.toLowerCase().includes(q),
    )
  }, [search, sortedUsers])

  const resetDialog = () => {
    setDialogOpen(false)
    setEditingUserId(null)
    setCreateForm(initialCreateForm)
    setEditForm(initialEditForm)
    setDialogError(null)
    setSaving(false)
  }

  const openCreateDialog = () => {
    setDialogError(null)
    setEditingUserId(null)
    setCreateForm(initialCreateForm)
    setDialogOpen(true)
  }

  const openEditDialog = (account: ManagedUser) => {
    setDialogError(null)
    setEditingUserId(account.id)
    setEditForm({
      displayName: account.displayName,
      role: account.role,
      isActive: account.isActive,
      password: '',
      clientIds: account.grantedClientIds,
    })
    setDialogOpen(true)
  }

  const toggleClientGrant = (clientId: string) => {
    setEditForm((x) => ({
      ...x,
      clientIds: x.clientIds.includes(clientId)
        ? x.clientIds.filter((id) => id !== clientId)
        : [...x.clientIds, clientId],
    }))
  }

  const createUser = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setDialogError(null)
    setSaving(true)
    try {
      await fetchJson('/api/v1/users', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(createForm),
      })
      resetDialog()
      await loadData()
    } catch (saveError) {
      // Surfaces server validation errors (400) directly in the dialog.
      setDialogError(saveError instanceof Error ? saveError.message : 'Failed to create user')
      setSaving(false)
    }
  }

  const updateUser = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!editingUserId) return
    setDialogError(null)
    setSaving(true)
    try {
      const patch: Record<string, unknown> = {
        displayName: editForm.displayName,
        role: editForm.role,
        isActive: editForm.isActive,
      }
      if (editForm.password) patch.password = editForm.password

      await fetchJson(`/api/v1/users/${editingUserId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(patch),
      })

      // Grants only apply to client viewers; the server rejects non-empty
      // grants for any other role, so the PUT is skipped entirely for them.
      if (editForm.role === 'client_viewer') {
        await fetchJson(`/api/v1/users/${editingUserId}/grants`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ clientIds: editForm.clientIds }),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      // Shows 409 (last-admin guard) and 400 (grant validation) messages in
      // the dialog; the background refresh reflects any partially applied save.
      setDialogError(saveError instanceof Error ? saveError.message : 'Failed to save user')
      setSaving(false)
      void loadData()
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
          <CardTitle>Users</CardTitle>
          <div className="flex items-center gap-3">
            <Badge variant="muted">{filteredUsers.length} records</Badge>
            <Button onClick={openCreateDialog} disabled={busy}>
              New User
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Email</TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Role</TableHead>
                <TableHead>Client access</TableHead>
                <TableHead>Active</TableHead>
                <TableHead>Last login</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filteredUsers.map((account) => {
                const roleMeta = getRoleMeta(account.role)
                return (
                  <TableRow key={account.id}>
                    <TableCell className="font-medium">{account.email}</TableCell>
                    <TableCell>{account.displayName}</TableCell>
                    <TableCell>
                      <Badge variant={roleMeta.badgeVariant} className={roleMeta.badgeClassName}>
                        {roleMeta.label}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      {account.role === 'client_viewer' ? (
                        account.grantedClientIds.length === 0 ? (
                          <Badge variant="warning">No clients granted</Badge>
                        ) : (
                          <div className="flex max-w-[280px] flex-wrap gap-1">
                            {account.grantedClientIds.map((clientId) => (
                              <Badge key={clientId} variant="muted" className="px-2 text-[11px]">
                                {clientNameById.get(clientId) ?? clientId}
                              </Badge>
                            ))}
                          </div>
                        )
                      ) : (
                        <span className="text-muted-foreground">All clients</span>
                      )}
                    </TableCell>
                    <TableCell>
                      <Badge variant={account.isActive ? 'success' : 'muted'}>
                        {account.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-muted-foreground">
                      {formatRelativeOrDate(account.lastLoginAtUtc)}
                    </TableCell>
                    <TableCell className="text-right">
                      <Button variant="outline" size="sm" onClick={() => openEditDialog(account)}>
                        Edit
                      </Button>
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
          {filteredUsers.length === 0 && !busy && (
            <p className="py-6 text-center text-sm text-muted-foreground">
              No users found{search ? ' for the current search' : ''}.
            </p>
          )}
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingUserId ? 'Edit User' : 'Create User'}</DialogTitle>
            <DialogDescription>
              {editingUserId
                ? 'Update profile, role, status, and client access.'
                : 'Add a console user and assign their role.'}
            </DialogDescription>
          </DialogHeader>
          {editingUserId ? (
            <form className="grid gap-3" onSubmit={updateUser}>
              {!!dialogError && (
                <p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {dialogError}
                </p>
              )}
              <Input
                placeholder="Display name"
                value={editForm.displayName}
                onChange={(e) => setEditForm((x) => ({ ...x, displayName: e.target.value }))}
                required
              />
              <Select
                aria-label="Role"
                value={editForm.role}
                onChange={(e) => setEditForm((x) => ({ ...x, role: e.target.value }))}
              >
                <RoleOptions currentRole={editForm.role} />
              </Select>
              <Input
                type="password"
                autoComplete="new-password"
                placeholder="New password (optional)"
                value={editForm.password}
                onChange={(e) => setEditForm((x) => ({ ...x, password: e.target.value }))}
              />
              <label className="text-sm text-muted-foreground">
                <input
                  className="mr-2"
                  type="checkbox"
                  checked={editForm.isActive}
                  onChange={(e) => setEditForm((x) => ({ ...x, isActive: e.target.checked }))}
                />
                Active
              </label>
              {editForm.role === 'client_viewer' && (
                <div className="grid gap-1.5">
                  <p className="text-sm font-medium">Client access</p>
                  <div className="max-h-48 overflow-y-auto rounded-md border border-input p-2">
                    {sortedClients.length === 0 ? (
                      <p className="px-1 py-1 text-xs text-muted-foreground">No clients available.</p>
                    ) : (
                      sortedClients.map((client) => (
                        <label key={client.id} className="flex items-center gap-2 px-1 py-1 text-sm">
                          <input
                            type="checkbox"
                            checked={editForm.clientIds.includes(client.id)}
                            onChange={() => toggleClientGrant(client.id)}
                          />
                          {client.name}
                        </label>
                      ))
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    This user only sees data for the checked clients.
                  </p>
                </div>
              )}
              <div className="flex justify-end gap-2 pt-1">
                <Button type="button" variant="outline" onClick={resetDialog}>
                  Cancel
                </Button>
                <Button type="submit" disabled={saving}>
                  Save
                </Button>
              </div>
            </form>
          ) : (
            <form className="grid gap-3" onSubmit={createUser}>
              {!!dialogError && (
                <p className="rounded-md border border-destructive/25 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {dialogError}
                </p>
              )}
              <Input
                type="email"
                autoComplete="off"
                placeholder="Email"
                value={createForm.email}
                onChange={(e) => setCreateForm((x) => ({ ...x, email: e.target.value }))}
                required
              />
              <Input
                placeholder="Display name"
                value={createForm.displayName}
                onChange={(e) => setCreateForm((x) => ({ ...x, displayName: e.target.value }))}
                required
              />
              <Input
                type="password"
                autoComplete="new-password"
                placeholder="Password"
                value={createForm.password}
                onChange={(e) => setCreateForm((x) => ({ ...x, password: e.target.value }))}
                required
              />
              <Select
                aria-label="Role"
                value={createForm.role}
                onChange={(e) => setCreateForm((x) => ({ ...x, role: e.target.value as UserRole }))}
              >
                <RoleOptions />
              </Select>
              {createForm.role === 'client_viewer' && (
                <p className="text-xs text-muted-foreground">
                  Client viewers start with no client access — grant clients from the Edit dialog
                  after creating the user.
                </p>
              )}
              <div className="flex justify-end gap-2 pt-1">
                <Button type="button" variant="outline" onClick={resetDialog}>
                  Cancel
                </Button>
                <Button type="submit" disabled={saving}>
                  Create
                </Button>
              </div>
            </form>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
