import { useCallback, useEffect, useState } from 'react'
import type { ReactNode } from 'react'

import { ConfigImportPanel } from '@/components/ConfigImportPanel'
import { Notice } from '@/components/Notice'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { fetchJson } from '@/lib/api'
import type { BackupStatus, ConfigImportPreview } from '@/lib/entities'
import { formatRelativeOrDate, formatUtcDateTime } from '@/lib/format'

/**
 * Backup and recovery, in one admin page rather than a card bolted onto an existing
 * one. Status, export and import are the same subject read three ways — "is a copy
 * being made", "make one now", "put one back" — and an operator mid-incident should
 * not have to know which other page each half lives on. It also gives the
 * unprotected-credentials warning somewhere to be first-class instead of a footnote
 * under someone else's heading.
 */
export function BackupPage() {
  const [status, setStatus] = useState<BackupStatus | null>(null)
  const [preview, setPreview] = useState<ConfigImportPreview | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [allowPlaintext, setAllowPlaintext] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)
  const [exportNotice, setExportNotice] = useState<string | null>(null)

  /**
   * Set when an import replaced this operator's own password hash. Their session is
   * already gone server-side, so every button on this page that would call the API
   * is now a button that returns 401 — and a 401 on a non-auth path force-logs-out
   * the console, taking the import result off screen before it has been read.
   */
  const [sessionEnded, setSessionEnded] = useState(false)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [statusData, previewData] = await Promise.all([
        fetchJson<BackupStatus>('/api/v1/admin/backup/status'),
        fetchJson<ConfigImportPreview>('/api/v1/admin/config/import/preview'),
      ])
      setStatus(statusData)
      setPreview(previewData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load backup status')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void loadData()
  }, [loadData])

  /**
   * Downloaded by hand rather than through `fetchJson`, which parses every response
   * as JSON: this endpoint answers with a file. The failure path still has to be
   * read as JSON, because the export refuses outright when credentials are
   * unprotected and says so in a flat `{ error }`.
   */
  const downloadExport = async () => {
    setExporting(true)
    setExportError(null)
    setExportNotice(null)
    try {
      const query = allowPlaintext ? '?allowPlaintextCredentials=true' : ''
      const response = await fetch(`/api/v1/admin/config/export${query}`, {
        credentials: 'include',
      })

      if (!response.ok) {
        let message = `Export failed (${response.status})`
        try {
          const payload = (await response.json()) as { error?: string }
          if (payload.error) message = payload.error
        } catch {
          // A non-JSON error body leaves the status code as the only fact.
        }
        throw new Error(message)
      }

      // The endpoint names the file after the export date, which is what makes a
      // directory of these legible six months later; keep its name rather than
      // inventing one.
      const disposition = response.headers.get('Content-Disposition') ?? ''
      const named = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
      const name = named?.[1] ?? `dmarc-config-${new Date().toISOString().slice(0, 10)}.json`

      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = name
      anchor.rel = 'noopener'
      document.body.append(anchor)
      anchor.click()
      anchor.remove()
      // Revoking in the same task can cancel the download before the browser has
      // finished reading the blob.
      window.setTimeout(() => URL.revokeObjectURL(url), 0)

      setExportNotice(`Downloaded ${name}. Store it as you would a database dump.`)
    } catch (downloadError) {
      setExportError(
        downloadError instanceof Error ? downloadError.message : 'The export did not complete.',
      )
    } finally {
      setExporting(false)
    }
  }

  const exportBlocked = status != null && !status.credentialsProtected && !allowPlaintext

  return (
    <>
      <div className="mb-5">
        <h1 className="font-display text-xl font-bold tracking-tight text-body">
          Backup and recovery
        </h1>
        <p className="mt-1 text-sm text-secondary">
          Configuration is the part of this install a human typed. Report data arrived over IMAP and
          can arrive again.
        </p>
      </div>

      {error ? <Notice tone="danger" className="mb-3.5">{error}</Notice> : null}

      {status === null && busy ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : null}

      {status ? (
        <div className="grid gap-3.5">
          {/* First, and in the danger tone, because this is the dev default: an
              operator who never chose it has to be told, and every artifact this
              install produces carries plaintext passwords until it is fixed. */}
          {!status.credentialsProtected ? (
            <Notice tone="danger" title="Mailbox passwords are stored in plaintext">
              No credential encryption key is configured, so every mailbox password is plaintext in
              the database — and any artifact exported from this install carries them the same way.
              Offload refuses to run in this state rather than shipping them to object storage. Set{' '}
              <span className="font-mono">Security__CredentialEncryptionKey</span> to a base64
              32-byte key and restart.
            </Notice>
          ) : null}

          {status.offloadConfigured && status.bucketVersioning !== 'enabled' ? (
            <Notice
              tone="warn"
              title={
                status.bucketVersioning === 'disabled'
                  ? 'The offload bucket does not keep old versions'
                  : 'The bucket’s versioning state could not be read'
              }
            >
              Every pass overwrites <span className="font-mono">config/latest.json</span>, so a pass
              that succeeds while producing a bad document destroys the good copy — and you find out
              during a recovery.{' '}
              {status.bucketVersioning === 'disabled'
                ? 'Turn on bucket versioning; it makes any bad overwrite recoverable.'
                : 'Some S3-compatible backends report versioning inconsistently, so confirm it by hand rather than assuming.'}
            </Notice>
          ) : null}

          {status.lastError ? (
            <Notice tone="warn" title="The last offload pass failed">
              <span className="font-mono text-xs">{status.lastError}</span>
            </Notice>
          ) : null}

          <Card pad>
            <CardHeader
              title="Offload status"
              description="Whether a copy is being made without anyone remembering to make one."
              actions={
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={busy || sessionEnded}
                  onClick={() => void loadData()}
                >
                  <Icon
                    name="refresh-cw"
                    size={14}
                    className={busy ? 'animate-spin' : undefined}
                  />
                  Refresh
                </Button>
              }
            />
            <div className="grid">
              <StatusRow
                label="Offload"
                badge={
                  status.offloadConfigured ? (
                    <Badge variant="success" dot>
                      Configured
                    </Badge>
                  ) : (
                    <Badge variant="warning" dot>
                      Not configured
                    </Badge>
                  )
                }
                hint={
                  status.offloadConfigured
                    ? undefined
                    : 'Nothing is shipped anywhere, so the only artifact is one you download here and store yourself.'
                }
              />
              <StatusRow
                label="Last successful offload"
                badge={
                  status.offloadConfigured && status.lastSuccessfulOffloadAtUtc === null ? (
                    <Badge variant="warning">Never</Badge>
                  ) : null
                }
                value={formatRelativeOrDate(status.lastSuccessfulOffloadAtUtc)}
                hint={
                  status.lastSuccessfulOffloadAtUtc ? (
                    <span className="font-mono">
                      {formatUtcDateTime(status.lastSuccessfulOffloadAtUtc)}
                    </span>
                  ) : undefined
                }
              />
              <StatusRow
                label="Bucket versioning"
                badge={
                  <Badge
                    variant={
                      status.bucketVersioning === 'enabled'
                        ? 'success'
                        : status.bucketVersioning === 'disabled'
                          ? 'warning'
                          : 'neutral'
                    }
                  >
                    {status.bucketVersioning === 'enabled'
                      ? 'Enabled'
                      : status.bucketVersioning === 'disabled'
                        ? 'Disabled'
                        : 'Unknown'}
                  </Badge>
                }
              />
              <StatusRow
                label="Mailbox credentials"
                badge={
                  status.credentialsProtected ? (
                    <Badge variant="success">Encrypted at rest</Badge>
                  ) : (
                    <Badge variant="danger">Plaintext</Badge>
                  )
                }
                hint={
                  status.credentialsProtected
                    ? 'The artifact carries enc:v1: ciphertext and a key fingerprint. Never store the key beside it.'
                    : undefined
                }
              />
              {/* The message itself is in the banner above rather than repeated
                  here — the row exists so "no failures" is a stated fact and not an
                  absence of one. */}
              <StatusRow
                label="Last offload error"
                badge={
                  status.lastError ? (
                    <Badge variant="danger">Recorded</Badge>
                  ) : (
                    <Badge variant="neutral">None</Badge>
                  )
                }
              />
            </div>
          </Card>

          <Card pad>
            <CardHeader
              title="Export configuration"
              description="One JSON document with everything a fresh install needs to become this one — clients, domains, mailbox sources, recipients, users, identities and grants."
            />

            {exportError ? (
              <Notice tone="danger" className="mb-3">
                {exportError}
              </Notice>
            ) : null}
            {exportNotice ? (
              <Notice tone="ok" className="mb-3">
                {exportNotice}
              </Notice>
            ) : null}

            {!status.credentialsProtected ? (
              <label className="mb-3 flex items-start gap-2 text-sm text-body">
                <input
                  type="checkbox"
                  className="mt-1"
                  checked={allowPlaintext}
                  onChange={(event) => setAllowPlaintext(event.target.checked)}
                />
                <span>
                  Export anyway, with plaintext mailbox passwords in the file.
                  <span className="block text-xs text-secondary">
                    The export refuses this by default. Requiring the box to be ticked is the point:
                    the file becomes a list of working mailbox credentials.
                  </span>
                </span>
              </label>
            ) : null}

            <div className="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                variant="secondary"
                disabled={exporting || exportBlocked || sessionEnded}
                onClick={() => void downloadExport()}
              >
                <Icon
                  name={exporting ? 'loader-circle' : 'download'}
                  size={16}
                  className={exporting ? 'animate-spin' : undefined}
                />
                {exporting ? 'Preparing' : 'Download export'}
              </Button>
              <span className="text-xs text-faint">
                Report data is excluded and the manifest says so, with row counts. Take a{' '}
                <span className="font-mono">pg_dump</span> before an upgrade — rollback across a
                migration needs one.
              </span>
            </div>
          </Card>

          {preview ? (
            <ConfigImportPanel
              preview={preview}
              onImported={(imported) => setSessionEnded(imported.signedInSessionInvalidated)}
            />
          ) : null}
        </div>
      ) : null}
    </>
  )
}

/** One label/value line in the status card. */
function StatusRow({
  label,
  badge,
  value,
  hint,
}: {
  label: string
  badge?: ReactNode
  value?: string
  hint?: ReactNode
}) {
  return (
    <div className="border-t border-border py-2.5 first:border-t-0 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
        <span className="text-sm text-secondary">{label}</span>
        <span className="flex items-center gap-2 text-sm text-body">
          {value != null ? <span>{value}</span> : null}
          {badge}
        </span>
      </div>
      {hint != null ? <p className="mt-1 text-xs text-faint">{hint}</p> : null}
    </div>
  )
}
