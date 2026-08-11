import type {
  ConfigExportManifest,
  ConfigImportEntityCount,
} from '@/lib/entities'

/**
 * Reads a configuration export the operator picked from disk, in the browser,
 * before anything is sent.
 *
 * The point is not convenience. A wrong or truncated file is the most likely thing
 * an operator hands to a recovery, and the cheapest place to catch it is here: no
 * request, no audit event for an import that never happened, and an error message
 * that names the file instead of a status code. Everything this module refuses is a
 * refusal the server would also make — it just makes it before the network.
 */

/**
 * The artifact's collections, clients first. A file that parses but carries no
 * clients is the failure mode the spec's validate-then-copy rule exists for, so the
 * counts are listed in the order that makes it obvious at a glance.
 */
const ARTIFACT_COLLECTIONS = [
  'clients',
  'domains',
  'reportSources',
  'notificationRecipients',
  'users',
  'userIdentities',
  'grants',
] as const

/**
 * Sentence-case labels for collection names. Used for both the browser-parsed
 * counts and the names the import endpoint sends back, so the two read the same;
 * an unmapped name falls through to its raw form rather than disappearing.
 */
const ENTITY_LABEL: Record<string, string> = {
  clients: 'Clients',
  domains: 'Domains',
  reportSources: 'Report sources',
  notificationRecipients: 'Notification recipients',
  users: 'Users',
  userIdentities: 'Linked identities',
  grants: 'Client grants',
}

export function entityLabel(entity: string): string {
  return ENTITY_LABEL[entity] ?? entity
}

export type ParsedConfigArtifact = {
  manifest: ConfigExportManifest
  /** Lowercased emails from the artifact's `users`, for the collision check against the signed-in account. */
  userEmails: string[]
  /**
   * The file's bytes as read, forwarded to the import endpoint verbatim rather than
   * re-serialized from a parsed object. A round-trip through `JSON.parse` would
   * silently drop every property this console does not model, and the console
   * deliberately models only part of a format that outlives it.
   */
  text: string
  entities: ConfigImportEntityCount[]
}

export type ConfigArtifactReadResult =
  | { ok: true; value: ParsedConfigArtifact }
  | { ok: false; error: string }

/** Reads and checks a picked file. Returns the reason rather than throwing, because every reason is shown to the operator. */
export async function readConfigArtifact(file: File): Promise<ConfigArtifactReadResult> {
  const text = await file.text()

  let document: unknown
  try {
    document = JSON.parse(text)
  } catch {
    return {
      ok: false,
      error: `${file.name} is not valid JSON, so it is not a configuration export.`,
    }
  }

  if (typeof document !== 'object' || document === null || Array.isArray(document)) {
    return { ok: false, error: `${file.name} is not a configuration export.` }
  }

  const root = document as Record<string, unknown>
  const manifest = root.manifest
  if (typeof manifest !== 'object' || manifest === null || Array.isArray(manifest)) {
    return {
      ok: false,
      error: `${file.name} has no manifest, so there is no way to tell which install or which encryption key it came from.`,
    }
  }

  const fields = manifest as Record<string, unknown>

  if (typeof fields.formatVersion !== 'number') {
    return { ok: false, error: `${file.name} does not declare a format version.` }
  }
  if (typeof fields.exportedAtUtc !== 'string') {
    return { ok: false, error: `${file.name} does not record when it was exported.` }
  }
  // Absent is not the same as null here. Null means "the exporting install had no
  // key"; absent means the file cannot answer the question at all, and treating
  // that as null would turn the wrong-key check into a coin toss.
  if (fields.encryptionKeyFingerprint !== null && typeof fields.encryptionKeyFingerprint !== 'string') {
    return {
      ok: false,
      error: `${file.name} does not record which encryption key protects its mailbox credentials, so a wrong-key import cannot be caught before it happens.`,
    }
  }
  if (typeof fields.credentialsProtected !== 'boolean') {
    return {
      ok: false,
      error: `${file.name} does not state whether its mailbox passwords are encrypted, so it cannot be treated as safe.`,
    }
  }

  const entities: ConfigImportEntityCount[] = []
  for (const collection of ARTIFACT_COLLECTIONS) {
    const rows = root[collection]
    if (!Array.isArray(rows)) {
      return {
        ok: false,
        // A partial import is worse than none: it looks like it worked, and the
        // missing half only surfaces when a mailbox never syncs or a user cannot
        // sign in. So a file missing any collection is refused whole.
        error: `${file.name} is missing its ${entityLabel(collection).toLowerCase()}, so it is incomplete.`,
      }
    }
    entities.push({ entity: collection, inArtifact: rows.length })
  }

  const userEmails = (root.users as unknown[])
    .map((row) =>
      typeof row === 'object' && row !== null ? (row as Record<string, unknown>).email : null,
    )
    .filter((email): email is string => typeof email === 'string')
    .map((email) => email.toLowerCase())

  return {
    ok: true,
    value: {
      manifest: {
        formatVersion: fields.formatVersion,
        exportedAtUtc: fields.exportedAtUtc,
        encryptionKeyFingerprint: fields.encryptionKeyFingerprint,
        credentialsProtected: fields.credentialsProtected,
      },
      userEmails,
      text,
      entities,
    },
  }
}
