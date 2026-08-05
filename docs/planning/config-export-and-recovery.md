# Spec: configuration export, offload, and recovery

- Status: **accepted and implemented**, except the bucket replay path (phase 8) — promoted to
  [ADR 0009](adr/0009-configuration-export-and-recovery.md).
- Date: 2026-07-27
- Supersedes nothing. Retained as the detailed design record behind ADR 0009.

> **Where this differs from what shipped.** Two things the implementation found that this
> document understates. Import cannot always preserve Ids: when a natural key matches an
> existing row a merge keeps the *existing* Id, so every child in the artifact has to be
> repointed at it — without that a merge produces orphaned grants and reads as a success.
> And the import preview is two endpoints, not one: a `GET` reporting facts about the install
> (a GET cannot carry an artifact) and a `POST .../dry-run` that vets a supplied artifact.

## Why

Backup today is a `pg_dump | gzip` documented in four places
([website](https://dmarc-analyzer-net.github.io/docs/upgrading-and-backup/),
`docs/ops/migrating-a-running-instance.md:61`, the Kubernetes page, `AGENTS.md:265`)
and automated nowhere. It has two problems.

**It is dominated by the cheap part of the data.** A production database is
~5.3M `dmarc_report_record` rows against a few hundred rows of configuration.
Report data is a statistical summary that arrived over IMAP and can arrive again.
Configuration — retention windows, legal-hold flags, per-client alert thresholds,
mailbox hosts and credentials, users and grants — is what a human typed, and
re-typing it is the part of a recovery that actually hurts.

**A SQL dump is the wrong artifact for that part.** It is opaque, tied to a
Postgres version and a schema revision, restores only into an empty database, and
`psql` reports success while restoring almost nothing unless you remember
`-v ON_ERROR_STOP=1`. The website's restore section is ~40 lines of caveats that
exist entirely because of the format.

So: a JSON artifact, shipped continuously to object storage, as the primary
backup. Portable across Postgres versions, diffable, restorable through the app,
and small enough to ship every 30 minutes.

The full dump does not go away — it moves to where it earns its cost: before an
upgrade, because rollback across a migration needs it.

## The tier model

| Tier | Contents | Size | Cadence | Recovers |
|---|---|---|---|---|
| 0 | Configuration: clients, domains, mailbox sources, recipients, users, identities, grants | KB | every 30 min, overwrite | A usable console with correct routing and credentials |
| 1 | History: audit, alert, digest, sync-run, ingest-ledger rows | MB | every 30 min, append-only | The compliance and operational record |
| 2 | Report mail archive — **optional**, object storage only | GB | at ingest, append-only | Full report history independent of the mailbox |
| 3 | `pg_dump` | GB | before upgrades | Everything, including a mid-migration rollback |

**No report data is ever written to the database twice.** The mailbox is the
primary archive; tier 2 is an optional copy of the same mail in the same bucket
the config export already uses, which is what makes bounded mailbox retention
safe to turn on.

## The three recovery gaps, and the answers

The case for skipping report data rests on "reports are re-ingestible." That is
true — verified, not assumed:

- Ingestion is **non-destructive today**. There is no `Expunge`, no `\Deleted`, no
  `\Seen` flagging anywhere in the ingestion path, so the mailbox is left intact
  and remains a source of truth after a restore.
- Re-ingesting is **idempotent**. `dmarc_report` carries a unique index on
  `(DomainId, ReportId, RangeBeginUtc, RangeEndUtc)`
  (`DmarcAnalyzerDbContext.cs:203`); `TryInsertDmarcReportAsync` returns null on a
  duplicate and the transaction rolls back (`MailboxSyncService.cs:177-194`).
  Replaying a mailbox inserts only what is missing.
  (Note: ADR 0005 describes the dedup key as `(client, domain, report-id, begin,
  end)`. That is the *ingest ledger's* index, `DmarcAnalyzerDbContext.cs:154-160`.
  Report dedup itself is domain-scoped and does not include the client.)

### Gap 1 — the mailbox is the archive, and it should be bounded by retention

Mailboxes keep mail indefinitely in practice, and where a provider policy exists
it can be set to whatever window the data deserves. So the recoverable horizon is
not the problem it first appears: replaying the mailbox reconstructs the full
report history.

That makes storing raw report XML **in the database** unnecessary — it would be a
second copy of something the mailbox already keeps, inside the store whose size we
are trying to reduce. Dropped. Shipping that mail to the *bucket* is a different
proposition and is worth having as an option; see
[the optional report archive](#the-optional-report-archive) below.

The real problem is the opposite one. The mailbox currently keeps report mail
**forever, unbounded, outliving the retention window the app enforces on itself.**
That is a compliance hole, not a safety net:

- `data-protection.md` states retention as "27 months by default, a daily pass
  deletes what has aged past the window" and describes purging as "deletion, not
  archival." Both are true of the database and false of the system: the same
  personal data (sending IP addresses, authentication outcomes) sits in the
  mailbox indefinitely.
- The personal-data inventory in that document does not list the mailbox as a
  location at all, so an Art. 30 record built from it is incomplete.
- An erasure request cannot be satisfied by lowering a client's retention window
  and purging, because the reports come back on the next sync — and if the mailbox
  is never pruned, they come back forever.

**The answer: the worker deletes report mail older than the retention window.**
This is a new feature, and it is the improvement worth making here. It gives the
system one retention window instead of two, and it makes "the mailbox is the
archive" a bounded, documented claim rather than an accident.

Design rules, all of which exist to make a deletion pass safe:

0. **Archive first, if the archive is enabled.** When tier 2 is on, a message is
   deleted only after its object exists in the bucket. Deletion and archival are
   configured independently but ordered strictly: no delete without a confirmed
   write.
1. **Opt-in per source.** `MailboxSource.DeleteAfterRetention`, default false.
   Deleting a customer's mail is not a default behaviour, and some operators poll
   a shared mailbox that other tooling also reads.
2. **Cutoff is the widest window the source serves.** One mailbox can receive
   reports for many clients (ADR 0005), so the cutoff is
   `max(RetentionMonths)` across every client whose domains that source has
   ingested for — never a single client's window.
3. **Legal hold suspends deletion entirely for that source.** If any client the
   source serves has `LegalHold = true`, the pass does nothing and says why. The
   database exemption is worthless if the upstream copy is being deleted.
4. **A grace margin on top of the window.** Delete at retention + N days
   (`Worker__MailboxRetentionGraceDays`, default 30) so a clock skew, a paused
   worker, or a mid-incident config change cannot destroy mail the database has
   not yet re-read.
5. **Delete by message date, not by "processed".** A message that failed to parse
   must age out too, or the mailbox accumulates permanent failures. Parse failures
   are already counted per run (`ParseFailures` on `mailbox_sync_run`), so they
   stay visible.
6. **Preview before purge, mirroring retention.** `GET .../mailbox-retention/preview`
   reports per source: cutoff date, message count, oldest message, and whether
   legal hold is suspending it — the same shape as
   `/api/v1/admin/retention/preview` (`RetentionModule.cs:18`).
7. **Audit every pass.** A deletion of upstream data belongs in the trail.

Checkpoint interaction is safe: IMAP UIDs are monotonic within a UIDVALIDITY, so
deleting messages *below* `LastProcessedUid` never causes a reused UID to be
skipped. The checkpoint is not affected by deletion.

**Keep the recoverability horizon anyway** — `MailboxSource.OldestMessageAtUtc`,
one FETCH of the lowest UID's internal date per sync. Its purpose changes from
mitigation to verification: it is how an operator confirms that mail retention
actually matches the app's window, and it is how the deletion pass proves it did
what it claimed. Surfaced in the console and in the export manifest.

**Consequence to accept explicitly:** with mail deletion on and the archive off,
report data within the retention window has the database as its only complete
copy, plus mail back to the cutoff. That is the correct design — you keep exactly
what you said you keep — but it means a bug in the deletion pass is unrecoverable.
Hence opt-in, grace margin, preview, and audit. Turning the archive on removes
that sharp edge, which is the main reason to have it.

### The optional report archive

Ship the report mail to the same bucket as the config export, at ingest time,
before the retention deletion pass can reach it.

```
s3://bucket/dmarc/reports/2026/07/27/<source-id>/<uid>-<report-id>.eml.gz
```

**Archive the whole message, gzipped, not just the extracted XML.** The message
carries the provenance a bare attachment loses — sending organisation, date,
subject, the envelope — and it is the exact input the existing parser already
handles, so a replay path can reuse `ExtractXmlStreamsAsync` unchanged rather than
growing a second ingestion route.

Three properties make this cheap: objects are written once and never rewritten, so
they cost one PUT each; they need no database space; and they are independent of
the schema, so they survive any migration.

**Replay is a separate capability, and should be built as one.** Archiving gives
you the bytes; getting them back into a database means a bucket-sourced ingestion
path (`POST /api/v1/admin/reports/replay?from=…&to=…`) that walks the prefix and
feeds each object through the parser. Dedup makes it safe to run repeatedly, for
the same reason mailbox replay is safe. Worth stating plainly: **until replay
exists, the archive is evidence, not a restore path.** Ship the archive first if
that is the faster win, but do not describe it as recovery until the replay side
lands.

**The compliance trap.** Archiving to a bucket does not reduce the data footprint —
it relocates it. An archive with no expiry re-creates precisely the unbounded
second copy that bounded mailbox retention was introduced to remove, and it does it
in a store that is easier to forget than a mailbox. So:

- The archive prefix needs a lifecycle rule, and the docs must say so with a
  concrete example rather than leaving it to the operator.
- `data-protection.md` gains a third location in its personal-data inventory
  (database, mailbox, archive), each with its own window.
- **Archive and erasure are opposing goals.** The spec should not pretend otherwise:
  an operator running the archive for continuity has a longer erasure horizon than
  one who does not, and that is a choice to document, not a default to pick for
  them.

### Gap 2 — the re-ingestion rate

`backlog.md:387` gives the arithmetic: 200 messages per pass, hourly, so a
10,000-message backlog takes about two days. Raising the cap to 500 helps
proportionally. The batch cap is the wrong place to fix this, though.

**The answer: drain in a loop, checkpointing each batch, bounded by time rather
than by count.** Keep fetching batches while a full batch comes back and the time
budget allows. The batch size stops being a work limit and becomes what it should
be — a bound on fetches and transactions, and the point at which progress is
committed.

Taking *everything* in one pass instead (the other option raised) is the same idea
with an unbounded batch, and it is worse in two specific ways:

- **It gives up the fetch bound and the checkpoint boundary.** An earlier version
  of this argument said an unbounded selection "materialises the whole UID set",
  and put the case on memory grounds. **That reason was wrong**, and it is worth
  correcting rather than deleting: the pass opens with `inbox.SearchAsync`, which
  returns *every* matching UID and is already held in full today
  (`MailboxSyncService.cs`), and `.Take(n)` only ever bounded the `GetMessageAsync`
  calls that followed it. The UID list costs the same either way. What the batch
  actually bounds is how many whole messages are fetched, parsed and written before
  the loop stops — and, with per-batch checkpointing below, where progress can be
  committed. An unbounded pass has no batch boundary, so it has nowhere to commit
  and a timeout at 90% costs the entire pass.
- **Nothing is gained.** Overlap protection already exists, so "skip the next run
  if this one is still going" is *already the behaviour* — `mailbox_sync_run` has a
  partial unique index on `MailboxSourceId WHERE Status = 'running'`
  (`20260402150000_AddMailboxSyncActiveRunUnique`), and `StaleRunTimeoutMinutes`
  (90) reaps a run that died holding it.

#### Fix the checkpoint write first

The checkpoint is currently lost whenever a run does not finish cleanly, and this
is worth fixing on its own — a drain loop makes it worse, but it is a live
inefficiency today.

The mechanism, traced precisely:

1. `LastProcessedUid` is assigned once, near the end of the run, and saved in a
   single `SaveChangesAsync(operationToken)` (`MailboxSyncService.cs:225-253`).
2. The timeout check throws immediately before that save
   (`MailboxSyncService.cs:233-236`), so on timeout it never runs.
3. The outer handler then calls **`db.ChangeTracker.Clear()`**
   (`MailboxSyncService.cs:273`) — which discards the pending checkpoint
   modification — before adding the `failed` run row.

Step 3 is the actual culprit, and it is there for a good reason: without it, the
failed-run insert would try to flush the same partial state that just failed.

The good news is that the durable-write path already exists.
`TryPersistRunStateAsync` saves with **`CancellationToken.None`**
(`MailboxSyncService.cs:423-436`), which is why the `failed` run row *does* get
written even on a timeout. So the fix is narrow:

- After `ChangeTracker.Clear()`, deliberately re-apply the checkpoint from a local
  variable — re-attach the source and mark `LastProcessedUid` /
  `LastProcessedUidValidity` modified — so it is saved alongside the run row by the
  existing `CancellationToken.None` save. Progress made before a timeout is then
  never thrown away.
- Advance `highestProcessedUid` only **after** a message is fully processed. It
  currently advances at the top of the loop (`MailboxSyncService.cs:112-113`),
  before the fetch and parse, so persisting it on a timeout would otherwise skip a
  message that was never actually read.
- Distinguish the run status: a timeout that made progress is `partial`, not
  `failed`. `failed` for a run that ingested 400 of 900 messages misreads as
  nothing having happened.
- Then, for the drain loop, commit the checkpoint after **each batch** rather than
  once per run — cheap once the write above is correct, and it bounds re-work after
  a crash to a single batch.

Settings, with their real current values:

| Setting | Committed default | Notes |
|---|---|---|
| `Worker__MaxMessagesPerSync` | `200` (`appsettings.json:24`) | becomes a per-batch bound |
| `Worker__ScheduleIntervalSeconds` | `3600` | the outer cadence |
| `Worker__SyncRunTimeoutMinutes` | `30` | must be ≥ the new drain budget, or the drain is cancelled by it |
| `Worker__MailboxDrainBudgetMinutes` | **new**, default ~20 | stop starting batches past this |

Two things to fix while here: the repo's dev `docker-compose.yml:44` sets
`Worker__MaxMessagesPerSync: 50`, four times *slower* than `appsettings.json`
(`deploy/compose.yml`, the published file, sets nothing and so inherits 200) — and
`SyncRunTimeoutMinutes` at 30 with a 20-minute drain budget leaves little
headroom, so they need to move together.

With per-batch checkpointing and a drain loop, a fresh install's first sync drains
the whole mailbox on its own, which is exactly the recovery behaviour wanted.

**Recovery still wants the opposite order from steady state.** Today the search is
`SearchQuery.Uids(lastProcessedUid+1 → MaxValue)` then `.Take(n)` — UID-ascending,
oldest-first. Correct for steady-state backfill; backwards for recovery, where you
want the newest 30 days first so the console is useful within the hour. It matters
more than convenience: the analytics window anchors to the newest report
*tenant-wide* (`AnalyticsQueryService.ResolveWindowAsync`), so an oldest-first
restore makes every domain read as stale until the whole replay finishes. Propose
an opt-in newest-first mode, used during recovery only.

### Gap 3 — history that no replay can reconstruct

`audit_event` is the compliance record of the install itself (730-day retention,
`RetentionOptions.cs:11`, deliberately exempt from legal hold). `alert_event` and
`digest_delivery` are computed from reports *at evaluation time*, so re-ingesting
reports does not replay them. `mailbox_sync_run` is the operational record.

**The answer: an offload worker that ships them continuously to object storage.**
The exclusion criterion is volume, not category — the export omits exactly four
tables (`dmarc_report`, `dmarc_report_record`, and the two auth-result tables) and
carries everything else.

These tables are append-only and never updated after insert, which is what makes
continuous shipping cheap:

```
s3://bucket/dmarc/
  config/latest.json                  ← tier 0, overwritten every pass
  config/2026-07-27.json              ← daily dated copy (see below)
  history/audit/2026-07-27T0800.jsonl ← tier 1, written once, never rewritten
  history/alert/…  history/digest/…  history/sync-run/…  history/ingest/…
```

**Cadence** follows the existing scheduled-pass shape (`Digest:CheckIntervalHours`,
`Dns:RefreshIntervalHours`, `Worker:RetentionIntervalHours`):
`Backup__IntervalMinutes: 30`. The single-worker constraint is already enforced by
a Postgres advisory lock, so there is no double-shipping to design around.

**The watermark, without a schema change.** Ship rows newer than the last
watermark, but re-ship a fixed overlap window (say 15 minutes) on every pass, and
let import dedupe on the primary key. This is idempotent by construction and
immune to the failure mode a bare timestamp cursor has: a row committed slightly
after a concurrently-read `now()` is skipped forever. The alternative — adding a
`bigserial` cursor column to each history table — is cleaner in theory and costs a
migration on the largest tables; not worth it. Watermarks persist per stream
(a small `backup_stream_state` table, or a settings row).

**Overwriting `latest.json` is the one real risk in this design.** A pass that
succeeds while producing a truncated or empty document destroys the good copy, and
you find out during a recovery. Three mitigations, all cheap:

1. Write to a temp key and copy to `latest.json` only after the document validates
   (parses, manifest present, client count > 0).
2. Keep a dated daily copy alongside, so the blast radius of a bad overwrite is
   one day.
3. **Require bucket versioning, and verify it rather than recommending it.** A
   versioned bucket makes any bad overwrite recoverable, which is the strongest of
   the three. The offload worker checks the bucket's versioning state on startup
   (`GetBucketVersioning`, one call) and logs a prominent warning when it is
   disabled — turning a line of documentation nobody reads into a fact the operator
   is told about. Do not hard-fail on it: MinIO and some S3-compatible backends
   report versioning inconsistently, and a backup that refuses to run is worse than
   an unversioned one. Surface it in the console's backup status instead.

**Two consequences worth writing down before this ships:**

- **`data-protection.md` claims its outbound connection list is exhaustive** —
  "IMAP to the mailboxes you configured, DNS lookups, SMTP to your relay, HTTPS to
  your identity provider." Object storage is a fifth. That page must be updated in
  the same change, or it becomes wrong in the one place it promises completeness.
- **The bucket holds credential material.** The artifact carries `enc:v1:` mailbox
  ciphertext and PBKDF2 password hashes. The bucket must be private, and
  `DMARC_ENCRYPTION_KEY` must **never** be shipped to it — co-locating the pair is
  exactly what the existing backup docs tell operators not to do. The manifest
  carries a key *fingerprint*, not the key.

Target is S3-compatible generally (AWS, MinIO, R2, B2), credentials via config or
ambient IAM role.

## Artifact format

One JSON document per snapshot; JSONL for history chunks. `formatVersion` is the
compatibility gate — an importer refuses a version it does not know rather than
guessing.

```json
{
  "manifest": {
    "formatVersion": 1,
    "exportedAtUtc": "2026-07-27T08:00:00Z",
    "appVersion": "0.2.2",
    "migrationId": "20260725210431_AddDmarcReportRecordRangeBegin",
    "migrationCount": 17,
    "encryptionKeyFingerprint": "sha256:8f3a1c9d2b4e6a70",
    "credentialsProtected": true,
    "scope": { "config": true, "history": "shipped-separately", "reports": "none" },
    "excluded": { "dmarc_report": 174233, "dmarc_report_record": 5312880 },
    "legalHoldClients": ["acme"],
    "mailboxRetention": [
      { "mailboxSource": "dmarc@example.com",
        "deleteAfterRetention": true,
        "cutoffUtc": "2024-01-27T00:00:00Z",
        "oldestMessageAtUtc": "2024-02-02T06:11:00Z" }
    ]
  },
  "clients": [], "domains": [], "mailboxSources": [],
  "notificationRecipients": [], "users": [], "userIdentities": [], "grants": []
}
```

Three manifest fields carry most of the design:

**`excluded`, with row counts.** The artifact states what it is *not*. A file that
silently omits 5.3M rows is a trap; one that says so is a backup with a scope.

**`encryptionKeyFingerprint`.** The first 8 bytes, hex, of SHA-256 over the base64
key string. `AesGcmCredentialProtector` stores `enc:v1:` as a *format* version with
no key identity in it, so today a wrong key surfaces as an
`AuthenticationTagMismatchException` on the next mailbox sync — long after the
restore looked successful. The fingerprint makes "do I hold the right key for this
file?" a check that runs **before** importing. It identifies the key without
enabling decryption, so it is safe to store beside the artifact.

**`mailboxRetention`.** Together with `oldestMessageAtUtc` this is the evidence
that the archive claim holds — that the mailbox really does go back as far as the
retention window, and that the deletion pass is cutting where it says.

## Import identity

Guid primary keys are client-generated (`= Guid.NewGuid()` on every entity), so
exporting Ids verbatim and importing them as-is keeps every foreign key valid with
no rewiring. Natural keys are the fallback for merge mode and for detecting
conflicts. All verified against `DmarcAnalyzerDbContext.cs`:

| Entity | Natural key | Source |
|---|---|---|
| `client` | `slug` | `:104` |
| `domain` | `name` (globally unique) | `:112` |
| `agency_user` | `email` | `:36` |
| `user_identity` | `(issuer, subject)` | `:63` |
| `user_client_grant` | `(userId, clientId)` | `:76` |
| `notification_recipient` | `(clientId, email)`; null `clientId` = agency-wide | `:263` |
| `digest_delivery` | `(clientId, periodStartUtc)` | `:296` |
| `dmarc_report_ingest` | `(clientId, policyDomain, reportId, begin, end)` | `:154-160` |
| `mailbox_source` | **none — `id` only** | no unique index beyond the PK |
| `alert_event`, `audit_event`, `mailbox_sync_run` | **none — `id` only** | non-unique indexes only |

`mailbox_source` having no unique constraint is worth calling out: two sources may
legitimately share host and username (different folders, different default
clients), so import cannot dedupe them on anything but the Id.

## Import happens during setup, not from a shell

No `APP_MODE=export` / `APP_MODE=import`. Import is a **first-run action in the
console**: bring up a clean install, create the first administrator through the
existing bootstrap flow, then import as the first thing that account does.

This works because the bootstrap gate already exists —
`GET /api/v1/auth/setup` returns `requiresBootstrap` (`AuthModule.cs:27-31`) — so
"is this a clean install?" is a question the app can already answer.

```
GET  /api/v1/admin/config/import/preview     ← what would change, no writes
POST /api/v1/admin/config/import             ← mode=restore|merge
```

Sources: an uploaded file, or `config/latest.json` pulled from the configured
bucket — the latter matters, because in a real recovery the operator should not
have to find and download the artifact by hand.

Two modes, because restore and clone are different jobs:

- **`restore`** — the DR path. Allowed only when the install is empty (no clients,
  no domains). Writes Ids as given, fails on any pre-existing row rather than
  merging into unknown state.
- **`merge`** — the clone/seed path. Upserts by natural key, keeps the existing
  row's Id where one is found, reports Id-vs-natural-key conflicts instead of
  resolving them silently.

### Import never deletes

**Import is additive. It inserts and updates; it never deletes a row.** A user,
grant, client, domain or recipient that exists in the install but not in the
artifact is left exactly as it is.

The artifact contains `agency_user` rows, and the operator is logged in as a
freshly bootstrapped admin that is not one of them. The rule that follows:

- **On an email collision, the imported user wins** — password hash, display name,
  role and active flag are all taken from the artifact. After a restore you log in
  with your pre-disaster credentials, which is what makes the restore faithful and
  what the operator's password manager already holds.
- **The bootstrap account is never removed.** If its email does not collide, it
  survives as a break-glass administrator, which is a useful thing to keep after a
  disaster rather than something to clean up.
- **Sessions are invalidated only for users whose hash actually changed.** If the
  bootstrap admin's email did not collide, their session stays valid and the import
  completes without logging them out. If it did collide, their password just
  changed under them and that one session must go.
- The import response states which accounts were created, which were updated, and
  which credentials to use next.

This invariant is also why `restore` mode requires an empty install: a
non-destructive import cannot faithfully reproduce a state in which something was
*deleted* before the disaster, so restoring into a populated install would produce
a union, not a copy. `merge` mode is a union on purpose; `restore` refuses to
pretend.

Both modes support a dry-run preview, mirroring the existing
`retention/preview` → `purge` pairing.

## Deliberately excluded from the artifact

Not oversights — each would be wrong to carry:

- **`user_session`** — 12h idle / 7d absolute sessions. Restoring live sessions
  would resurrect authenticated cookies; a security hole, not a feature.
- **`Domain.DnsPolicy` / `DnsLookupStatus` / `DnsCheckedAtUtc`** — a cache the
  worker's DNS pass refreshes, annotated as such on the entity.
- **`MailboxSource.LastProcessedUid` / `LastProcessedUidValidity`** — must reset so
  a restored source rescans from the beginning. Carrying a checkpoint over would
  skip mail, and UIDVALIDITY makes a stale value actively misleading.
- **`MailboxSource.LastSuccessSyncAtUtc`** — derived from sync runs.
- **`AgencyUser.LastLoginAtUtc`** — derived state, and misleading if carried: the
  account is restored, but the login it records did not happen on this install.
  The password hash comes across; the activity record does not.
- **`__EFMigrationsHistory`** — recorded in the manifest for comparison, never
  imported. The target install migrates itself.

Export is **config as typed**, not rows as stored.

## Security

The artifact is a **credential file**, not a config file. Handle it as you would a
database dump; a private bucket, and never the encryption key alongside it.

**If no encryption key is configured, the export contains plaintext mailbox
passwords.** `AddCredentialProtection` falls back to `NullCredentialProtector` with
only a log warning when `Security:CredentialEncryptionKey` is unset
(`CredentialProtectionExtensions.cs:11-23`), so those rows are plaintext in the
database and would be plaintext in the artifact — and shipping them to object
storage makes that considerably worse than leaving them in Postgres. Offload must
**refuse to start** in that state, and manual export must require an explicit
override and set `credentialsProtected: false`. This matters more than it looks:
`backlog.md` already records that `docs/ops/configuration.md` wrongly describes
that setting as having "no usable default", so operators may believe they are
protected when they are not.

Import refuses an artifact whose `encryptionKeyFingerprint` does not match the
running key by default, rather than silently importing sources that can never
decrypt. `allowKeyFingerprintMismatch` lets an operator proceed anyway — the
rest of the artifact still imports, and every mailbox source under the old key
needs its password re-entered by hand afterward. The console surfaces this as
an acknowledgeable warning rather than a dead end.

Every export, offload failure, import, and mailbox-deletion pass writes an audit
event.

## Phasing

| Phase | Deliverable | Schema change |
|---|---|---|
| 0 | **Checkpoint durability fix** — persist `LastProcessedUid` past a timeout, `partial` run status | no |
| 1 | Export (tier 0) with manifest, admin endpoint + download | no |
| 2 | Offload worker: S3 target, `latest.json`, validate-then-copy, dated dailies, versioning check | small (stream state) |
| 3 | History streams (tier 1) with overlap-window watermarks | no |
| 4 | Import: preview, `restore`/`merge`, pull from bucket, wired into first-run | no |
| 5 | Drain loop, per-batch checkpointing, drain budget (gap 2) | no |
| 6 | Report mail archive to bucket (tier 2), opt-in | no |
| 7 | Mailbox retention deletion (gap 1): opt-in, preview, legal-hold suspension, archive-first ordering | small (`MailboxSource` flags, `OldestMessageAtUtc`) |
| 8 | Bucket replay path — the archive becomes a restore path rather than evidence | no |

**Phase 0 is a bug fix and should not wait for the rest.** It is small, it is
independent of every backup concern here, and it makes today's syncs stop throwing
away progress on a timeout.

Phases 1-4 are the backup story. Phase 5 stands alone. Phase 6 must land before
phase 7 if the archive is wanted at all, because 7 deletes what 6 preserves —
that ordering is the whole reason 7 ships last, opt-in, and with a preview.

## Follow-ups outside this spec

- `backlog.md:380` asks for a **restore-from-backup drill**. Phases 1-4 make it
  scriptable in the style of the website's `scripts/verify-docs-snippets.sh`:
  export, import into a throwaway instance, compare manifest counts, then sync a
  mailbox source — the only thing that actually proves the encryption key
  round-tripped.
- The same artifact answers `backlog.md`'s "no documented way to seed a dev
  database" (a committed `merge`-mode fixture) and reduces the website's "Moving
  to another host" section to a clean install plus an import.
- **`data-protection.md` needs three edits** independent of the code: the mailbox
  belongs in the personal-data inventory, the report archive belongs there too as a
  third location with its own window, and object storage belongs in the
  outbound-connections list that page currently presents as exhaustive.
- Key rotation becomes tractable once a key has an identity: adding a key id to
  the `enc:v1:` prefix, combined with the existing re-protect-on-first-use path
  (`MailboxSyncService.cs:59-70`), makes a staged rotation possible. Worth its own
  ADR, not this one.
