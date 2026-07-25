# Backlog

Prioritized list of candidate work.

## Next Up (recommended sequence)

The MVP is functionally complete: multi-tenant RBAC, pluggable auth (local +
OIDC), worker ingestion, analytics dashboards, and per-source drill-down are
all shipped. The near-term sequence below turns it from "works" into
"operable and client-facing", ordered by value and dependencies.

1. ~~**Surface the audit trail in the console.**~~ (done) `/audit`, admin only —
   day range, event-type, actor and client filters, paged 100 at a time with the
   unpaged total, and a per-row expander for details, target and user agent.
3. **Client access: portal polish + magic links.** The `client_viewer` role
   already approximates a read-only portal; add magic-link (single-client,
   read-only, 7-day) sharing for occasional client access without accounts.

Smaller, independent items to slot in opportunistically: **POP3 ingestion**, the
**report upload/query API endpoints** (which would also make seeding test data
far easier), and **CSV/JSON export**. Larger, deferred until a deployment calls for them: **Helm/K8s
charts**, **branded PDF reports**, and **M365/Google Workspace connectors**.
(The console **visual redesign** is done — shipped as the new ink-green/teal
design system.) See the categorized lists below for the full inventory.

## High Priority

- [x] (done) Define MVP feature set by benchmarking core workflows from dmarcian and EasyDMARC.
- [x] (done) Scaffold solution in `src/` with C# web app backend and React frontend.
- [x] (done) Integrate `DmarcRua` serializer and validate parsing against sample RUA XML fixtures.
- [x] (done) Design PostgreSQL schema for agency, clients, domains, mailbox sources, reports, records, and retention policies.
- [ ] (todo) Add POP3 support to mailbox ingestion (IMAP via MailKit is implemented).
- [x] (done) Implement tenant-aware data access model with strict client isolation for agency operators (client_viewer scoping via per-request user context).
- [x] (done) Implement single-database tenant-keyed architecture (direct or transitive ClientId on all client-scoped entities, enforced in query services).
- [x] (done) Define RBAC with agency_admin/agency_analyst/client_viewer roles (deny-by-default endpoint enforcement; in-app client grants).
- [x] (done) Implement local username/password authentication with secure password hashing and session flow.
- [x] (done) Add secure mailbox credential storage with app-level encryption key management (AES-256-GCM, key via `Security:CredentialEncryptionKey`).
- [x] (done) Add Dockerfiles and Docker Compose stack (api, ui, db, worker) for self-hosted deployment.
- [x] (done) Define and implement global 60-minute polling schedule (24/7) with operational override at deployment level (`Worker:ScheduleIntervalSeconds` defaults to 3600 in `appsettings.json`, overridable per deployment via `Worker__ScheduleIntervalSeconds`; dev uses 15s).
- [x] (done) Implement report deduplication using client + domain + report-id + begin/end date range.
- [x] (done) Enforce globally unique domain ownership across clients.
- [x] (done) Add support for ZIP and GZIP attachment extraction in ingestion pipeline (magic-byte detection; SharpCompress codecs incl. deflate64/bzip2/lzma/zstd).
- [x] (done) Implement unlimited initial mailbox backfill (oldest-to-newest) with durable checkpoints.
- [ ] (todo) Add magic link access model (single-client, read-only, 7-day default expiry).

## Medium Priority

- [ ] (todo) Implement API endpoints for report upload, mailbox sync trigger, and report/query retrieval.
- [x] (done) Add initial EF Core migration and indexes for core entities (clients, domains, mailbox sources).
- [x] (done) Add initial client/domain CRUD baseline endpoints for API vertical slice.
- [x] (done) Add mailbox source CRUD baseline endpoints for API vertical slice.
- [x] (done) Refactor API route handlers to use an application service layer (DTOs + validation in services).
- [x] (done) Build admin operations UI for clients/domains/mailbox sources with list-first tables and modal create/edit.
- [ ] (todo) Add migrations, repository layer, and indexing strategy for PostgreSQL.
- [x] (done) Build React dashboards for pass/fail, SPF/DKIM alignment, and disposition (source IP trends pending drill-down below).
- [x] (done) Add per-source drill-down with daily aggregates (domain detail page with per-IP DMARC results and raw auth breakdown).
- [x] (done) Add scheduled polling orchestration with retries and sync audit history (worker-driven, `mailbox_sync_run`).
- [x] (done) Implement per-client retention rules with default 27 months plus purge job and legal-hold support (`RetentionPurgeService`, daily worker pass, `client.LegalHold`, admin preview/purge endpoints). Archival-before-delete was not implemented — purging is outright deletion.
- [x] (done) Publish a versioned container image (GHCR) via CI and add a README quick-start (`.github/workflows/ci.yml` builds/tests then pushes `ghcr.io/dmarc-analyzer-net/dmarc-analyzer` for amd64+arm64; `deploy/compose.yml` + README "Quick Start" run it without a local build).
- [x] (done) Redesign the console UI — new "ink-green/teal" design system (tokens + self-hosted fonts), ported primitives, new sidebar shell, all six screens + login rebuilt; Domains/Detail surface published policy + enforcement status.
- [ ] (todo) Add Kubernetes deployment assets — Helm chart(s) with health checks and stateless service patterns, supporting both self-contained (bundled PostgreSQL, local auth) and bring-your-own deployments (external managed PostgreSQL, external OIDC), toggled via chart values.
- [ ] (todo) Add branded PDF report generation (server-side HTML to PDF) with agency logo/colors/footer.
- [x] (done) Add monthly email digest delivery and SMTP relay configuration (`DigestService`, previous-whole-month period, `digest_delivery` for idempotency, worker check pass, admin preview/send endpoints).
- [x] (done) Add alert engine for failure spikes and policy regression with per-client thresholds (`AlertEvaluationService`, hourly worker pass, `alert_event` history with cooldown, per-client overrides on `client`, email notification, `GET /alerts` + admin evaluate endpoint).
- [x] (done) Add core audit logging for login events, config changes, and manual sync triggers (`audit_event`, `IAuditLog`, admin query endpoint, `/audit` console page, 2-year retention). Scheduled sync runs are covered by `mailbox_sync_run` rather than duplicated; magic-link events will be added with magic links.
- [ ] (todo) Instrument the API and worker with OpenTelemetry — traces, metrics and logs to a collector — delivering the OTEL-ready pipeline ADR 0006 already accepts. Motivating case: an `/enforcement` request measured 7.7s wall clock against 70ms of process CPU and ~1s of logged SQL; finding the missing ~6.6s took hand-diffing container logs and running `EXPLAIN ANALYZE`. Per-request spans around EF/Npgsql commands, DNS resolution and handler time would have shown it immediately. Worth pairing with a slow-request log threshold, since EF's `Executed DbCommand` duration excludes row streaming and so hides this whole class of problem.
- [ ] (todo) Add analytics indexes matching the actual query shapes: `dmarc_report (DomainId, RangeBeginUtc)` and `(RangeEndUtc)`. The window anchor (`MAX(RangeEndUtc)` across all reports) and every window filter currently fall back to sequential scans — the domain-detail aggregation reads all 15.6k reports for a domain and discards 14.8k of them by date.
- [ ] (todo) Fix horizontal overflow in the two widest tables. Domains needs 1110px inside a 1038px container (72px over); Domain Detail's sending-sources table needs 1425px (387px over), because the Source IP column alone takes 419px to fit an untruncated reverse-DNS hostname beneath the IP. Both scroll inside their own container rather than the page, so nothing is unreachable, but the compliance value clips mid-number. Options: truncate long hostnames with a `title`, narrow the compliance meter, or move low-value columns into the expanded row.
- [x] (done) Add guided path to enforcement: per-domain policy recommendation engine surfacing the next safe policy step (none -> quarantine -> reject) and the sources still blocking full enforcement (`/enforcement` endpoint + Domain Detail panel).
- [x] (done) Persist published DMARC policy (`policy_published` from reports) and add a record-inspection view comparing published DMARC/SPF records (live DNS via host resolver) against observed report data (`/records` endpoint + Domain Detail card).
- [x] (done) Add a threat feed view: dedicated list of unauthenticated/failing sending sources with IP, volume, and first/last-seen for spoofing investigation (`/threats` endpoint + Threats page in sidebar).

## Low Priority

- [ ] (todo) Add export options for analytics (CSV and JSON).
- [ ] (todo) Add onboarding and deployment docs for local Docker-based development.
- [x] (done) Add optional OIDC support for external identity providers (hybrid handler + JIT provisioning; Zitadel tested, any OIDC provider via config).
- [ ] (todo) Add read-only client portal mode for selected clients.
- [ ] (todo) Add mailbox connectors for Microsoft 365 and Google Workspace APIs.
- [ ] (todo) Add forensic/failure (RUF) report ingestion and parsing (MVP is scoped to aggregate/RUA only; marketing site advertises "aggregate and forensic").

## Parking Lot

- [ ] (todo) Investigate DNS and WHOIS enrichment for sending infrastructure insights.
- [ ] (todo) Add sending-source enrichment: map sending IPs/hostnames to known ESPs/services (beyond reverse DNS, which exists in HostnameResolver) and add IP geolocation for threat context (site claims sources "resolved to a recognisable service" and shown with "geography").
- [ ] (todo) Evaluate anomaly detection for sudden DMARC/SPF/DKIM failure spikes.
- [ ] (todo) Evaluate optional BIMI and TLS-RPT support after DMARC MVP.
