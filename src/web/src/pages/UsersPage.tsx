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
import { USER_ROLES, type UserRole } from '@/lib/auth-context'
import type { Client, ManagedUser } from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'

type BadgeVariant = 'brand' | 'neutral'

type RoleMeta = {
  label: string
  badgeVariant: BadgeVariant
}

const ROLE_META: Record<UserRole, RoleMeta> = {
  agency_admin: { label: 'Admin', badgeVariant: 'brand' },
  agency_analyst: { label: 'Staff', badgeVariant: 'neutral' },
  client_viewer: { label: 'Client viewer', badgeVariant: 'neutral' },
}

/** Unknown roles from a newer server render as-is with the neutral style. */
const getRoleMeta = (role: string): RoleMeta =>
  ROLE_META[role as UserRole] ?? { label: role, badgeVariant: 'neutral' as const }

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
  const [users, setUsers] = useState<ManagedUser[]>([])
  const [clients, setClients] = useState<Client[]>([])
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(true)
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

  const subtitle = `${users.length} ${users.length === 1 ? 'user' : 'users'} · roles gate what each account can see`

  return (
    <>
      <div className="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Users</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex shrink-0 items-center gap-2.5">
          <Input
            icon="search"
            placeholder="Search users"
            className="w-56"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <Button icon="plus" onClick={openCreateDialog}>
            Add user
          </Button>
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {busy && users.length === 0 ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : (
        <Card pad={false} className="overflow-hidden">
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>User</TableHead>
                  <TableHead>Role</TableHead>
                  <TableHead>Client access</TableHead>
                  <TableHead>Last login</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredUsers.map((account, index) => {
                  const roleMeta = getRoleMeta(account.role)
                  return (
                    <TableRow key={account.id} last={index === filteredUsers.length - 1}>
                      <TableCell>
                        <div className="flex flex-col">
                          <span className="text-sm font-semibold text-body">
                            {account.displayName}
                          </span>
                          <span className="font-mono text-xs text-secondary">{account.email}</span>
                        </div>
                      </TableCell>
                      <TableCell>
                        <Badge variant={roleMeta.badgeVariant}>{roleMeta.label}</Badge>
                      </TableCell>
                      <TableCell>
                        {account.role === 'client_viewer' ? (
                          account.grantedClientIds.length === 0 ? (
                            <Badge variant="warning">No clients granted</Badge>
                          ) : (
                            <span className="text-sm text-secondary">
                              {account.grantedClientIds
                                .map((clientId) => clientNameById.get(clientId) ?? clientId)
                                .join(' · ')}
                            </span>
                          )
                        ) : (
                          <span className="text-sm text-secondary">All clients</span>
                        )}
                      </TableCell>
                      <TableCell className="whitespace-nowrap">
                        <span className="text-sm text-secondary">
                          {formatRelativeOrDate(account.lastLoginAtUtc)}
                        </span>
                      </TableCell>
                      <TableCell>
                        <Badge variant={account.isActive ? 'success' : 'neutral'}>
                          {account.isActive ? 'Active' : 'Inactive'}
                        </Badge>
                      </TableCell>
                      <TableCell align="right">
                        <Button
                          variant="secondary"
                          size="sm"
                          icon="pencil"
                          onClick={() => openEditDialog(account)}
                        >
                          Edit
                        </Button>
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </div>
          {filteredUsers.length === 0 ? (
            <p className="px-5 py-10 text-center text-sm text-secondary">
              No users found{search ? ' for the current search' : ''}.
            </p>
          ) : null}
        </Card>
      )}

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingUserId ? 'Edit user' : 'Add user'}</DialogTitle>
            <DialogDescription>
              {editingUserId
                ? 'Update profile, role, status, and client access.'
                : 'Add a console user and assign their role.'}
            </DialogDescription>
          </DialogHeader>
          {editingUserId ? (
            <form className="grid gap-4" onSubmit={updateUser}>
              {dialogError ? (
                <p className="rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
                  {dialogError}
                </p>
              ) : null}
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Display name
                <Input
                  value={editForm.displayName}
                  onChange={(e) => setEditForm((x) => ({ ...x, displayName: e.target.value }))}
                  required
                />
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Role
                <Select
                  aria-label="Role"
                  value={editForm.role}
                  onChange={(e) => setEditForm((x) => ({ ...x, role: e.target.value }))}
                >
                  <RoleOptions currentRole={editForm.role} />
                </Select>
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                New password (optional)
                <Input
                  type="password"
                  autoComplete="new-password"
                  value={editForm.password}
                  onChange={(e) => setEditForm((x) => ({ ...x, password: e.target.value }))}
                />
              </label>
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={editForm.isActive}
                  onChange={(e) => setEditForm((x) => ({ ...x, isActive: e.target.checked }))}
                />
                Active
              </label>
              {editForm.role === 'client_viewer' && (
                <div className="grid gap-1.5">
                  <p className="text-sm font-medium text-body">Client access</p>
                  <div className="max-h-48 overflow-y-auto rounded-md border border-border p-2">
                    {sortedClients.length === 0 ? (
                      <p className="px-1 py-1 text-xs text-secondary">No clients available.</p>
                    ) : (
                      sortedClients.map((client) => (
                        <label
                          key={client.id}
                          className="flex items-center gap-2 px-1 py-1 text-sm text-body"
                        >
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
                  <p className="text-xs text-secondary">
                    This user only sees data for the checked clients.
                  </p>
                </div>
              )}
              <div className="flex justify-end gap-2 pt-1">
                <Button type="button" variant="secondary" onClick={resetDialog}>
                  Cancel
                </Button>
                <Button type="submit" disabled={saving}>
                  Save
                </Button>
              </div>
            </form>
          ) : (
            <form className="grid gap-4" onSubmit={createUser}>
              {dialogError ? (
                <p className="rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
                  {dialogError}
                </p>
              ) : null}
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Email
                <Input
                  type="email"
                  autoComplete="off"
                  value={createForm.email}
                  onChange={(e) => setCreateForm((x) => ({ ...x, email: e.target.value }))}
                  required
                />
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Display name
                <Input
                  value={createForm.displayName}
                  onChange={(e) => setCreateForm((x) => ({ ...x, displayName: e.target.value }))}
                  required
                />
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Password
                <Input
                  type="password"
                  autoComplete="new-password"
                  value={createForm.password}
                  onChange={(e) => setCreateForm((x) => ({ ...x, password: e.target.value }))}
                  required
                />
              </label>
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Role
                <Select
                  aria-label="Role"
                  value={createForm.role}
                  onChange={(e) => setCreateForm((x) => ({ ...x, role: e.target.value as UserRole }))}
                >
                  <RoleOptions />
                </Select>
              </label>
              {createForm.role === 'client_viewer' && (
                <p className="text-xs text-secondary">
                  Client viewers start with no client access — grant clients from the edit dialog
                  after creating the user.
                </p>
              )}
              <div className="flex justify-end gap-2 pt-1">
                <Button type="button" variant="secondary" onClick={resetDialog}>
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
