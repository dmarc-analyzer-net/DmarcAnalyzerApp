# Architecture

Technical architecture for `DmarcAnalyzerApp` MVP and near-term post-MVP evolution.

## 1) Goals and Scope

- Build an agency-first DMARC analyzer similar to dmarcian/EasyDMARC workflows.
- Support multiple agency clients (tenants) with strict data isolation in a single PostgreSQL database.
- Ingest DMARC RUA reports from IMAP/POP3 mailboxes using MailKit.
- Parse DMARC XML using `DmarcRua`.
- Provide dashboards, branded PDF summaries, email digests, and actionable alerts.
- Run as Docker image(s), with equal deployment guidance for Docker Compose and Kubernetes.

## 2) Confirmed Product Decisions

- Backend framework: ASP.NET Core + Carter modules (organized by domain feature).
- Frontend: React + TypeScript + Vite.
- Data store: PostgreSQL (EF Core + Npgsql).
- Scheduling: global polling interval, every 60 minutes, 24/7.
- Background execution: lightweight DB-backed queue + hosted worker mode.
- Kubernetes execution: CronJob-triggered worker runs.
- Compose execution: single image dual mode (API mode and worker mode by env/config).
- Mailbox support (MVP): IMAP + POP3, ZIP + GZIP report attachments.
- Backfill: unlimited mailbox history, oldest-to-newest with checkpointing.
- Authentication (agency users): local username/password, HTTP-only cookie session.
- Session defaults: 12 hours idle, 7 days absolute max.
- Magic links (client view): signed JWT/HMAC + DB nonce, reusable until expiry, 7-day default, single-client read-only scope.
- Retention: configurable per client, default 27 months, purge by report end date.
- Domain policy: globally unique domain ownership.
- Routing default: by policy domain map; source has default client fallback for unmatched domains.
- Alerts: failure spike + policy regression; delivery by SMTP; both global recipients and per-client recipients.
- Branding: per agency (logo, colors, report footer).
- Exports: async CSV/JSON with size cap.
- Observability: structured logs + health/readiness; telemetry-ready for OTEL collector (logs/metrics/spans push model).

## 3) High-Level Components

### Runtime modes (one image, `APP_MODE`)

A single container image runs either half of the system, or both, selected by the
`APP_MODE` environment variable (default `api`):

- `APP_MODE=api` — the ASP.NET web host: HTTP API plus the built React app served
  from `wwwroot`. No background loop.
- `APP_MODE=worker` — a plain `Host` with no HTTP listener, running
  `QueueWorkerService` only.
- `APP_MODE=all` — the web host with `QueueWorkerService` registered as a hosted
  service. One process, one log stream. See ADR 0008; this is the intended shape
  for a single-host deployment.
- `APP_MODE=migrate` — applies pending migrations and exits. The smallest host
  that can do it: a DbContext and the audit trail, nothing that serves or
  ingests. It exists because neither other migration path can run *before* an
  application pod — startup migration races across replicas, and the admin
  endpoint needs the very instance being waited for. The Kubernetes chart runs it
  as a pre-install/pre-upgrade Job. Re-running with nothing pending is a logged
  no-op, so an unchanged upgrade does not need a human to interpret it.

`Program.cs` branches on this before building anything, so worker mode never
constructs the web pipeline. Combined mode costs one `AddHostedService` call
because the web host already registers every service the loop needs — sync,
alerts, digest, retention, the DNS cache and `WorkerOptions` are all there for
the endpoints that share them.

**An unrecognised `APP_MODE` fails startup** rather than defaulting to `api`
(`AppRuntimeMode.Parse`). A typo that silently produced an API-only container
would be close to undiagnosable: the container is up, the console loads, the
healthcheck passes, and the only symptom is reports not arriving.

Two consequences worth knowing:

- **Worker mode does not apply EF migrations** (`Database:MigrateOnStartup` is
  read only by the web host), so it must not be started against an un-migrated
  database — the shipped compose files gate it on the API reporting healthy.
  Combined mode migrates before the host starts, so the loop never sees an
  un-migrated schema.
- **Identity in non-request scopes.** `ICurrentUserContext` resolves to
  `SystemUserContext` when there is no `HttpContext` and the HTTP-backed
  `CurrentUserContext` when there is. That keeps the startup-migration audit
  entry and every combined-mode worker pass on the same system identity that
  worker mode uses, instead of an unauthenticated request context that nothing
  populated.

### API Service (C# ASP.NET + Carter)

Carter modules as implemented (`src/api/Modules`):

| Module | Surface |
|---|---|
| `AuthModule` | login/logout/register/me/setup/providers |
| `OidcAuthModule` | OIDC challenge + callback (see ADR 0007) |
| `ClientsModule`, `DomainsModule`, `UsersModule` | tenant CRUD + viewer grants |
| `ReportSourcesModule` | mailbox CRUD + manual sync trigger |
| `MailboxHealthModule`, `MailboxSyncRunsModule` | ingestion operations views |
| `AnalyticsModule` | all report analytics + enforcement, threats, record inspection |
| `SystemModule` | status |
| `DatabaseModule` | admin migrate |

Planned modules, not yet present: alerts, exports, magic links, PDF reports, and
a report-upload/query surface. See [`api-contract.md`](api-contract.md) §0 for the
authoritative endpoint list.

- Responsibilities:
  - CRUD for clients, domains, report sources, and users/grants.
  - Analytics query APIs over ingested report data.
  - Trigger and inspect ingestion/sync runs.

### Analytics query layer

- `AnalyticsQueryService` answers every dashboard/drill-down request **on demand**
  from `dmarc_report_record`; there is no pre-aggregated metrics table.
- `RecordInspectionService` + `IDnsTxtResolver` do live DNS lookups (via the
  host's configured resolver, deliberately not a third-party DoH endpoint) and
  compare published records against the policy observed in reports.
- Two constraints shape the code here:
  - **Tenancy** — every query is scoped through `ICurrentUserContext`; viewers are
    filtered by client grants and cross-tenant ids return 404.
  - **Query shape** — navigations are flattened before `GroupBy`, because EF turns
    grouped navigation aggregates into per-group correlated subqueries (33s vs
    ~75ms for a domain with 1.3k sources). One per-source aggregation is
    hand-written SQL for the same reason.

### Worker Mode (same image, hosted service enabled)

- Poll scheduler processes report sources sequentially (one source at a time per worker pass).
- Implemented per-pass work:
  - Auto-close stale `running` sync runs.
  - Mailbox sync: fetch, extract attachments, parse, dedup, persist.
- Planned processors (not built): alert evaluation, digest generation, retention
  purge, export generation. Aggregate refresh is not planned — metrics are
  computed on demand (see the analytics layer above).
- Uses persisted sync run history and checkpoints for safe retry and operational visibility.
- The pass loop catches and logs its own failures, backing off from 5s up to
  `Worker:ScheduleIntervalSeconds` so a transient database outage doesn't stop
  ingestion.

### Frontend (React + Vite + Tailwind + shadcn-style components)

- Agency UI:
  - Client/domain/mailbox configuration
  - List-first operations tables with modal create/edit flows
  - Dashboard and trend exploration
  - Sync status and diagnostics
  - Alert and digest management
- Optional client-facing view:
  - Read-only pages opened via magic link.

### PostgreSQL

- OLTP store for config + raw/normalized report data.
- Job queue and checkpoints.
- Audit logs and notification state.
- Export task metadata and artifact pointers.

## 4) Deployment Topology

### Docker Compose (equal priority)

- Services:
  - `app-api` (web mode)
  - `app-worker` (worker mode; same image)
  - `postgres`
- Optional:
  - reverse proxy
  - otel collector sidecar/service
- Configuration via environment variables and Docker secrets.

### Kubernetes (equal priority)

- `Deployment`: API pod(s)
- `CronJob`: periodic worker runs for polling/reconciliation
- `Job`: migration/init container for EF migrations
- `Service` + `Ingress` for API/UI
- `Secret`/`ConfigMap` for credentials and config
- Optional OTEL collector integration.

## 5) Data Model (Tenant-Keyed, Single DB)

This section was written before the build, and the entity list it originally
carried was a sketch rather than a schema. Eight of the twenty entities it named
were never created, four more arrived under different names, and it anticipated
none of the twelve auth, TLS-RPT/MTA-STS and config-export tables that exist
today. The list has been removed rather than re-synced: keeping a second copy of
the schema here is what let it drift in the first place.

**The implemented data model is [`data-model.md`](data-model.md)** — entities,
columns, keys, tenancy paths and retention, tracked against the code. What
follows is only the shape the design settled on, and the places the build
departed from it.

### Tenancy

Not every client-scoped table carries a client id — the four report tables and
`mailbox_sync_run` do not. `domain.ClientId` and `report_source.DefaultClientId`
are the direct keys; reports and records derive tenancy transitively through
`dmarc_report.DomainId → domain.ClientId`. Domain names are globally unique, and
that is *why* the uniqueness is load-bearing rather than merely tidy: it is what
makes the derivation unambiguous.

Isolation is enforced explicitly in application code, not by the ORM — there is
no `HasQueryFilter` anywhere in the codebase. This is the single most important
invariant in the system, and nothing in the data layer will catch a query that
forgets it.

### Deduplication

Two unique keys do the work, at different levels:

- `dmarc_report` on `(DomainId, ReportId, RangeBeginUtc, RangeEndUtc)` — the
  report envelope itself.
- `dmarc_report_ingest` on `(ClientId, PolicyDomain, ReportId,
  ReportRangeBeginUtc, ReportRangeEndUtc)` — the ledger, which is what a repeated
  mailbox pass hits first.

Partitioning by report period is still available as a later step and has not been
introduced.

### Where the build departed from this sketch

| Sketched | What exists |
|---|---|
| `mailbox_source_client` | never built — a source has one `DefaultClientId`, and routing is by policy domain |
| `sync_run` | `mailbox_sync_run` |
| `sync_checkpoint` | never built — checkpoint columns live on `report_source` |
| `raw_report` | never built — raw report mail is archived outside the database, before parsing and independently of whether it parses |
| `dmarc_record` | `dmarc_report_record` |
| `dmarc_auth_result` | split in two: `dmarc_report_record_dkim_auth_result` and `dmarc_report_record_spf_auth_result` |
| `alert_rule` | never built — thresholds are columns on `client` (`AlertsEnabled`, `AlertComplianceDropPercent`, `AlertMinMessages`) |
| `alert_recipient` | `notification_recipient`, unique on `(ClientId, Email)` |
| `digest_schedule` | never built — `digest_delivery` is unique on `(ClientId, PeriodStartUtc)` |
| `export_job` | never built |
| `magic_link_nonce` | never built — magic links are still on the backlog, and no token or bearer path exists |
| `retention_policy` | never built — retention is `client.RetentionMonths`, with `LegalHold` |

Arriving since, and unanticipated by the sketch: `user_session`, `user_identity`
and `user_client_grant` for auth; `smtp_tls_report`, `smtp_tls_report_policy`,
`smtp_tls_failure_detail`, `tls_report_ingest`, `mta_sts_state` and
`mta_sts_policy` for TLS-RPT and MTA-STS; and `backup_stream_state` for config
export.

## 6) Ingestion and Routing Flow

1. Scheduler creates mailbox sync jobs.
2. Worker acquires job lock and opens source mailbox (IMAP/POP3 via MailKit).
3. Fetch candidate messages (checkpoint-aware).
4. Extract DMARC attachments (`.zip`, `.gz`, raw xml if present).
5. Parse XML with `DmarcRua`.
6. Resolve target domain:
   - Match by globally unique `domain.name`.
   - If not found, auto-create domain under source default client.
   - If found under different client ownership, reuse existing domain.
7. Dedup normalized report by `(domain, report-id, date-range)`.
8. Persist full-fidelity normalized entities:
   - `dmarc_report`
   - `dmarc_report_record`
   - `dmarc_report_record_dkim_auth_result`
   - `dmarc_report_record_spf_auth_result`
9. Commit checkpoint and mark run result.
10. Emit audit events and alert triggers.

### Current Operational Endpoints

- `GET /api/v1/mailbox-health`
- `GET /api/v1/mailbox-sync-runs`
- `POST /api/v1/report-sources/{id}/sync` (manual operator trigger)

### Current Worker Controls

Configured via `Worker__*` settings:

- `ScheduleIntervalSeconds`
- `MaxMessagesPerSync`
- `MaxRetryAttempts`
- `RetryBaseDelaySeconds`
- `StaleRunTimeoutMinutes`
- `SyncRunTimeoutMinutes`

### Known Gap

- Archive extraction compatibility still needs hardening for certain unsupported ZIP compression methods.

## 7) Queue, Scheduling, and Retry Model

- DB-backed jobs table:
  - `job_type`, `payload`, `status`, `attempt_count`, `next_attempt_at`, `locked_by`, `locked_until`.
- Retry policy:
  - exponential backoff
  - max attempts by job type
  - dead-letter terminal status for operator review.
- Idempotency:
  - dedup key for report ingest.
  - checkpoint monotonic progression (oldest-to-newest backfill).
- Cron in K8s triggers worker execution; worker drains due jobs safely.

## 8) Security Model

### Applying Migrations

Three paths apply migrations, and all three allow the same 10-minute command
timeout because a pending migration may carry a multi-minute backfill —
`AddDmarcReportRecordRangeBegin` rewrites every `dmarc_report_record` row and
takes over two minutes at ~5M rows. Npgsql's default is 30 seconds, which is not
enough:

| Path | Where the timeout is set |
|---|---|
| Startup, `Database:MigrateOnStartup` | `Program.cs` |
| `POST /api/v1/admin/database/migrate` | `DatabaseModule` |
| `dotnet ef database update` | `DmarcAnalyzerDbContextFactory` |

A migration that times out rolls back cleanly — EF wraps each one in a
transaction — so the failure mode is "nothing applied", not a half-migrated
schema. It is still a failure, and one that only shows up against production
data volumes.

### Client Addresses Behind a Proxy

The audit trail records `Connection.RemoteIpAddress`. Behind a reverse proxy
that is the proxy, not the caller — on the default Compose stack every entry
reads as Docker's bridge gateway.

`Network:UseForwardedHeaders` turns on `X-Forwarded-For` / `X-Forwarded-Proto`
handling, but **only from hops named in `Network:TrustedProxies` (addresses) or
`Network:TrustedNetworks` (CIDR)**. Enabling it with neither is refused and
logged as an error rather than applied: an empty trust list means any caller can
set the address recorded against its own audit entries, which is worse than
recording the gateway honestly. `Network:ForwardLimit` (default 1) bounds how
many hops are walked back.

Off by default, so an install that has not thought about its proxy keeps the
current behaviour.

```jsonc
"Network": {
  "UseForwardedHeaders": true,
  "TrustedNetworks": ["172.16.0.0/12"],   // the Docker bridge, say
  "ForwardLimit": 1
}
```

### Agency Authentication

- **Authorization is always in-app; authentication is pluggable** (ADR 0007).
  Local username/password and OIDC are interchangeable front doors that both mint
  the same app `dmarc_session` cookie.
- Local credentials with PBKDF2-SHA256 password hashing.
- Optional OIDC (off by default): external handler → short-lived cookie →
  app-minted session, with `user_identity` mapping and JIT provisioning.
- Roles `agency_admin` / `agency_analyst` / `client_viewer`, enforced
  deny-by-default by `RoleAuthorizationMiddleware` + route metadata.
- Cookie auth:
  - `HttpOnly`, `Secure`, `SameSite=Lax/Strict` depending on frontend hosting pattern.
- Session controls:
  - idle timeout 12h
  - absolute max 7d

### Client Read-Only Access

> **Not implemented.** The `client_viewer` role plus `user_client_grant` already
> provides account-based read-only access; magic links remain planned.

- Magic link token:
  - signed JWT/HMAC with nonce reference.
  - scoped to one client and read-only routes.
  - reusable until expiry (default 7d).
- Revocation:
  - nonce invalidation in DB.
- Audit:
  - token generation, access, expiry, revocation events.

### Secrets

- Mailbox credentials encrypted at rest with app-level key from environment/secret store.
- Rotation strategy documented for encryption key and SMTP credentials.

## 9) Reporting and Notifications

### Dashboard Metrics

Computed on demand per request (no daily aggregate table), over a relative window
anchored to the newest report rather than wall-clock time:

- DMARC pass/fail trend
- SPF/DKIM alignment trend
- disposition breakdown
- source IP top senders/failures

### PDF and Digest

> **Not implemented** — planned; Playwright Chromium is already a dependency.

- Branded PDF generated server-side via Playwright Chromium.
- Monthly digest job composes summary and sends via SMTP.
- Sender identity is deployment-level configured.

### Alerting

> **Not implemented** — planned. Problems currently surface reactively through the
> dashboards, the threat feed, and mailbox health.

- Alert types:
  - failure spikes
  - policy regression
- Thresholds:
  - per-client overrides + global defaults.
- Delivery:
  - SMTP email to global and per-client recipient lists.

## 10) Observability and Operations

- Structured JSON logs (include correlation IDs and client/domain/job context).
- Health endpoints:
  - liveness
  - readiness
- OTEL-ready exporter configuration:
  - logs, metrics, spans pushed to collector backend.
- Operational views:
  - sync run history
  - job failures
  - dead-letter inspections
  - export job status

## 11) Retention and Data Lifecycle

- Default retention: 27 months, configurable per client.
- Purge basis: report end date.
- Scheduled retention job:
  - soft-delete/archival hooks (optional)
  - hard-delete beyond retention if no legal hold.
- Audit record for purge actions.

## 12) Non-Goals for MVP

- External IdP/OIDC login (post-MVP).
- Microsoft 365 / Google Workspace API connectors (post-MVP).
- Formal compliance certification workflows (SOC2/GDPR controls hardening deferred, while keeping good operational hygiene).

## 13) Open Questions Before Build Start

- Should source default-client auto-domain-create require domain allowlist pattern (to avoid accidental ownership of unrelated domains)?
- Should unmatched reports notify agency admins immediately, even when auto-assigned?
- What max export size cap should MVP enforce (rows and file size)?
- What are target API response times for dashboard endpoints at 200-client scale?
- Should audit log retention differ from report retention?
