import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import type { AuditEvent, AuditEventPage, Client } from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'
import { cn } from '@/lib/utils'

const PAGE_SIZE = 100

/** Day ranges worth offering: the endpoint clamps to 730. */
const DAY_OPTIONS = [7, 30, 90, 365, 730] as const

/**
 * The endpoint prefix-matches on the dotted event name, so filtering by the
 * segment before the first dot covers every event in that family without
 * enumerating them.
 */
const EVENT_GROUPS: Array<{ value: string; label: string }> = [
  { value: '', label: 'All activity' },
  { value: 'auth', label: 'Sign-in and sign-out' },
  { value: 'client', label: 'Clients' },
  { value: 'domain', label: 'Domains' },
  { value: 'mailbox_source', label: 'Mailbox sources' },
  { value: 'user', label: 'Users and access' },
  { value: 'alert', label: 'Alert triage' },
  { value: 'notification_recipient', label: 'Notification recipients' },
  { value: 'retention', label: 'Retention purges' },
  { value: 'admin', label: 'Admin operations' },
]

/** Readable label for a dotted event name, falling back to the raw value. */
const EVENT_LABEL: Record<string, string> = {
  'auth.login.succeeded': 'Signed in',
  'auth.login.failed': 'Sign-in failed',
  'auth.logout': 'Signed out',
  'auth.user.registered': 'Registered',
  'client.created': 'Client created',
  'client.updated': 'Client updated',
  'domain.created': 'Domain created',
  'domain.updated': 'Domain updated',
  'mailbox_source.created': 'Mailbox source created',
  'mailbox_source.updated': 'Mailbox source updated',
  'mailbox_source.sync.triggered': 'Sync triggered',
  'user.created': 'User created',
  'user.updated': 'User updated',
  'user.grants.changed': 'Access changed',
  'alert.status.changed': 'Alert triaged',
  'retention.purge.ran': 'Retention purge ran',
  'notification_recipient.added': 'Recipient added',
  'notification_recipient.removed': 'Recipient removed',
  'admin.database.migrated': 'Database migrated',
}

/** Failed sign-ins are the one event worth colouring — everything else is routine. */
function eventTone(eventType: string): 'danger' | 'warning' | 'neutral' {
  if (eventType === 'auth.login.failed') return 'danger'
  if (eventType === 'retention.purge.ran' || eventType.startsWith('admin.')) return 'warning'
  return 'neutral'
}

/**
 * The audit trail: who did what, read-only. There is no write endpoint by
 * design, so this page only ever reads — a trail that can be edited from the
 * console it audits isn't evidence.
 */
export function AuditPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const days = Number(searchParams.get('days') ?? 30)
  const eventType = searchParams.get('event') ?? ''
  const clientId = searchParams.get('clientId') ?? ''

  const [actorInput, setActorInput] = useState(searchParams.get('actor') ?? '')
  const actor = searchParams.get('actor') ?? ''

  const [page, setPage] = useState<AuditEventPage | null>(null)
  const [clients, setClients] = useState<Client[]>([])
  const [offset, setOffset] = useState(0)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const requestSeq = useRef(0)

  // Filters are URL state so a findable trail stays linkable — an auditor
  // pasting "who changed this client last month" into a ticket is the point.
  const setFilter = (key: string, value: string) => {
    setOffset(0)
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev)
        if (value) next.set(key, value)
        else next.delete(key)
        return next
      },
      { replace: true },
    )
  }

  const loadData = useCallback(async () => {
    const seq = ++requestSeq.current
    setBusy(true)
    setError(null)
    try {
      const query = new URLSearchParams({
        days: String(days),
        limit: String(PAGE_SIZE),
        offset: String(offset),
      })
      if (eventType) query.set('eventType', eventType)
      if (actor) query.set('actor', actor)
      if (clientId) query.set('clientId', clientId)

      const payload = await fetchJson<AuditEventPage>(`/api/v1/admin/audit-events?${query}`)
      if (seq === requestSeq.current) setPage(payload)
    } catch (loadError) {
      if (seq === requestSeq.current) {
        setError(loadError instanceof Error ? loadError.message : 'Failed to load the audit trail')
      }
    } finally {
      if (seq === requestSeq.current) setBusy(false)
    }
  }, [days, eventType, actor, clientId, offset])

  useEffect(() => {
    void loadData()
  }, [loadData])

  useEffect(() => {
    void fetchJson<Client[]>('/api/v1/clients')
      .then(setClients)
      .catch(() => setClients([]))
  }, [])

  const items = page?.items ?? []
  const total = page?.total ?? 0
  const rangeStart = total === 0 ? 0 : offset + 1
  const rangeEnd = offset + items.length

  return (
    <>
      <div className="mb-5">
        <h1 className="text-xl font-semibold tracking-tight text-body">Audit trail</h1>
        <p className="mt-1 text-sm text-secondary">
          Who did what, and when. Read-only — entries cannot be edited or deleted.
        </p>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      <Card pad className="mb-3.5">
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex min-w-[150px] flex-col gap-1.5">
            <span className="text-xs font-medium text-secondary">Period</span>
            <Select value={String(days)} onChange={(e) => setFilter('days', e.target.value)}>
              {DAY_OPTIONS.map((option) => (
                <option key={option} value={option}>
                  Last {option} days
                </option>
              ))}
            </Select>
          </label>
          <label className="flex min-w-[200px] flex-col gap-1.5">
            <span className="text-xs font-medium text-secondary">Activity</span>
            <Select value={eventType} onChange={(e) => setFilter('event', e.target.value)}>
              {EVENT_GROUPS.map((group) => (
                <option key={group.value} value={group.value}>
                  {group.label}
                </option>
              ))}
            </Select>
          </label>
          <label className="flex min-w-[180px] flex-col gap-1.5">
            <span className="text-xs font-medium text-secondary">Client</span>
            <Select value={clientId} onChange={(e) => setFilter('clientId', e.target.value)}>
              <option value="">All clients</option>
              {clients.map((client) => (
                <option key={client.id} value={client.id}>
                  {client.name}
                </option>
              ))}
            </Select>
          </label>
          <form
            className="flex min-w-[220px] flex-1 flex-col gap-1.5"
            onSubmit={(e) => {
              e.preventDefault()
              setFilter('actor', actorInput.trim())
            }}
          >
            <span className="text-xs font-medium text-secondary">Actor</span>
            <div className="flex gap-2">
              <Input
                value={actorInput}
                onChange={(e) => setActorInput(e.target.value)}
                placeholder="email contains…"
              />
              <Button type="submit" variant="secondary" size="sm">
                <Icon name="search" size={14} />
                Find
              </Button>
            </div>
          </form>
        </div>
      </Card>

      {page === null && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {page ? (
        <div className={cn('transition-opacity', busy && 'opacity-60')}>
          <Card>
            <div className="flex items-start justify-between gap-3 px-5 pt-5">
              <CardHeader
                title="Activity"
                description="Newest first. Select a row to see the full detail recorded with it."
              />
              <Badge variant="neutral">
                {total === 0
                  ? 'none'
                  : `${rangeStart.toLocaleString('en-US')}–${rangeEnd.toLocaleString('en-US')} of ${total.toLocaleString('en-US')}`}
              </Badge>
            </div>

            {items.length === 0 ? (
              <div className="flex flex-col items-center gap-2 px-5 pb-10 pt-4 text-center">
                <Icon name="file-text" size={32} className="text-secondary" />
                <p className="text-sm font-semibold text-body">Nothing recorded</p>
                <p className="max-w-md text-sm text-secondary">
                  No audit events match these filters in this period.
                </p>
              </div>
            ) : (
              <div className="overflow-x-auto">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>When</TableHead>
                      <TableHead>Actor</TableHead>
                      <TableHead>Event</TableHead>
                      <TableHead>What happened</TableHead>
                      <TableHead>Client</TableHead>
                      <TableHead>IP</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {items.map((event) => (
                      <AuditRow
                        key={event.id}
                        event={event}
                        expanded={expanded === event.id}
                        onToggle={() => setExpanded(expanded === event.id ? null : event.id)}
                      />
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}

            {total > PAGE_SIZE ? (
              <div className="flex items-center justify-between gap-3 border-t border-border px-5 py-3">
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={offset === 0 || busy}
                  onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
                >
                  <Icon name="chevron-left" size={14} />
                  Newer
                </Button>
                <span className="text-xs text-secondary">
                  Page {Math.floor(offset / PAGE_SIZE) + 1} of {Math.ceil(total / PAGE_SIZE)}
                </span>
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={rangeEnd >= total || busy}
                  onClick={() => setOffset(offset + PAGE_SIZE)}
                >
                  Older
                  <Icon name="chevron-right" size={14} />
                </Button>
              </div>
            ) : null}
          </Card>
        </div>
      ) : null}
    </>
  )
}

/** Shows `::ffff:172.19.0.1` as `172.19.0.1`. Kestrel reports IPv4 peers in
 * IPv4-mapped IPv6 form behind Docker's bridge, which is the same address with
 * six characters of noise in front of it. */
function formatIpAddress(ip: string | null | undefined): string {
  if (!ip) return '—'
  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i.exec(ip)
  return mapped ? mapped[1] : ip
}

function AuditRow({
  event,
  expanded,
  onToggle,
}: {
  event: AuditEvent
  expanded: boolean
  onToggle: () => void
}) {
  const hasDetail = Boolean(event.details || event.userAgent || event.targetId)
  const label = EVENT_LABEL[event.eventType] ?? event.eventType

  return (
    <>
      {/* TableRow already applies cursor-pointer when onClick is set. */}
      <TableRow onClick={hasDetail ? onToggle : undefined}>
        <TableCell className="whitespace-nowrap text-xs text-secondary">
          {formatRelativeOrDate(event.occurredAtUtc)}
        </TableCell>
        <TableCell className="whitespace-nowrap text-sm text-body">
          {event.actorEmail || <span className="text-secondary">{event.actorType}</span>}
        </TableCell>
        <TableCell className="whitespace-nowrap">
          <Badge variant={eventTone(event.eventType)}>{label}</Badge>
        </TableCell>
        <TableCell>
          <span className="text-sm text-body">{event.summary}</span>
          {hasDetail ? (
            <Icon
              name={expanded ? 'chevron-down' : 'chevron-right'}
              size={12}
              className="ml-1.5 inline text-secondary"
              aria-hidden
            />
          ) : null}
        </TableCell>
        <TableCell className="whitespace-nowrap text-sm text-secondary">
          {event.clientName ?? (event.clientId ? <span className="italic">deleted</span> : '—')}
        </TableCell>
        <TableCell className="whitespace-nowrap font-mono text-xs text-secondary">
          {formatIpAddress(event.ipAddress)}
        </TableCell>
      </TableRow>
      {expanded ? (
        <TableRow>
          <TableCell colSpan={6} className="bg-surface-sunken">
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 py-1 text-xs">
              <dt className="font-medium text-secondary">Event</dt>
              <dd className="font-mono text-body">{event.eventType}</dd>
              {event.targetType ? (
                <>
                  <dt className="font-medium text-secondary">Target</dt>
                  <dd className="font-mono text-body">
                    {/* The summary already says what happened, so the id earns its
                        place only by being actionable or exact. Domains are the one
                        entity with a detail route, so those link; everything else
                        keeps the id verbatim for correlating against other records,
                        de-emphasised because it is a reference, not the content. */}
                    {event.targetType === 'domain' && event.targetId ? (
                      <Link
                        to={`/domains/${event.targetId}`}
                        className="text-body underline decoration-dotted underline-offset-2 hover:text-brand"
                      >
                        {event.targetType} · {event.targetId}
                      </Link>
                    ) : (
                      <>
                        {event.targetType}
                        {event.targetId ? <span className="text-faint"> · {event.targetId}</span> : null}
                      </>
                    )}
                  </dd>
                </>
              ) : null}
              {event.details ? (
                <>
                  <dt className="font-medium text-secondary">Details</dt>
                  <dd className="whitespace-pre-wrap break-words text-body">{event.details}</dd>
                </>
              ) : null}
              {event.userAgent ? (
                <>
                  <dt className="font-medium text-secondary">User agent</dt>
                  <dd className="break-words text-body">{event.userAgent}</dd>
                </>
              ) : null}
            </dl>
          </TableCell>
        </TableRow>
      ) : null}
    </>
  )
}
