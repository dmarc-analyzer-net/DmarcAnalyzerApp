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
| `DnsPolicy` | max 16 — the **effective** policy a receiver applies. Cached so list views render it from one query instead of a DNS lookup per row. |
| `DnsLookupStatus` | max 16 — `found` (own record), `inherited` (an ancestor's), `missing`, `lookup_failed`. |
| `DnsPolicyInheritedFrom` | max 253 — the ancestor `DnsPolicy` came from, when inherited. Null otherwise. |
| `DnsCheckedAtUtc` | when the three above were last refreshed. |
| `CreatedAtUtc`, `UpdatedAtUtc` | `UpdatedAtUtc` means *an operator changed this domain*; the DNS refresh deliberately does not touch it. |

`DnsPolicy` is the policy that **applies**, not necessarily the one this domain
publishes. A subdomain with no DMARC record of its own is not unprotected: RFC 7489
§6.6.3 has the receiver fall back to the organisational domain and apply its `sp=`,
or its `p=` when there is no `sp=`. `DmarcPolicyResolver` walks up the DNS tree to
find it, so `email.example.com` under a `p=reject` parent is stored as `reject` with
`DnsLookupStatus = inherited`.

Two things make that worth storing rather than deriving on read. The ancestor is
usually **not** a monitored domain — in one real instance, 39 of 44 subdomain-shaped
domains had no parent row, because reports only ever arrive for the sending
subdomain — so the answer cannot come from this table. And a wrong inheritance
should be legible: the source name is shown in the console next to the policy, so
"reject, via example.com" can be checked rather than silently trusted.

A failed lookup keeps the previous policy *and* source. A transient SERVFAIL must
not make a `p=reject` domain look unprotected; only a successful lookup finding
nothing anywhere clears them.

## A.3 Report sources and sync history

### `report_source`
A place reports arrive from. Today that is always an IMAP mailbox and `Protocol`
says so; the name is the general one because the column already allows others.
Also carries its own sync checkpoint — there is no separate checkpoint table.

Was `mailbox_source` until the rename; the audit-log action names and the config
export's entity keys still read `mailbox_source`, because both are values in data
already written rather than references to the table.

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
| `AllowForeignDomains` | May ingest reports for domains another client owns. Default **true**, which is how every source behaved before it existed — routing by policy domain is what makes one shared mailbox usable for many clients. |
| `DeleteAfterRetention` | default false — opt-in per source; the worker expunges report mail past the *widest* retention window among the clients this source serves, suspended entirely if any of them is under legal hold |
| `OldestMessageAtUtc` | nullable — internal date of the oldest message still in the polled folder, refreshed each sync; the evidence for how far back the mailbox can still archive-replay from |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

### `mailbox_sync_run`
One row per sync attempt; the operational audit trail behind
`GET /mailbox-sync-runs` and `GET /mailbox-health`.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ReportSourceId` | FK → `report_source`, **restrict**, indexed |
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
| `ReportSourceId` | FK → `report_source`, **restrict**, indexed |
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
| `ReportSourceId` | FK → `report_source`, **restrict**, indexed |
| `OrganizationName` | max 255 — the reporter (e.g. `google.com`) |
| `ReportId` | max 255 |
| `RangeBeginUtc`, `RangeEndUtc` | reporting window |
| `RecordCount` | |
| `IngestedAtUtc` | |
| `PublishedPolicy` | max 16, default `none` — `policy_published.p` |
| `SubdomainPolicy` | max 16, **nullable** — `sp`. NULL means the reporter sent no `sp` tag, so subdomains inherit `p` (RFC 7489 §6.3). Rows ingested before this became nullable store `none` either way. |
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
| `ReportRangeBeginUtc` | Denormalised copy of `dmarc_report.RangeBeginUtc`, **indexed**. Written by ingestion; never updated, since a report's range is fixed once stored. |

A message is DMARC-compliant when `DkimResult = 'pass' OR SpfResult = 'pass'`.

`ReportRangeBeginUtc` is the one deliberate denormalisation in the schema. Every
analytics query is scoped to a time window that logically lives on the parent report,
but filtering through the navigation made the planner hash-join and scan all 5.27M
records to keep the ~3% inside the window — once per aggregate. Indexing the report
side does not fix it (measured; the planner keeps the full record scan). Carrying the
date on the record turns the window into a bitmap index scan. It is safe to duplicate
precisely because it is immutable: nothing updates a report's range after ingestion.

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

## A.4.1 SMTP TLS report data (TLS-RPT, RFC 8460)

### `smtp_tls_report`
One TLS report as the reporter sent it. **No DomainId** — a single report can
carry policies for several policy-domains, possibly of different clients, so
tenancy and analytics hang off the policy rows.

| Column | Notes |
|---|---|
| `Id` | PK |
| `ReportSourceId` | FK → `report_source`, **restrict**, indexed |
| `OrganizationName`, `ReportId` | max 255 each; with the range, the dedupe key — unique `(OrganizationName, ReportId, RangeBeginUtc, RangeEndUtc)`: without a domain in the key, the org disambiguates report-id collisions across reporters |
| `ContactInfo` | max 320, nullable |
| `RangeBeginUtc`, `RangeEndUtc` | `RangeEndUtc` indexed for the orphan sweep |
| `PolicyCount`, `TotalSuccessfulSessionCount`, `TotalFailureSessionCount` | denormalized sums for list views |
| `IngestedAtUtc` | |

### `smtp_tls_report_policy`
One policy block — the tenancy and analytics level. The report window is
denormalized here for the same reason it is on `dmarc_report_record`.

| Column | Notes |
|---|---|
| `Id` | PK |
| `SmtpTlsReportId` | FK → `smtp_tls_report`, **cascade**, indexed |
| `DomainId` | FK → `domain`, **restrict** — resolved per policy-domain via the same create-or-get DMARC ingestion uses (`DomainIngestResolver`) |
| `PolicyType` | max 32 — `sts` \| `tlsa` \| `no-policy-found`, unknown kept raw |
| `PolicyDomain` | max 255 — as reported, normalized lowercase |
| `PolicyString`, `MxHostPatterns` | max 4000 / 2000, newline-joined when they arrived as arrays |
| `SuccessfulSessionCount`, `FailureSessionCount` | bigint |
| `ReportRangeBeginUtc`, `ReportRangeEndUtc` | `(DomainId, ReportRangeBeginUtc)` indexed for windows; `ReportRangeEndUtc` for retention |

### `smtp_tls_failure_detail`

| Column | Notes |
|---|---|
| `Id` | PK |
| `SmtpTlsReportPolicyId` | FK → `smtp_tls_report_policy`, **cascade**, indexed |
| `ResultType` | max 64 — the reporter's value, lowercased but raw (RFC 8460 has no closed registry) |
| `FailureCategory` | max 16 — `sts` \| `dane` \| `transport` \| `other`, computed at ingest by `TlsRptFailureClassifier`. `validation-failure` lands in `sts` deliberately (conservative for the promotion gate); re-bucketing is a classifier edit plus an UPDATE because the raw type survives |
| `SendingMtaIp`, `ReceivingMxHostname`, `ReceivingMxHelo`, `ReceivingIp` | |
| `FailedSessionCount` | a present row asserts ≥ 1; a missing count parses as 1 |
| `AdditionalInformation`, `FailureReasonCode` | max 2000 / 255 |

### `tls_report_ingest`
The TLS provenance ledger — a **parallel** of `dmarc_report_ingest`, not a
discriminator on it, so the DMARC ledger's unique key and purge stay untouched.
Unique `(ClientId, OrganizationName, ReportId, ReportRangeBeginUtc,
ReportRangeEndUtc)` — the DMARC key with the policy domain (meaningless for a
multi-domain report) swapped for the organization name. `PolicyDomains` is a
comma-joined, truncated copy for post-purge "did we ever receive it" searches.
`ClientId` is the report source's default client, exactly as for DMARC.

Retention: policy rows purge per client on the window end; the ledger likewise;
report rows left with no policies sweep once older than the **longest**
retention across clients — so a legal-hold client's rows never delete and its
reports never orphan. The three report tables are excluded from the backup
config artifact (replayable from the mailbox); the ledger ships as its own
history stream.

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
| `RuleType` | max 32 — `failure_spike` \| `policy_regression` \| `mta_sts_policy_change` \| `mta_sts_broken` \| `mta_sts_mx_mismatch` |
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

### `audit_event`
Who did what. **No foreign keys on purpose** — a trail that loses meaning when a
user or client row is deleted isn't a trail, so the actor email is copied in at
write time.

| Column | Notes |
|---|---|
| `Id` | PK |
| `OccurredAtUtc` | indexed; also `(EventType, OccurredAtUtc)` and `(ClientId, OccurredAtUtc)` |
| `ActorType` | max 16 — `user` \| `system` \| `anonymous` |
| `ActorUserId` | nullable, **not** an FK |
| `ActorEmail` | max 320, denormalised |
| `EventType` | max 64, dotted (`auth.login.failed`, `client.updated`) |
| `TargetType`, `TargetId` | what was acted on |
| `ClientId` | nullable, **not** an FK; lets the trail be filtered per tenant |
| `Summary`, `Details` | max 500 / 4000, truncated rather than rejected |
| `IpAddress`, `UserAgent` | max 64 / 512 |

Aged out on its own window (`Retention:AuditRetentionDays`, 2 years), not by a
client's retention setting, and **not protected by legal hold**.

### `mta_sts_state`
The current MTA-STS posture of a domain, one row per domain, maintained by the
worker's check pass (and the staff recheck endpoint). Current state only — no
history table; change *notification* is `alert_event`'s job, via the three
`mta_sts_*` rule types that read these columns.

| Column | Notes |
|---|---|
| `Id` | PK |
| `DomainId` | FK → `domain`, **cascade**, unique — the 1:1 |
| `DnsRecordStatus` | max 16 — `found` \| `missing` \| `lookup_failed` \| `invalid` (2+ STSv1 records or bad syntax: senders treat as no policy). Never `inherited` — MTA-STS has no tree walk |
| `RawRecord` | max 512 — the STSv1 TXT as published |
| `PolicyId`, `PreviousPolicyId` | max 64 each — current `id=` and the one before the last observed change |
| `PolicyIdChangedAtUtc` | when the id last moved (both sides non-null); the alert window |
| `FetchStatus` | max 32 — `ok` \| `redirected` \| `http_error` \| `tls_failed` \| `connect_failed` \| `timeout` \| `too_large` |
| `FetchDetail` | max 1000 — human reason (HTTP status, certificate failure, …) |
| `LastFetchOkAtUtc` | never cleared: tells "broken now" apart from "never reachable yet" (matters once hosted policies exist and a domain is mid-setup) |
| `PolicyValid`, `Mode`, `MaxAgeSeconds`, `PolicyBody` | the last successfully fetched policy; kept when only the fetch fails |
| `MxLookupStatus` | max 16 — `found` \| `missing` \| `lookup_failed` |
| `MxHostsJson`, `UnmatchedMxHostsJson`, `IssuesJson` | JSON blobs: live MX with per-host match verdicts, hosts no pattern covers (`[]` = all covered), and the rendered findings |
| `LastCheckedAtUtc` | indexed — the pass picks least-recently-checked first; always advances |
| `LastChangedAtUtc` | last material change, for "last verified" copy |

Same doctrine as the `domain.Dns*` columns: a failed lookup keeps the last
known values (a SERVFAIL must not make an enforce-mode domain read as
unprotected); only a definitive `missing` clears them. Excluded from the
backup config artifact and history streams — it is a cache the pass rebuilds
within one interval.

### `mta_sts_policy`
A hosted MTA-STS policy: what this instance serves at
`https://mta-sts.{domain}/.well-known/mta-sts.txt` for a domain whose mta-sts
CNAME points here. Inside-out serving config, deliberately separate from
`mta_sts_state` (outside-in monitoring): once the CNAME is live, the check
pass validates a hosted policy exactly like an external one.

| Column | Notes |
|---|---|
| `Id` | PK |
| `DomainId` | FK → `domain`, **cascade**, unique — one hosted policy per domain |
| `Enabled` | serving requires this and `domain.IsActive`; off keeps the settings and answers 404 |
| `Mode` | max 16 — `enforce` \| `testing` \| `none` |
| `MaxAgeSeconds` | validated 3600–31557600 (the RFC cap) |
| `MxPatterns` | max 2000 — newline-joined, normalized lowercase. Empty is legal only for mode `none`, but stored lines survive a mode switch |
| `PolicyId` | max 32 — server-generated `yyyyMMddHHmmss` UTC, bumped **exactly** when the rendered policy content changes (senders only refetch on an id move; a same-second double save formats one second later) |
| `ModeChangedAtUtc` | when `Mode` last changed (set on create) — the testing-clock input for the promotion gate |
| `CreatedAtUtc`, `UpdatedAtUtc` | operator actions; an identical save touches neither |

Included in the backup artifact (`mtaStsPolicies`, ids and `PolicyId`
verbatim so a restore forces no TXT updates); the import attaches policies
through the artifact's own domain list and skips ones whose domain was
skipped.

## A.5.1 What is deliberately *not* a table

- **No pre-aggregated metrics table.** Dashboard figures are computed on demand
  by `AnalyticsQueryService` over `dmarc_report_record`, with one hand-written
  `GROUP BY` for per-source aggregation (EF's grouped navigations produced
  per-group correlated subqueries — 33s vs ~75ms for a domain with 1.3k sources).
  Revisit if data volume outgrows it.
- **No job table.** Background work is the worker polling `report_source` and
  writing `mailbox_sync_run`; there is no generic queue table.
- **No separate checkpoint table** — checkpoints live on `report_source`.

## A.6 Backup

### `backup_stream_state`
Where the offload worker got to, per stream — the configuration snapshot plus
one row per append-only history table it ships (`audit_event`, `alert_event`,
`digest_delivery`, `mailbox_sync_run`, `dmarc_report_ingest`). Has to be in the
database rather than the worker's memory: the periodic passes gate on
in-memory fields, so a restarted worker would otherwise re-ship a history
stream from the beginning of time, and the console needs "when did this last
succeed?" to survive a restart too.

| Column | Notes |
|---|---|
| `Id` | PK |
| `Stream` | max 64 — `config`, or a history table name; **unique** |
| `WatermarkUtc` | nullable — highest row timestamp shipped so far, read back with an overlap window rather than used as an exact cursor; null for `config`, which is a snapshot with nothing to advance through |
| `LastSuccessAtUtc`, `LastAttemptAtUtc` | nullable |
| `LastError` | max 4000, nullable — cleared by the next success, so a lingering value always means "still failing" |
| `UpdatedAtUtc` | |

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
  ← report_source.DefaultClientId
      ← mailbox_sync_run.ReportSourceId
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
  entities (`client`, `domain`, `report_source`) use **restrict** so tenant data
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
