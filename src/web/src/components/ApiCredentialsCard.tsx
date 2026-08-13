import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'

import { Notice } from '@/components/Notice'
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
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import type { ApiCredential, IssuedApiCredential, ReportSource } from '@/lib/entities'

/**
 * Issue and revoke the credentials that let an external system push reports in.
 *
 * The token is shown once, in a dialog, and then never again — the server keeps only its
 * hash. That is the whole reason this lives in the console rather than being curl-only:
 * reveal-once is only usable if the person who asked for the credential is looking at the
 * screen when it appears.
 */
export function ApiCredentialsCard({ sources }: { sources: ReportSource[] }) {
  const [credentials, setCredentials] = useState<ApiCredential[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const [issuing, setIssuing] = useState(false)
  const [name, setName] = useState('')
  const [sourceId, setSourceId] = useState('')
  const [issued, setIssued] = useState<IssuedApiCredential | null>(null)
  const [copied, setCopied] = useState(false)

  const apiSources = useMemo(() => sources.filter((s) => s.protocol === 'api'), [sources])

  const load = useCallback(async () => {
    try {
      setCredentials(await fetchJson<ApiCredential[]>('/api/v1/api-credentials'))
      setError(null)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load credentials')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!sourceId && apiSources.length > 0) setSourceId(apiSources[0].id)
  }, [apiSources, sourceId])

  async function issue(event: FormEvent) {
    event.preventDefault()
    if (!sourceId || name.trim().length === 0) return

    try {
      const result = await fetchJson<IssuedApiCredential>('/api/v1/api-credentials', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reportSourceId: sourceId, name: name.trim() }),
      })
      setIssued(result)
      setCopied(false)
      setName('')
      setIssuing(false)
      await load()
    } catch (issueError) {
      setError(issueError instanceof Error ? issueError.message : 'Failed to issue credential')
    }
  }

  async function revoke(credential: ApiCredential) {
    // Irreversible for the holder: anything using this token stops working immediately.
    if (!window.confirm(`Revoke "${credential.name}"? Anything using it stops ingesting at once.`)) {
      return
    }

    try {
      await fetchJson(`/api/v1/api-credentials/${credential.id}/revoke`, { method: 'POST' })
      await load()
    } catch (revokeError) {
      setError(revokeError instanceof Error ? revokeError.message : 'Failed to revoke credential')
    }
  }

  if (apiSources.length === 0 && credentials.length === 0) {
    return null
  }

  return (
    <Card pad={false} className="mt-3.5">
      <div className="flex flex-wrap items-start justify-between gap-3 px-5 pt-4 pb-2">
        <CardHeader
          title="Machine credentials"
          description="Tokens that let an external system push reports to an API source."
        />
        {apiSources.length > 0 ? (
          <Button onClick={() => setIssuing(true)}>Issue credential</Button>
        ) : null}
      </div>

      {error ? (
        <div className="px-5 pb-3">
          <Notice tone="danger">{error}</Notice>
        </div>
      ) : null}

      {loading ? (
        <p className="px-5 py-8 text-center text-sm text-secondary">Loading…</p>
      ) : credentials.length === 0 ? (
        <p className="px-5 py-8 text-center text-sm text-secondary">
          No credentials yet. Issue one to let a system push reports in.
        </p>
      ) : (
        <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Source</TableHead>
                <TableHead>Token id</TableHead>
                <TableHead>Last used</TableHead>
                <TableHead>Status</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {credentials.map((credential) => (
                <TableRow key={credential.id}>
                  <TableCell className="font-medium text-body">{credential.name}</TableCell>
                  <TableCell>{credential.reportSourceName ?? '—'}</TableCell>
                  <TableCell className="font-mono text-xs">{credential.tokenId}</TableCell>
                  <TableCell>
                    {credential.lastUsedAtUtc
                      ? new Date(credential.lastUsedAtUtc).toLocaleString()
                      : 'never'}
                  </TableCell>
                  <TableCell>
                    {credential.isUsable ? (
                      <Badge variant="success">active</Badge>
                    ) : (
                      <Badge variant="muted">{credential.revokedAtUtc ? 'revoked' : 'expired'}</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-right">
                    {credential.isUsable ? (
                      <Button variant="ghost" onClick={() => void revoke(credential)}>
                        Revoke
                      </Button>
                    ) : null}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <Dialog open={issuing} onOpenChange={setIssuing}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Issue machine credential</DialogTitle>
            <DialogDescription>
              The token is shown once and cannot be retrieved afterwards.
            </DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={(event) => void issue(event)}>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Name
              <Input
                value={name}
                onChange={(event) => setName(event.target.value)}
                placeholder="mail-gateway-production"
                required
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Report source
              <Select value={sourceId} onChange={(event) => setSourceId(event.target.value)}>
                {apiSources.map((source) => (
                  <option key={source.id} value={source.id}>
                    {source.name}
                  </option>
                ))}
              </Select>
            </label>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={() => setIssuing(false)}>
                Cancel
              </Button>
              <Button type="submit">Issue</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={issued !== null} onOpenChange={(open) => (!open ? setIssued(null) : null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Copy this token now</DialogTitle>
            <DialogDescription>
              It is not stored and cannot be shown again. If it is lost, revoke this
              credential and issue another.
            </DialogDescription>
          </DialogHeader>
          <div className="grid gap-3">
            <code className="block overflow-x-auto rounded-md bg-surface-muted p-3 font-mono text-xs text-body">
              {issued?.token}
            </code>
            <div className="flex justify-end gap-2">
              <Button
                variant="ghost"
                onClick={() => {
                  if (issued) {
                    void navigator.clipboard.writeText(issued.token)
                    setCopied(true)
                  }
                }}
              >
                {copied ? 'Copied' : 'Copy'}
              </Button>
              <Button onClick={() => setIssued(null)}>Done</Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
