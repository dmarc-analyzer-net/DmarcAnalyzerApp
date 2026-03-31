# Roadmap

Milestone-based execution plan.

## Milestone 1 - Foundation and Architecture

Target: establish stack, architecture, and development baseline.

- Finalize MVP scope modeled on core dmarcian/EasyDMARC style workflows.
- Initialize solution in `src/` with C# backend and React frontend.
- Set up local development stack and PostgreSQL integration.
- Define agency-first multi-client domain model and initial migration plan.
- Define tenant isolation rules and RBAC boundaries (single database, tenant-keyed model).
- Define globally unique domain ownership rules and conflict handling.
- Document architecture decisions and setup process.

## Milestone 2 - Ingestion and Parsing

Target: reliably ingest DMARC RUA reports and normalize parsed data.

- Implement manual upload and mailbox ingestion flows.
- Add IMAP and POP3 mailbox readers with MailKit.
- Integrate `DmarcRua` serializer for RUA XML parsing.
- Add ZIP/GZIP attachment extraction and validation.
- Persist parsed entities to PostgreSQL with deduplication and basic validation.
- Add fixture-based tests for parser correctness and ingestion edge cases.
- Implement scheduled polling every 60 minutes (global, 24/7), retries, and sync state tracking.
- Implement unlimited historical mailbox backfill (oldest-to-newest) with resumable checkpoints.
- Add lightweight database-backed queue for safe background job execution.

## Milestone 3 - API and Dashboard Insights

Target: surface actionable insights through API and UI.

- Build tenant-scoped API endpoints for summary metrics, filtered report views, and detail drill-down.
- Implement React dashboards for pass/fail trends and alignment results with daily aggregate views.
- Add date/domain/source filtering and per-report detail pages.
- Add export functionality for CSV and JSON.
- Add user-facing diagnostics for ingestion and parsing failures.
- Add signed magic-link access for single-client read-only dashboard access (7-day expiry).
- Add branded PDF summary generation and monthly email digest distribution via SMTP.
- Add alerting for failure spikes and policy regressions with per-client thresholds.

## Milestone 4 - Deployment and Security Hardening

Target: improve reliability and readiness.

- Increase test coverage across ingestion, parsing, persistence, and dashboard queries.
- Improve performance with indexing, query tuning, and background processing strategy.
- Finalize local username/password auth and operational security controls.
- Provide production Docker images and Compose deployment guide.
- Provide Kubernetes deployment guidance at equal depth (manifests/Helm + operations notes).
- Finalize runbooks, per-client retention operations (default 27 months), and release checklist.
- Finalize core audit logging for operational traceability.

## Milestone 5 - Enterprise Integrations (Post-MVP)

Target: add enterprise identity and mailbox ecosystem support.

- Add optional OIDC SSO integration with external identity providers.
- Add read-only client portal access mode for selected client users.
- Add Microsoft 365 mailbox/API ingestion connector.
- Add Google Workspace/Gmail mailbox/API ingestion connector.
