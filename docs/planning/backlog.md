# Backlog

Prioritized list of candidate work.

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
- [ ] (todo) Define and implement global 60-minute polling schedule (24/7) with operational override at deployment level (interval is configurable; production default not yet set).
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
- [ ] (todo) Implement per-client retention rules with default 27 months plus archival/purge jobs and legal-hold support.
- [ ] (todo) Add Kubernetes deployment assets (manifests/Helm), health checks, and stateless service patterns.
- [ ] (todo) Add branded PDF report generation (server-side HTML to PDF) with agency logo/colors/footer.
- [ ] (todo) Add monthly email digest delivery and SMTP relay configuration.
- [ ] (todo) Add alert engine for failure spikes and policy regression with per-client thresholds.
- [ ] (todo) Add core audit logging for login events, config changes, sync runs, and magic-link usage.
- [ ] (todo) Add guided path to enforcement: per-domain policy recommendation engine surfacing the next safe policy step (none -> quarantine -> reject) and the sources still blocking full enforcement (marketing site headline feature; not yet built).
- [ ] (todo) Persist published DMARC policy (`policy_published` from reports) and add a record-inspection view comparing published DMARC/SPF/DKIM records against observed report data (site "Record inspection"; noted as Planned Next in status.md).
- [ ] (todo) Add a threat feed view: dedicated list of unauthenticated/failing sending sources with IP, volume, and first/last-seen for spoofing investigation (site "Threats" / spoofing detection; distinct from the alert engine above).

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
