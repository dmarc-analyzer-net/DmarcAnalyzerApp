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

### API Service (C# ASP.NET + Carter)

- Domain feature Carter modules:
  - `AuthModule`
  - `ClientsModule`
  - `DomainsModule`
  - `MailboxSourcesModule`
  - `IngestionModule`
  - `ReportsModule`
  - `DashboardsModule`
  - `AlertsModule`
  - `ExportsModule`
  - `MagicLinksModule`
  - `AdminModule`
- Responsibilities:
  - CRUD for clients, domains, mailbox sources, recipients, thresholds.
  - Report and dashboard query APIs.
  - Trigger and inspect ingestion/sync runs.
  - Manage exports, alerts, and report artifacts.
  - Serve magic-link read-only endpoints.

### Worker Mode (same image, hosted service enabled)

- Poll scheduler processes mailbox sources sequentially (one source at a time per worker pass).
- Job processors:
  - Mailbox sync
  - Attachment extraction/parsing
  - Dedup + persistence
  - Aggregate refresh
  - Alert evaluation
  - Digest generation
  - Retention purge
  - Export generation
- Uses persisted sync run history and checkpoints for safe retry and operational visibility.

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

All client-scoped entities include `client_id`.

Core entities:

- `agency_user`
- `client`
- `domain` (globally unique)
- `mailbox_source` (IMAP/POP3 settings, default client)
- `mailbox_source_client` (if one source explicitly services multiple clients)
- `sync_run`
- `sync_checkpoint`
- `raw_report` (raw XML payload hash + metadata)
- `dmarc_report` (normalized report envelope)
- `dmarc_record` (row-level aggregate records)
- `dmarc_auth_result` (spf/dkim outcomes)
- `alert_rule`
- `alert_recipient`
- `alert_event`
- `digest_schedule`
- `digest_delivery`
- `export_job`
- `magic_link_nonce`
- `audit_event`
- `retention_policy`

Constraints and indexes:

- Unique `domain.name` globally.
- Dedup unique key for normalized reports: `(domain_id, report_id, begin_utc, end_utc)`.
- Indexed query paths:
  - `(client_id, domain_id, report_date)`
  - `(client_id, source_ip, report_date)`
  - `(client_id, disposition, report_date)`
- Partitioning can be introduced later by report period if needed.

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
- `POST /api/v1/mailbox-sources/{id}/sync` (manual operator trigger)

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

### Agency Authentication

- Local credentials with strong password hashing.
- Cookie auth:
  - `HttpOnly`, `Secure`, `SameSite=Lax/Strict` depending on frontend hosting pattern.
- Session controls:
  - idle timeout 12h
  - absolute max 7d

### Client Read-Only Access

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

### Dashboard Metrics (daily aggregates)

- DMARC pass/fail trend
- SPF/DKIM alignment trend
- disposition breakdown
- source IP top senders/failures

### PDF and Digest

- Branded PDF generated server-side via Playwright Chromium.
- Monthly digest job composes summary and sends via SMTP.
- Sender identity is deployment-level configured.

### Alerting

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
