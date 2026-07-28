import type { AuthUser } from '@/lib/auth-context'

/**
 * `partial` is a run that timed out after ingesting some of its backlog. It keeps
 * its checkpoint, so the next pass resumes rather than starting over — which makes
 * it a slow source, not a broken one.
 */
export type SyncRunStatus = 'success' | 'failed' | 'running' | 'partial' | 'unknown'

/** Row shape of the admin-only GET /api/v1/users endpoint. */
export type ManagedUser = {
  id: string
  email: string
  displayName: string
  role: AuthUser['role']
  isActive: boolean
  lastLoginAtUtc: string | null
  createdAtUtc: string
  updatedAtUtc: string
  /** Client grants; only meaningful for client_viewer users. */
  grantedClientIds: string[]
}

export type Client = {
  id: string
  name: string
  slug: string
  isActive: boolean
  retentionMonths: number
  /** Exempts the client from retention purging entirely. */
  legalHold: boolean
  alertsEnabled: boolean
  /** Null means "use the server default" for these two. */
  alertComplianceDropPercent: number | null
  alertMinMessages: number | null
  timezone: string
}

export type Domain = {
  id: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string | null
}

export type MailboxSource = {
  id: string
  name: string
  protocol: 'imap' | 'pop3'
  host: string
  port: number
  useTls: boolean
  username: string
  defaultClientId: string
  defaultClientName: string | null
  isActive: boolean
  /**
   * Whether the worker deletes report mail from this mailbox once it is older than the
   * retention window. Off unless someone turned it on: it destroys mail outside this
   * application, and the mailbox is what a report replay reads from.
   */
  deleteAfterRetention: boolean
  /**
   * Internal date of the oldest message still in the polled folder. The evidence for how
   * far back a replay could actually reach, and where the last deletion pass cut.
   */
  oldestMessageAtUtc: string | null
}

export type MailboxHealth = {
  mailboxSourceId: string
  name: string
  isActive: boolean
  lastSuccessSyncAtUtc: string | null
  lastProcessedUid: number | null
  lastProcessedUidValidity: number | null
  lastRunStatus: SyncRunStatus | null
  lastRunStartedAtUtc: string | null
  lastRunFinishedAtUtc: string | null
  lastRunError: string | null
  lastRunMessagesScanned: number | null
  lastRunAttachmentsProcessed: number | null
  lastRunReportsInserted: number | null
  lastRunReportsSkippedAsDuplicate: number | null
  lastRunParseFailures: number | null
}

export type MailboxSyncRun = {
  id: string
  mailboxSourceId: string
  trigger: string
  status: SyncRunStatus
  startedAtUtc: string
  finishedAtUtc: string | null
  messagesScanned: number
  attachmentsProcessed: number
  reportsInserted: number
  reportsSkippedAsDuplicate: number
  parseFailures: number
  error: string | null
  createdAtUtc: string
}

/**
 * One immutable audit-trail entry. `clientName` resolves to null when the event
 * has no client, or when that client has since been deleted — the trail keeps
 * the id either way, since `audit_event` deliberately has no foreign keys.
 */
export type AuditEvent = {
  id: string
  occurredAtUtc: string
  /** `user`, `system`, or `anonymous` (a failed sign-in has no actor yet). */
  actorType: string
  actorUserId: string | null
  actorEmail: string
  /** Dotted name, e.g. `auth.login.succeeded`. */
  eventType: string
  targetType: string | null
  targetId: string | null
  clientId: string | null
  clientName: string | null
  summary: string
  details: string | null
  ipAddress: string | null
  userAgent: string | null
}

export type AuditEventPage = {
  total: number
  items: AuditEvent[]
}

/**
 * Whether the offload bucket keeps superseded object versions. `unknown` is a real
 * answer rather than a placeholder: MinIO and several S3-compatible backends report
 * versioning inconsistently, so the check cannot always tell "off" from "this
 * backend won't say". The console treats both the same way, because both mean a
 * pass that overwrites `latest.json` with a bad document has destroyed the good one.
 */
export type BucketVersioningState = 'enabled' | 'disabled' | 'unknown'

/** Shape of GET /api/v1/admin/backup/status. */
export type BackupStatus = {
  /** False means nothing ships anywhere: the only artifact is the one an operator downloads by hand. */
  offloadConfigured: boolean
  /**
   * False means no credential encryption key is configured, so mailbox passwords are
   * plaintext in the database and would be plaintext in every artifact. That is the
   * dev default, which is exactly why the console reports it as a defect and not as
   * a setting — an operator who never chose it has to be told.
   */
  credentialsProtected: boolean
  bucketVersioning: BucketVersioningState
  /** Null means no pass has ever completed, which is a different problem from a failing one. */
  lastSuccessfulOffloadAtUtc: string | null
  /** The most recent failure, cleared by the next success. Null when the last pass was fine. */
  lastError: string | null
}

/** `restore` is the disaster-recovery path and only accepts an empty install; `merge` is the clone/seed path. */
export type ConfigImportMode = 'restore' | 'merge'

/**
 * Where the artifact comes from. `upload` sends the file's bytes as the request
 * body; `bucket` sends no body and the server reads `config/latest.json` itself —
 * which is the one that matters in a real recovery, where the operator should not
 * have to find and download the artifact by hand first.
 */
export type ConfigImportSourceKind = 'upload' | 'bucket'

/** Rows one collection of the artifact carries. */
export type ConfigImportEntityCount = {
  /** Collection name as the artifact spells it: `clients`, `domains`, `users`, … */
  entity: string
  inArtifact: number
}

/**
 * Shape of GET /api/v1/admin/config/import/preview — the facts about *this install*
 * plus whatever artifact object storage can offer. It takes no artifact of its own,
 * because a GET cannot carry one: an uploaded file is parsed and checked in the
 * browser instead, which is also what keeps an invalid file from ever being sent.
 */
export type ConfigImportPreview = {
  /**
   * No clients and no domains. This is the authenticated answer to "is this a clean
   * install?" — `GET /api/v1/auth/setup` cannot answer it, because by the time the
   * console loads the bootstrap administrator exists and `requiresBootstrap` is
   * already false.
   */
  isEmptyInstall: boolean
  /**
   * The artifact format version this build reads. The console refuses a file that
   * declares anything else rather than uploading it and guessing at the difference.
   */
  supportedFormatVersion: number
  /**
   * Fingerprint of the running credential key, or null when none is configured.
   * Comparing it to an artifact's manifest answers "do I hold the right key for this
   * file?" *before* the import, instead of at the next failed mailbox sync.
   */
  keyFingerprint: string | null
  /** Whether object storage is configured at all — distinguishes "nothing to pull" from "misconfigured". */
  bucketConfigured: boolean
  /** The artifact found in object storage, or null when there is none to read. */
  bucket: ConfigImportBucketArtifact | null
}

/**
 * The artifact the server found in the bucket. Note there is no
 * `credentialsProtected` here: offload refuses to start without a credential
 * encryption key, so an artifact that reached the bucket cannot be one of the
 * plaintext ones. Only an uploaded file can be.
 */
export type ConfigImportBucketArtifact = {
  /** Object key it was read from, so the operator can tell which artifact this is. */
  key: string
  formatVersion: number
  exportedAtUtc: string
  /** False means the running key can never decrypt this artifact's mailbox credentials. */
  keyFingerprintMatches: boolean
  /**
   * True when the signed-in account's email appears in the artifact's users. On an
   * email collision the imported user wins, so importing replaces the operator's own
   * password and ends this session — they have to be told before, not after.
   */
  carriesSignedInUser: boolean
  entities: ConfigImportEntityCount[]
}

/** Per-collection outcome of an import. */
export type ConfigImportEntityResult = {
  entity: string
  created: number
  updated: number
}

/** Shape of POST /api/v1/admin/config/import?mode=…&source=… */
export type ConfigImportResult = {
  mode: ConfigImportMode
  /** Rows inserted, across every collection. */
  created: number
  /** Rows updated in place, across every collection. Import never deletes. */
  updated: number
  entities: ConfigImportEntityResult[]
  /**
   * Emails whose password hash the artifact replaced. Their sessions are gone, and
   * they now sign in with the credentials they had before the disaster — which is
   * what makes a restore faithful, and what the console has to state plainly.
   */
  usersWithChangedPasswords: string[]
  /**
   * True when the signed-in account was one of them. Every later request from this
   * tab will 401, and a 401 force-logs-out the whole console — so this response is
   * the last thing the operator will see, and it has to carry the credentials they
   * need next.
   */
  signedInSessionInvalidated: boolean
  /**
   * Id-versus-natural-key collisions the import reported instead of resolving
   * silently (merge mode). Human-readable, one line each.
   */
  conflicts: string[]
}

/**
 * The part of an artifact's manifest the console reads before it will upload the
 * file. Deliberately a subset: the artifact is a published format that outlives any
 * one console build, so modelling all of it here would make an older console reject
 * a newer, still-compatible file.
 */
export type ConfigExportManifest = {
  formatVersion: number
  exportedAtUtc: string
  /** Null when the exporting install had no credential encryption key. */
  encryptionKeyFingerprint: string | null
  /** False means this file contains plaintext mailbox passwords. */
  credentialsProtected: boolean
}
