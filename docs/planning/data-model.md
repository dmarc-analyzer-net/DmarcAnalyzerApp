# Data Model

Data model for `DmarcAnalyzerApp`.

**Part A documents the schema as built** — table and column names match the EF
Core entities in `src/api/Data/Entities` and the migrations, so it is safe to
code against. **Part B is unbuilt target state** for features on the roadmap.

> Names in Part B are provisional. The original version of this document
> described the whole model up front, and the names it guessed for the ingestion
> and report tables (`dmarc_record`, `sync_run`, `dmarc_auth_result`,
> `raw_report`) are *not* what shipped. Treat Part B as intent, and reconcile it
> here whenever something lands.

## 1) Modeling Principles

- Single PostgreSQL database.
- Strict tenant scoping: every client-owned row reaches a `client` either
  directly or transitively (see §3).
- Global domain uniqueness — a domain belongs to exactly one client, enforced by
  a unique index on `domain."Name"`.
- Report deduplication by business key, not file hash.
- Operational traceability through sync-run records.
- Column names are PascalCase (EF Core default, quoted in SQL); table names are
  snake_case via explicit `ToTable`.

---

# Part A — Implemented schema

Thirteen tables. Keys and indexes below are the ones actually configured in
`DmarcAnalyzerDbContext`.

## A.1 Identity and access

### `agency_user`
Operator accounts (agency staff and client viewers).

| Column | Notes |
|---|---|
| `Id` | PK, uuid |
| `Email` | max 255, **unique** |
| `PasswordHash` | max 512, PBKDF2-SHA256 |
| `DisplayName` | max 200 |
| `Role` | max 32 — `agency_admin` \| `agency_analyst` \| `client_viewer` |
| `IsActive` | bool |
| `LastLoginAtUtc` | nullable |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

### `user_session`
Server-side sessions behind the `dmarc_session` cookie.

| Column | Notes |
|---|---|
| `Id` | PK |
| `UserId` | FK → `agency_user`, **cascade delete**, indexed |
| `CookieId` | max 128, **unique** |
| `CreatedAtUtc`, `LastSeenAtUtc` | drives the 12h idle timeout |
| `ExpiresAtUtc` | indexed; 7d absolute cap |
| `RevokedAtUtc` | nullable — set on logout |
| `IpAddress` (64), `UserAgent` (512) | nullable |

### `user_identity`
External (OIDC) identities linked to a local user. See ADR 0007.

| Column | Notes |
|---|---|
| `Id` | PK |
| `UserId` | FK → `agency_user`, **cascade**, indexed |
| `Issuer` (512), `Subject` (255) | **unique together** — the OIDC identity key |
| `EmailAtLink` | max 255, nullable — email seen when the link was made |
| `CreatedAtUtc`, `LastLoginAtUtc` | |

### `user_client_grant`
Which clients a `client_viewer` may read. The tenancy gate for viewers.

| Column | Notes |
|---|---|
| `Id` | PK |
| `UserId` | FK → `agency_user`, **cascade** |
| `ClientId` | FK → `client`, **cascade**, indexed |
| — | `(UserId, ClientId)` **unique** |
| `CreatedByUserId` | FK → `agency_user`, **set null** on delete |
| `CreatedAtUtc` | |

## A.2 Clients and domains

### `client`
The tenant.

| Column | Notes |
|---|---|
| `Id` | PK |
| `Name` | max 200 |
| `Slug` | max 120, **unique** |
| `IsActive` | bool |
| `RetentionMonths` | default 27 — enforced by the daily retention pass, measured against report **window end** (`RangeEndUtc`), not ingest date |
| `LegalHold` | default false — when true the client is skipped entirely by retention purging |
| `AlertsEnabled` | default true — turns alerting off for this client only |
| `AlertComplianceDropPercent` | nullable — overrides `Alerts:ComplianceDropPercent` |
| `AlertMinMessages` | nullable — overrides `Alerts:MinMessages` |
| `Timezone` | max 64, default `UTC` |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

### `domain`
A monitored domain. Globally unique across all clients.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ClientId` | FK → `client`, **restrict** delete, indexed |
| `Name` | max 255, **unique globally** |
| `IsActive` | bool |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

## A.3 Mailbox sources and sync history

### `mailbox_source`
An IMAP mailbox to poll. Also carries its own sync checkpoint — there is no
separate checkpoint table.

| Column | Notes |
|---|---|
| `Id` | PK |
| `Name` (200), `Host` (255), `Port`, `UseTls` | |
| `Protocol` | max 20 — `imap` (POP3 not implemented) |
| `Username` | max 255 |
| `PasswordEncrypted` | max 2048 — AES-256-GCM via `Security:CredentialEncryptionKey` |
| `DefaultClientId` | FK → `client`, **restrict**, indexed — client assigned to domains auto-created from this mailbox |
| `IsActive` | bool |
| `LastSuccessSyncAtUtc` | nullable |
| `LastProcessedUid`, `LastProcessedUidValidity` | bigint, nullable — **the resumable backfill checkpoint** |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

### `mailbox_sync_run`
One row per sync attempt; the operational audit trail behind
`GET /mailbox-sync-runs` and `GET /mailbox-health`.

| Column | Notes |
|---|---|
| `Id` | PK |
| `MailboxSourceId` | FK → `mailbox_source`, **restrict**, indexed |
| `Trigger` | max 32 — `scheduled` \| `manual` |
| `Status` | max 32 — `running` \| `success` \| `failed` |
| `StartedAtUtc` | indexed |
| `FinishedAtUtc` | nullable — null while running |
| `MessagesScanned`, `AttachmentsProcessed`, `ReportsInserted`, `ReportsSkippedAsDuplicate`, `ParseFailures` | counters |
| `Error` | max 4000, nullable |
| `CreatedAtUtc` | |

Stale `running` rows are auto-closed to `failed` by the worker after
`Worker:StaleRunTimeoutMinutes`.

## A.4 DMARC report data

### `dmarc_report_ingest`
Ingestion ledger, keyed by what the *mailbox* delivered. Kept separate from
`dmarc_report` because it records the client/mailbox provenance of each accepted
file, independent of the domain the report resolves to.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ClientId` | FK → `client`, **restrict**, indexed |
| `MailboxSourceId` | FK → `mailbox_source`, **restrict**, indexed |
| `PolicyDomain` | max 255 — domain as stated in the report |
| `ReportId` | max 255 |
| `ReportRangeBeginUtc`, `ReportRangeEndUtc` | |
| `OrganizationName` | max 255 |
| `RecordCount` | |
| `IngestedAtUtc` | |
| — | composite **unique** across client + mailbox + policy domain + report id + range |

### `dmarc_report`
A normalized aggregate (RUA) report, resolved to a `domain`.

| Column | Notes |
|---|---|
| `Id` | PK |
| `DomainId` | FK → `domain`, **restrict**, indexed |
| `MailboxSourceId` | FK → `mailbox_source`, **restrict**, indexed |
| `OrganizationName` | max 255 — the reporter (e.g. `google.com`) |
| `ReportId` | max 255 |
| `RangeBeginUtc`, `RangeEndUtc` | reporting window |
| `RecordCount` | |
| `IngestedAtUtc` | |
| `PublishedPolicy` | max 16, default `none` — `policy_published.p` |
| `SubdomainPolicy` | max 16, default `none` — `sp` |
| `PublishedPct` | default 100 — `pct` |
| `DkimAlignment`, `SpfAlignment` | max 16, default `relaxed` — `adkim` / `aspf` |
| — | **`(DomainId, ReportId, RangeBeginUtc, RangeEndUtc)` unique — the dedup key** |

The `policy_published` columns were added after initial ingestion, so reports
ingested before then carry the defaults (`p=none`) until re-ingested.

> Dedup note: the unique key is domain-scoped, not client- or org-scoped. Because
> `domain."Name"` is globally unique, the client is implied; the reporting org is
> deliberately excluded so the same report cannot be double-counted if it arrives
> via two mailboxes.

### `dmarc_report_record`
One row per sending source within a report — the grain analytics aggregates over.

| Column | Notes |
|---|---|
| `Id` | PK |
| `DmarcReportId` | FK → `dmarc_report`, **cascade**, indexed |
| `SourceIp` | max 64 |
| `MessageCount` | |
| `Disposition` | max 32 — `none` \| `quarantine` \| `reject` |
| `DkimResult`, `SpfResult` | max 32 — **policy-evaluated** (i.e. authenticated *and* aligned) |
| `HeaderFrom`, `EnvelopeFrom`, `EnvelopeTo` | max 255 |

A message is DMARC-compliant when `DkimResult = 'pass' OR SpfResult = 'pass'`.

### `dmarc_report_record_dkim_auth_result`
Raw DKIM verdicts underlying a record (a record may have several signatures).

| Column | Notes |
|---|---|
| `Id` | PK |
| `DmarcReportRecordId` | FK → `dmarc_report_record`, **cascade**, indexed |
| `Domain` | signing domain (`d=`) |
| `Selector` | `s=` |
| `Result`, `HumanResult` | |

### `dmarc_report_record_spf_auth_result`
Raw SPF verdicts underlying a record.

| Column | Notes |
|---|---|
| `Id` | PK |
| `DmarcReportRecordId` | FK → `dmarc_report_record`, **cascade**, indexed |
| `Domain` | checked domain |
| `Scope` | `mfrom` \| `helo` |
| `Result`, `HumanResult` | |

## A.5 Notifications and alerts

### `notification_recipient`
Who gets notified. A **null `ClientId` is the agency-wide scope** — that address
receives notifications for every client.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ClientId` | FK → `client`, **cascade**, indexed; null = agency-wide |
| `Email` | max 320 |
| `Kind` | max 16 — `alert` \| `digest` \| `both` |
| `IsActive` | default true |
| — | `(ClientId, Email)` **unique** |

### `alert_event`
Raised alerts. Persisted so the same problem isn't emailed repeatedly (the
evaluation service checks for a recent event of the same kind) and so operators
can see history.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ClientId` | FK → `client`, **cascade** |
| `DomainId` | FK → `domain`, **set null**; null for client-wide alerts |
| `RuleType` | max 32 — `failure_spike` \| `policy_regression` |
| `Severity` | max 16 — `info` \| `warning` \| `critical` |
| `Status` | max 16 — `open` \| `acknowledged` \| `closed` |
| `Title`, `Details` | max 300 / 4000 |
| `DetectedAtUtc` | indexed; `(ClientId, RuleType, DetectedAtUtc)` indexed for the cooldown lookup |
| `NotifiedAtUtc` | nullable — null when no recipient or no relay |

### `digest_delivery`
One row per client per digest period. The unique index is the idempotency
guarantee.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ClientId` | FK → `client`, **cascade** |
| `PeriodStartUtc`, `PeriodEndUtc` | the covered month |
| `SentAtUtc` | |
| `RecipientCount` | 0 when recorded but nothing was delivered (no relay) |
| — | `(ClientId, PeriodStartUtc)` **unique** |

## A.5.1 What is deliberately *not* a table

- **No pre-aggregated metrics table.** Dashboard figures are computed on demand
  by `AnalyticsQueryService` over `dmarc_report_record`, with one hand-written
  `GROUP BY` for per-source aggregation (EF's grouped navigations produced
  per-group correlated subqueries — 33s vs ~75ms for a domain with 1.3k sources).
  Revisit if data volume outgrows it.
- **No job table.** Background work is the worker polling `mailbox_source` and
  writing `mailbox_sync_run`; there is no generic queue table.
- **No separate checkpoint table** — checkpoints live on `mailbox_source`.

---

# Part B — Planned (not implemented)

None of the following exist yet. Each maps to a backlog item; names and columns
are provisional.

| Planned table(s) | Purpose | Backlog item |
|---|---|---|
| `alert_rule` | Per-client alert *rules* as rows. Shipped instead as threshold columns on `client`, which covers per-client tuning without another CRUD surface | — |
| `digest_schedule` | Per-client digest cadence as rows. Shipped instead as a single global `Digest:DayOfMonth` — per-client schedules had no demand | — |
| `export_job` | Async CSV/JSON export | analytics export |
| `pdf_report_job` | Branded PDF summaries | branded PDF reports |
| `magic_link_nonce` | Signed single-client read-only links (7-day default), revocable via DB nonce | magic link access |
| `audit_event` | Login, config change, sync run, magic-link usage | core audit logging |
| archival before deletion | purging deletes outright; archiving to cold storage first is not implemented | — |
| daily rollup table | Only if on-demand aggregation stops scaling (see A.5) | — |

---

## 3) Tenancy paths

Every client-scoped read resolves to a `client` by one of these paths. Viewers
are filtered by `user_client_grant`; cross-tenant ids must read as **404**, never
403 (no existence oracle).

```
client
  ← domain.ClientId
      ← dmarc_report.DomainId
          ← dmarc_report_record.DmarcReportId
              ← dmarc_report_record_{dkim,spf}_auth_result.DmarcReportRecordId
  ← mailbox_source.DefaultClientId
      ← mailbox_sync_run.MailboxSourceId
  ← dmarc_report_ingest.ClientId
  ← user_client_grant.ClientId
```

## 4) Key integrity rules

- `domain."Name"` is **globally unique** — a domain cannot belong to two clients.
- `dmarc_report` dedup: unique on `(DomainId, ReportId, RangeBeginUtc, RangeEndUtc)`.
- `agency_user."Email"`, `client."Slug"`, `user_session."CookieId"` are unique;
  `user_identity` is unique on `(Issuer, Subject)`; `user_client_grant` on
  `(UserId, ClientId)`.
- Report data cascades on delete (report → records → auth results). Business
  entities (`client`, `domain`, `mailbox_source`) use **restrict** so tenant data
  cannot be silently orphaned.
- Unknown domains encountered during ingestion are auto-created under the
  originating mailbox's `DefaultClientId`.

## 5) Type mapping (EF Core → PostgreSQL)

- `Guid` → `uuid`, generated client-side.
- `DateTime` → `timestamp with time zone`; **all timestamps are UTC** and named
  `…Utc`.
- `string` → `varchar(n)` where a `HasMaxLength` is set.
- `int`/`long` → `integer`/`bigint`.

## 6) Query patterns to optimize

- Records in a window for a tenant: `dmarc_report.RangeBeginUtc` + `DomainId`.
- Per-source aggregation for one domain (the drill-down and threat feed).
- Latest published policy per domain (top-1 per group by `RangeEndUtc`).
- Newest report end date per tenant — analytics windows anchor to the newest
  report rather than wall-clock time, since backfilled mailboxes lag.

## 7) Open model questions

- Does retention purge by report end date only, or also archive first?
- Should `dmarc_report_ingest` be pruned independently of `dmarc_report`?
- Do forensic (RUF) reports get their own tables, or extend `dmarc_report`?
