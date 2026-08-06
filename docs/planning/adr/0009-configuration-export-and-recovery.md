# ADR 0009: Configuration Export and Recovery

- Status: accepted
- Date: 2026-07-27
- Extends ADR 0005 (keeps its per-client retention window, and applies the same
  window to the mailbox copy of the same data)

## Context

Backup is a `pg_dump | gzip`, documented in four places and automated nowhere. It
is dominated by the cheap part of the data — ~5.3M report records against a few
hundred rows of configuration. Reports are a statistical summary that arrived over
IMAP and can arrive again; configuration is what a human typed, and re-typing it
is the part of a recovery that hurts. A SQL dump is also the wrong artifact for
that part: opaque, tied to a Postgres version and schema revision, restorable only
into an empty database, and `psql` exits 0 while restoring almost nothing without
`-v ON_ERROR_STOP=1`.

"Reports are re-ingestible" was verified, not assumed: ingestion never sets
`\Seen` or `\Deleted` and never expunges, and `dmarc_report` has a unique index on
`(DomainId, ReportId, RangeBeginUtc, RangeEndUtc)`, so a replay inserts only what
is missing. Detail and code references:
[`config-export-and-recovery.md`](../config-export-and-recovery.md).

## Decision

- A **JSON configuration artifact is the primary backup**, restored through the
  app. `formatVersion` gates compatibility; the manifest carries the applied
  migration, excluded row counts, legal-hold clients, and an encryption-key
  fingerprint.
- **Four tiers by cost and cadence**: 0 configuration (KB, every 30 min,
  overwritten); 1 append-only history — audit, alert, digest, sync-run, ingest
  ledger (MB, every 30 min); 2 report mail archive (GB, optional, bucket only);
  3 `pg_dump` (GB, before upgrades). The dump stays, demoted to the pre-upgrade
  artifact a mid-migration rollback needs.
- **Report data is excluded: the mailbox is the archive**, so nothing writes report
  data to the database twice. Raw XML in the database was considered and dropped.
- **The worker deletes report mail past the retention window**, so the system has
  one retention window instead of a database window plus an unbounded mailbox.
  Opt-in per source, cutoff is the widest window that source serves, suspended
  entirely by legal hold, retention plus a grace margin, previewable, audited, and
  never before a confirmed archive write when tier 2 is on.
- **Tiers 0-1 ship continuously to S3-compatible object storage.** `latest.json`
  is copied from a temp key only after the document validates, a dated daily copy
  sits beside it, and the bucket's versioning state is checked and surfaced rather
  than enforced — several S3-compatible backends report it inconsistently, and a
  backup that refuses to run is worse than an unversioned one.
- **Import is a first-run console action, not an `APP_MODE`**: clean install,
  bootstrap an administrator, import as that account's first act. `restore`
  requires an install nothing has been added to yet — what bootstrap itself
  created, the admin account and the `default` client, does not count against
  it; `merge` upserts by natural key.
- **Import never deletes.** Rows absent from the artifact are left alone; on an
  email collision the imported user wins, so pre-disaster credentials work again,
  and the bootstrap account survives as break-glass.

## Consequences

### Positive

- Recovery is an app operation on a readable file, and a wrong key is caught by
  fingerprint before the import rather than at the next mailbox sync.
- The retention promise becomes true of the system, not just the database: a purge
  is no longer undone by the next sync.
- Continuous offload turns a documented manual step into a scheduled one, which is
  the difference between a backup that exists and one that was intended.

### Negative

- **The bucket holds credential material** — `enc:v1:` ciphertext and PBKDF2
  hashes. It must be private, and `DMARC_ENCRYPTION_KEY` must never be shipped to
  it. Offload refuses to start with no key configured, since the artifact would
  then carry plaintext mailbox passwords.
- With mail deletion on and the archive off, report data inside the window has the
  database as its only complete copy, so a bug in the deletion pass is
  unrecoverable — hence opt-in, grace, preview, audit.
- The archive relocates personal data rather than reducing it: running it means a
  longer erasure horizon, a lifecycle rule on the prefix, and a third location in
  the data-protection inventory.
- Overwriting `latest.json` is the one destructive write here; the mitigations
  shrink its blast radius rather than removing it.
- Because import is non-destructive, `restore` into a populated install would
  produce a union rather than a copy — so it refuses instead.

### Follow-up

- A bucket replay path. Until it exists the archive is evidence, not recovery.
- A restore drill: export, import into a throwaway instance, compare manifest
  counts, then sync a mailbox source — the only step that proves the key
  round-tripped.
- Key rotation, once a key has an identity (key id in the `enc:v1:` prefix). Its
  own ADR.
