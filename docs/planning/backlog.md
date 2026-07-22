# Backlog

Prioritized list of candidate work.

## High Priority

- [x] (done) Define MVP feature set by benchmarking core workflows from dmarcian and EasyDMARC.
- [x] (done) Scaffold solution in `src/` with C# web app backend and React frontend.
- [ ] (todo) Integrate `DmarcRua` serializer and validate parsing against sample RUA XML fixtures.
- [x] (done) Design PostgreSQL schema for agency, clients, domains, mailbox sources, reports, records, and retention policies.
- [ ] (todo) Implement mailbox ingestion service supporting both IMAP and POP3 via MailKit.
- [ ] (todo) Implement tenant-aware data access model with strict client isolation for agency operators.
- [ ] (todo) Implement single-database tenant-keyed architecture (tenant_id on all client-scoped entities).
- [ ] (todo) Define RBAC with agency-admin/agency-analyst roles and future client-viewer role.
- [x] (done) Implement local username/password authentication with secure password hashing and session flow.
- [ ] (todo) Add secure mailbox credential storage with app-level encryption key management.
- [ ] (todo) Add Dockerfiles and Docker Compose stack (api, ui, db, worker) for self-hosted deployment.
- [ ] (todo) Define and implement global 60-minute polling schedule (24/7) with operational override at deployment level.
- [ ] (todo) Implement report deduplication using client + domain + report-id + begin/end date range.
- [ ] (todo) Enforce globally unique domain ownership across clients.
- [ ] (todo) Add support for ZIP and GZIP attachment extraction in ingestion pipeline.
- [ ] (todo) Implement unlimited initial mailbox backfill (oldest-to-newest) with durable checkpoints.
- [ ] (todo) Add magic link access model (single-client, read-only, 7-day default expiry).

## Medium Priority

- [ ] (todo) Implement API endpoints for report upload, mailbox sync trigger, and report/query retrieval.
- [x] (done) Add initial EF Core migration and indexes for core entities (clients, domains, mailbox sources).
- [x] (done) Add initial client/domain CRUD baseline endpoints for API vertical slice.
- [x] (done) Add mailbox source CRUD baseline endpoints for API vertical slice.
- [x] (done) Refactor API route handlers to use an application service layer (DTOs + validation in services).
- [x] (done) Build admin operations UI for clients/domains/mailbox sources with list-first tables and modal create/edit.
- [ ] (todo) Add migrations, repository layer, and indexing strategy for PostgreSQL.
- [ ] (todo) Build React dashboards for pass/fail, SPF/DKIM alignment, disposition, and source IP trends.
- [ ] (todo) Add domain-level filtering, date range filtering, and per-source drill-down with daily aggregates.
- [ ] (todo) Add scheduled polling orchestration with DB-backed job queue, retries, and sync audit history.
- [ ] (todo) Implement per-client retention rules with default 27 months plus archival/purge jobs and legal-hold support.
- [ ] (todo) Add Kubernetes deployment assets (manifests/Helm), health checks, and stateless service patterns.
- [ ] (todo) Add branded PDF report generation (server-side HTML to PDF) with agency logo/colors/footer.
- [ ] (todo) Add monthly email digest delivery and SMTP relay configuration.
- [ ] (todo) Add alert engine for failure spikes and policy regression with per-client thresholds.
- [ ] (todo) Add core audit logging for login events, config changes, sync runs, and magic-link usage.

## Low Priority

- [ ] (todo) Add export options for analytics (CSV and JSON).
- [ ] (todo) Add onboarding and deployment docs for local Docker-based development.
- [ ] (todo) Add optional OIDC support for external identity providers (Azure AD, Okta, Keycloak, Auth0).
- [ ] (todo) Add read-only client portal mode for selected clients.
- [ ] (todo) Add mailbox connectors for Microsoft 365 and Google Workspace APIs.

## Parking Lot

- [ ] (todo) Investigate DNS and WHOIS enrichment for sending infrastructure insights.
- [ ] (todo) Evaluate anomaly detection for sudden DMARC/SPF/DKIM failure spikes.
- [ ] (todo) Evaluate optional BIMI and TLS-RPT support after DMARC MVP.
