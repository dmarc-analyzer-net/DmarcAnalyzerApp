import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'

import { Notice } from '@/components/Notice'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { entityLabel, readConfigArtifact } from '@/lib/config-artifact'
import type { ParsedConfigArtifact } from '@/lib/config-artifact'
import type {
  ConfigImportEntityCount,
  ConfigImportMode,
  ConfigImportPreview,
  ConfigImportResult,
  ConfigImportSourceKind,
} from '@/lib/entities'
import { formatUtcDateTime } from '@/lib/format'

/**
 * The import flow: pick an artifact, see what it would do, run it.
 *
 * Shared by the first-run step and the ongoing backup page, because the mechanics
 * are identical and only the framing around them differs. The panel owns its own
 * result and never notifies the host, which is not laziness: after an import that
 * changed the operator's own password hash, every further request from this tab
 * returns 401, and a 401 on a non-auth path force-logs-out the whole console. A
 * host that refreshed itself on success would replace the one screen carrying the
 * credentials the operator now needs with a login form.
 */

/** The artifact under consideration, from either source, reduced to what the decision needs. */
type ImportCandidate = {
  /** What to call it on screen — a file name, or the object key it came from. */
  origin: string
  formatVersion: number
  exportedAtUtc: string
  keyFingerprintMatches: boolean
  /** Whether the artifact names a key at all. "No key" and "a different key" are different failures. */
  hasKeyFingerprint: boolean
  credentialsProtected: boolean
  carriesSignedInUser: boolean
  entities: ConfigImportEntityCount[]
}

/** Styled native file input. There is no dropzone or file-input primitive, and this needs no more than tokens. */
const FILE_INPUT_CLASS =
  'block w-full cursor-pointer rounded-md border border-border bg-surface-card px-3 py-[7px] font-body text-base text-body transition-colors duration-[120ms] ease-out hover:bg-gray-100 focus-visible:border-brand focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none file:mr-3 file:cursor-pointer file:rounded-xs file:border-0 file:bg-[var(--surface-sunken)] file:px-2.5 file:py-1 file:font-body file:text-sm file:font-semibold file:text-body'

type ConfigImportPanelProps = {
  preview: ConfigImportPreview
  /**
   * Fired once, after an import commits. Deliberately not a "reload yourself"
   * signal: the host's job is to *stop* calling the API when
   * `signedInSessionInvalidated` is true, because at that point every request
   * returns 401 and a 401 replaces this screen with a login form.
   */
  onImported?: (result: ConfigImportResult) => void
}

export function ConfigImportPanel({ preview, onImported }: ConfigImportPanelProps) {
  const { user, logout } = useAuth()
  const navigate = useNavigate()

  const [source, setSource] = useState<ConfigImportSourceKind>(
    preview.bucket ? 'bucket' : 'upload',
  )
  // Restore is the faithful path and the default wherever it is allowed; merge is
  // the only option once the install holds anything, because a non-destructive
  // import into a populated install produces a union rather than a copy.
  const [mode, setMode] = useState<ConfigImportMode>(preview.isEmptyInstall ? 'restore' : 'merge')

  const [parsed, setParsed] = useState<ParsedConfigArtifact | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  const [fileError, setFileError] = useState<string | null>(null)

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ConfigImportResult | null>(null)

  /**
   * A key mismatch is not fatal: the artifact's other configuration is still good,
   * and the server accepts it via `allowKeyFingerprintMismatch` — the cost is
   * re-entering every mailbox password by hand afterward. Reset per artifact so an
   * acknowledgement for one file does not silently carry over to the next.
   */
  const [acknowledgeKeyMismatch, setAcknowledgeKeyMismatch] = useState(false)

  const candidate = useMemo<ImportCandidate | null>(() => {
    if (source === 'bucket') {
      const bucket = preview.bucket
      if (!bucket) return null
      return {
        origin: bucket.key,
        formatVersion: bucket.formatVersion,
        exportedAtUtc: bucket.exportedAtUtc,
        keyFingerprintMatches: bucket.keyFingerprintMatches,
        // Offload refuses to start without a credential encryption key, so an
        // artifact that reached the bucket cannot be one of the plaintext ones —
        // which also means it always names the key that protects it.
        hasKeyFingerprint: true,
        credentialsProtected: true,
        carriesSignedInUser: bucket.carriesSignedInUser,
        entities: bucket.entities,
      }
    }

    if (!parsed) return null
    return {
      origin: fileName ?? 'the selected file',
      formatVersion: parsed.manifest.formatVersion,
      exportedAtUtc: parsed.manifest.exportedAtUtc,
      // Both null is a match: two installs with no encryption key can exchange
      // artifacts, they are just exchanging plaintext, which the warning below says.
      keyFingerprintMatches: parsed.manifest.encryptionKeyFingerprint === preview.keyFingerprint,
      hasKeyFingerprint: parsed.manifest.encryptionKeyFingerprint !== null,
      credentialsProtected: parsed.manifest.credentialsProtected,
      carriesSignedInUser: user?.email
        ? parsed.userEmails.includes(user.email.toLowerCase())
        : false,
      entities: parsed.entities,
    }
  }, [source, preview, parsed, fileName, user])

  /** Reasons the import cannot run at all — the server would refuse these outright too. */
  const blockers: string[] = []
  /**
   * A key mismatch is reported separately from `blockers`: unlike them, it is not
   * fatal. The server accepts it given `allowKeyFingerprintMismatch=true`, and the
   * cost is re-entering every mailbox password afterward — not a refusal. Three
   * different explanations, because the fix differs for each and a single "key
   * mismatch" line would send an operator looking for the wrong thing.
   */
  let keyMismatchReason: string | null = null
  if (candidate) {
    if (candidate.formatVersion !== preview.supportedFormatVersion) {
      blockers.push(
        `This artifact declares format version ${candidate.formatVersion}, and this build reads version ${preview.supportedFormatVersion}. Importing it would mean guessing at the difference.`,
      )
    }
    if (!candidate.keyFingerprintMatches) {
      if (preview.keyFingerprint === null) {
        keyMismatchReason =
          'This artifact was written by an install that had a credential encryption key, and this one has none, so its mailbox passwords cannot be decrypted.'
      } else if (!candidate.hasKeyFingerprint) {
        keyMismatchReason =
          'This artifact was exported without a credential encryption key, so it carries plaintext mailbox passwords, while this install expects ciphertext under the key it holds.'
      } else {
        keyMismatchReason =
          'This artifact was encrypted with a different key than this install holds, so its report sources cannot be decrypted with the key here.'
      }
    }
    if (mode === 'restore' && !preview.isEmptyInstall) {
      blockers.push(
        'Restore mode only accepts an empty install. This one already holds clients or domains, and an import that never deletes cannot reproduce a state something was deleted from — it would produce a union, not a copy. Use merge if a union is what you want.',
      )
    }
  }

  const needsKeyOverride = keyMismatchReason !== null && !acknowledgeKeyMismatch

  const pickFile = async (file: File | null) => {
    setError(null)
    setFileError(null)
    setParsed(null)
    setFileName(file?.name ?? null)
    setAcknowledgeKeyMismatch(false)
    if (!file) return

    const read = await readConfigArtifact(file)
    if (read.ok) setParsed(read.value)
    else setFileError(read.error)
  }

  const runImport = async () => {
    if (!candidate || blockers.length > 0 || needsKeyOverride || busy) return
    if (source === 'upload' && !parsed) return

    // The one genuinely surprising outcome, so it is confirmed rather than
    // explained: the operator is about to change their own password.
    if (
      candidate.carriesSignedInUser &&
      !window.confirm(
        `This artifact contains an account for ${user?.email}. Importing it replaces that password with the one from the artifact and ends this session. Continue?`,
      )
    ) {
      return
    }

    setBusy(true)
    setError(null)
    try {
      const query = new URLSearchParams({ mode, source })
      if (keyMismatchReason !== null) {
        query.set('allowKeyFingerprintMismatch', 'true')
      }
      const payload = await fetchJson<ConfigImportResult>(
        `/api/v1/admin/config/import?${query}`,
        source === 'upload'
          ? {
              method: 'POST',
              // fetchJson sets no Content-Type of its own, so a JSON body has to
              // declare itself or the endpoint sees an unknown media type.
              headers: { 'Content-Type': 'application/json' },
              body: parsed?.text,
            }
          : { method: 'POST' },
      )
      setResult(payload)
      onImported?.(payload)
    } catch (importError) {
      setError(
        importError instanceof Error ? importError.message : 'The import did not complete.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card pad>
      <CardHeader
        title="Import configuration"
        description="Restores clients, domains, report sources, recipients, users and grants from an export. Import never deletes: anything here that the artifact does not mention is left exactly as it is."
      />

      {result ? (
        <ImportResultView
          result={result}
          onSignOut={() => void logout()}
          onContinue={() => navigate('/dashboard')}
        />
      ) : (
        <div className="grid gap-3.5">
          {error ? <Notice tone="danger">{error}</Notice> : null}

          <div className="flex flex-wrap items-end gap-3">
            {/* Offered only when there is something to pull. A picker whose first
                option cannot be chosen teaches nothing; the reason it is missing is
                stated below instead. */}
            {preview.bucket ? (
              <label className="flex min-w-[220px] flex-col gap-1.5">
                <span className="text-xs font-medium text-secondary">Artifact</span>
                <Select
                  value={source}
                  onChange={(event) => {
                    setSource(event.target.value as ConfigImportSourceKind)
                    setError(null)
                    setAcknowledgeKeyMismatch(false)
                  }}
                >
                  <option value="bucket">From object storage</option>
                  <option value="upload">From a file</option>
                </Select>
              </label>
            ) : null}
            <label className="flex min-w-[220px] flex-col gap-1.5">
              <span className="text-xs font-medium text-secondary">Mode</span>
              <Select
                value={mode}
                onChange={(event) => setMode(event.target.value as ConfigImportMode)}
              >
                <option value="restore" disabled={!preview.isEmptyInstall}>
                  Restore — recover this install
                </option>
                <option value="merge">Merge — add to what is here</option>
              </Select>
            </label>
          </div>

          <p className="text-xs text-faint">
            {mode === 'restore'
              ? 'Writes the artifact’s ids as they are, so every reference between clients, domains and grants stays intact. Refuses if any row already exists.'
              : 'Upserts by natural key — client slug, domain name, user email — and keeps the existing row’s id where one is found. Id-versus-natural-key conflicts are reported, not resolved silently.'}
          </p>

          {source === 'upload' ? (
            <label className="grid gap-1.5">
              <span className="text-xs font-medium text-secondary">Configuration export (.json)</span>
              <input
                type="file"
                accept="application/json,.json"
                className={FILE_INPUT_CLASS}
                onChange={(event) => void pickFile(event.target.files?.[0] ?? null)}
              />
              <span className="text-xs text-faint">
                Read in this browser first, so a wrong or truncated file is caught before anything is
                sent. Treat the file as you would a database dump — it carries encrypted mailbox
                credentials and password hashes.
              </span>
            </label>
          ) : null}

          {source === 'upload' && fileError ? (
            <Notice tone="danger">{fileError}</Notice>
          ) : null}

          {/* A configured bucket with no readable artifact is worth flagging on its
              own: it means the offload is not producing what a recovery will look
              for, and the place to find that out is not the recovery. */}
          {!preview.bucket && preview.bucketConfigured ? (
            <Notice tone="warn">
              Object storage is configured, but no readable artifact was found there, so the only
              copy available is one you hold. Check the bucket and prefix before you need them.
            </Notice>
          ) : null}
          {!preview.bucket && !preview.bucketConfigured ? (
            <p className="text-xs text-faint">
              No object storage is configured, so there is nothing to pull — a file is the only
              source.
            </p>
          ) : null}

          {candidate ? (
            <ArtifactSummary candidate={candidate} keyFingerprint={preview.keyFingerprint} />
          ) : null}

          {blockers.map((blocker) => (
            <Notice key={blocker} tone="danger">
              {blocker}
            </Notice>
          ))}

          {/* Not a blocker: the config underneath — clients, domains, users, grants —
              is still good, and the server accepts this given the checkbox below. The
              cost is real but bounded: every report source in the artifact needs its
              password re-entered by hand before it will sync again. */}
          {candidate && blockers.length === 0 && keyMismatchReason ? (
            <Notice tone="warn" title="Mailbox credentials will not carry over">
              <p>{keyMismatchReason}</p>
              <label className="mt-2 flex items-start gap-2 text-sm text-body">
                <input
                  type="checkbox"
                  className="mt-1"
                  checked={acknowledgeKeyMismatch}
                  onChange={(event) => setAcknowledgeKeyMismatch(event.target.checked)}
                />
                <span>
                  Import anyway, and re-enter every mailbox password by hand afterward.
                  <span className="block text-xs text-secondary">
                    Everything else in the artifact — clients, domains, recipients, users and grants —
                    imports normally. Only the report sources' passwords are affected.
                  </span>
                </span>
              </label>
            </Notice>
          ) : null}

          {/* Reachable only when this install has no key either: an artifact written
              without one carries no fingerprint, and a missing fingerprint against a
              configured key is already covered by the key-mismatch notice above. So
              the passwords are plaintext at both ends, and saying anything softer
              would be untrue. */}
          {candidate && blockers.length === 0 && keyMismatchReason === null && !candidate.credentialsProtected ? (
            <Notice tone="warn" title="This artifact contains plaintext mailbox passwords">
              It was exported by an install with no credential encryption key, and this one has none
              either, so the passwords stay readable here too. Delete the file once you are done with
              it, and set a key before this install ships anything to object storage.
            </Notice>
          ) : null}

          {candidate && blockers.length === 0 && candidate.carriesSignedInUser ? (
            <Notice tone="warn" title="This import will change your own password">
              The artifact contains an account for{' '}
              <span className="font-mono">{user?.email}</span>. On an email collision the imported
              user wins, so your password becomes the one from the artifact and this session ends.
              The result below will say what to sign in with — read it before navigating away.
            </Notice>
          ) : null}

          <div className="flex items-center gap-3">
            <Button
              type="button"
              disabled={!candidate || blockers.length > 0 || needsKeyOverride || busy}
              onClick={() => void runImport()}
            >
              <Icon name={busy ? 'loader-circle' : 'upload'} size={16} className={busy ? 'animate-spin' : undefined} />
              {busy ? 'Importing' : mode === 'restore' ? 'Restore configuration' : 'Merge configuration'}
            </Button>
            {candidate ? (
              <span className="text-xs text-faint">
                From <span className="font-mono">{candidate.origin}</span>
              </span>
            ) : null}
          </div>
        </div>
      )}
    </Card>
  )
}

/** What the artifact is, and whether this install can take it. */
function ArtifactSummary({
  candidate,
  keyFingerprint,
}: {
  candidate: ImportCandidate
  keyFingerprint: string | null
}) {
  return (
    <div className="rounded-md border border-border bg-surface-sunken p-3.5">
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt className="font-medium text-secondary">Exported</dt>
        <dd className="font-mono text-body">{formatUtcDateTime(candidate.exportedAtUtc)}</dd>
        <dt className="font-medium text-secondary">Format version</dt>
        <dd className="font-mono text-body">{candidate.formatVersion}</dd>
        <dt className="font-medium text-secondary">Encryption key</dt>
        <dd className="flex flex-wrap items-center gap-2">
          <Badge variant={candidate.keyFingerprintMatches ? 'success' : 'danger'}>
            {candidate.keyFingerprintMatches ? 'Matches this install' : 'Does not match'}
          </Badge>
          {/* The install's own fingerprint, not the artifact's: the fingerprint
              identifies a key without enabling decryption, so it is the one value
              that makes "do I hold the right key for this file?" checkable. */}
          <span className="text-faint">
            {keyFingerprint ? (
              <>
                this install holds <span className="font-mono">{keyFingerprint}</span>
              </>
            ) : (
              'this install holds no key'
            )}
          </span>
        </dd>
      </dl>

      <div className="mt-3 overflow-x-auto rounded-xs border border-border bg-surface-card">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Contents</TableHead>
              <TableHead className="text-right">Rows</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {candidate.entities.map((entity, index) => (
              <TableRow key={entity.entity} last={index === candidate.entities.length - 1}>
                <TableCell className="text-sm text-body">{entityLabel(entity.entity)}</TableCell>
                <TableCell mono align="right">
                  {entity.inArtifact.toLocaleString('en-US')}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  )
}

/**
 * The import outcome, credentials first.
 *
 * Ordering is the whole design of this view: if the import replaced the operator's
 * password hash, their session is already gone and the next API call from this tab
 * will bounce them to the login screen. So what to sign in with comes before the
 * counts, and nothing here triggers a request.
 */
function ImportResultView({
  result,
  onSignOut,
  onContinue,
}: {
  result: ConfigImportResult
  onSignOut: () => void
  onContinue: () => void
}) {
  return (
    <div className="grid gap-3.5">
      {result.signedInSessionInvalidated ? (
        <Notice tone="warn" title="Sign in again with your restored credentials">
          This artifact contained your account, so your password is now the one it carried — the
          password this account had before the disaster, which is what your password manager already
          holds. This session ended the moment the import committed.
        </Notice>
      ) : (
        <Notice tone="ok" title="Import complete">
          Your session was not affected: your account was not in the artifact, so it survives as a
          break-glass administrator alongside the restored users.
        </Notice>
      )}

      {result.mailboxCredentialsWillNotDecrypt ? (
        <Notice tone="warn" title="Re-enter every mailbox password by hand">
          This artifact's mailbox credentials were imported under a different encryption key, so they
          cannot be decrypted here. Every report source above needs its password typed in again
          before it will sync.
        </Notice>
      ) : null}

      {result.warnings.length > 0 ? (
        <Notice tone="warn" title="Warnings">
          <ul className="mt-1 grid gap-1">
            {result.warnings.map((warning) => (
              <li key={warning} className="text-xs">
                {warning}
              </li>
            ))}
          </ul>
        </Notice>
      ) : null}

      {result.usersWithChangedPasswords.length > 0 ? (
        <div className="rounded-md border border-border bg-surface-sunken p-3.5">
          <p className="text-sm font-semibold text-body">
            Accounts now using the artifact’s password
          </p>
          <p className="mt-0.5 text-xs text-secondary">
            Their sessions were invalidated. Every other account kept its password and its session.
          </p>
          <ul className="mt-2 grid gap-1">
            {result.usersWithChangedPasswords.map((email) => (
              <li key={email} className="font-mono text-xs text-body">
                {email}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="neutral">{result.mode === 'restore' ? 'Restore' : 'Merge'}</Badge>
        <Badge variant="success">{result.created.toLocaleString('en-US')} created</Badge>
        <Badge variant={result.updated > 0 ? 'warning' : 'neutral'}>
          {result.updated.toLocaleString('en-US')} updated
        </Badge>
        <Badge variant="neutral">0 deleted</Badge>
      </div>

      <div className="overflow-x-auto rounded-md border border-border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Contents</TableHead>
              <TableHead className="text-right">Created</TableHead>
              <TableHead className="text-right">Updated</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {result.entities.map((entity, index) => (
              <TableRow key={entity.entity} last={index === result.entities.length - 1}>
                <TableCell className="text-sm text-body">{entityLabel(entity.entity)}</TableCell>
                <TableCell mono align="right">
                  {entity.created.toLocaleString('en-US')}
                </TableCell>
                <TableCell mono align="right">
                  {entity.updated.toLocaleString('en-US')}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      {result.conflicts.length > 0 ? (
        <Notice tone="warn" title="Conflicts reported, not resolved">
          <ul className="mt-1 grid gap-1">
            {result.conflicts.map((conflict) => (
              <li key={conflict} className="font-mono text-xs">
                {conflict}
              </li>
            ))}
          </ul>
        </Notice>
      ) : null}

      <div className="flex items-center gap-3">
        {result.signedInSessionInvalidated ? (
          <Button type="button" onClick={onSignOut}>
            <Icon name="log-out" size={16} />
            Go to sign in
          </Button>
        ) : (
          <Button type="button" onClick={onContinue}>
            <Icon name="arrow-right" size={16} />
            Continue to the dashboard
          </Button>
        )}
        <span className="text-xs text-faint">
          Report sources rescan from the beginning — a restored checkpoint would skip mail — so the
          first sync after a restore takes longer than usual.
        </span>
      </div>
    </div>
  )
}
