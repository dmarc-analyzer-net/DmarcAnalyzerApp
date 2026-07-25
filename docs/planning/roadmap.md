# Roadmap

Milestone-based execution plan.

Status markers: `[x]` done · `[~]` in progress / partially delivered · `[ ]` not
started. Milestones overlap in practice — items are sequenced by dependency, not
worked strictly in order.

> For finer granularity use [`status.md`](status.md) (what is implemented right
> now) and [`backlog.md`](backlog.md) (the prioritized task list, and the source
> of truth for what to pick up next).

## Milestone 1 - Foundation and Architecture — **complete**

Target: establish stack, architecture, and development baseline.

- [x] Finalize MVP scope modeled on core dmarcian/EasyDMARC style workflows.
- [x] Initialize solution in `src/` with C# backend and React frontend.
- [x] Set up local development stack and PostgreSQL integration.
- [x] Define agency-first multi-client domain model and initial migration plan.
- [x] Define tenant isolation rules and RBAC boundaries (single database, tenant-keyed model).
- [x] Define globally unique domain ownership rules and conflict handling.
- [x] Document architecture decisions and setup process (ADRs 0001–0007).

## Milestone 2 - Ingestion and Parsing — **near complete**

Target: reliably ingest DMARC RUA reports and normalize parsed data.

- [~] Implement manual upload and mailbox ingestion flows. *(mailbox ingestion
      shipped; manual upload endpoint still open — see backlog "report upload".)*
- [~] Add IMAP and POP3 mailbox readers with MailKit. *(IMAP shipped; POP3 open.)*
- [x] Integrate `DmarcRua` serializer for RUA XML parsing.
- [x] Add ZIP/GZIP attachment extraction and validation.
- [x] Persist parsed entities to PostgreSQL with deduplication and basic validation.
- [x] Add fixture-based tests for parser correctness and ingestion edge cases.
- [x] Implement scheduled polling every 60 minutes (global, 24/7), retries, and sync state tracking.
- [x] Implement unlimited historical mailbox backfill (oldest-to-newest) with resumable checkpoints.
- [x] Add lightweight database-backed queue for safe background job execution.

## Milestone 3 - API and Dashboard Insights — **in progress**

Target: surface actionable insights through API and UI.

- [x] Build tenant-scoped API endpoints for summary metrics, filtered report views, and detail drill-down.
- [x] Implement React dashboards for pass/fail trends and alignment results with daily aggregate views.
- [~] Add date/domain/source filtering and per-report detail pages. *(relative
      window + per-domain + per-source drill-down shipped; per-**report** detail
      pages not built.)*
- [x] Add guided path to enforcement — per-domain recommendation for the next safe
      policy step plus the sources blocking it.
- [x] Add a threat feed of unauthenticated sending sources for spoofing investigation.
- [x] Add record inspection — live DNS DMARC/SPF records compared against the
      policy observed in reports.
- [x] Add user-facing diagnostics for ingestion and parsing failures (mailbox
      health + sync-run history, with parse-failure filters).
- [ ] Add export functionality for CSV and JSON.
- [ ] Add signed magic-link access for single-client read-only dashboard access (7-day expiry).
- [ ] Add branded PDF summary generation and monthly email digest distribution via SMTP.
- [ ] Add alerting for failure spikes and policy regressions with per-client thresholds.

## Milestone 4 - Deployment and Security Hardening — **in progress**

Target: improve reliability and readiness.

- [~] Increase test coverage across ingestion, parsing, persistence, and dashboard
      queries. *(59 tests; analytics query services and the worker loop are
      covered, ingestion end-to-end is not.)*
- [~] Improve performance with indexing, query tuning, and background processing
      strategy. *(core indexes plus a hand-tuned per-source aggregation are in;
      a broader indexing strategy is still open.)*
- [x] Finalize local username/password auth and operational security controls.
- [x] Provide production Docker images and Compose deployment guide — CI publishes
      a multi-arch image to GHCR and Docker Hub; `deploy/compose.yml` plus the
      README quick-start run it without a local build.
- [ ] Provide Kubernetes deployment guidance at equal depth (manifests/Helm + operations notes).
- [x] Finalize runbooks, per-client retention operations (default 27 months), and
      release checklist. *(runbooks and the [release process](../ops/release.md)
      are in `docs/ops`; retention is enforced by a daily purge pass honouring
      per-client windows and legal hold.)*
- [ ] Finalize core audit logging for operational traceability.

## Milestone 5 - Enterprise Integrations (Post-MVP) — **started**

Target: add enterprise identity and mailbox ecosystem support.

- [x] Add optional OIDC SSO integration with external identity providers
      (shipped ahead of the rest of this milestone — see ADR 0007).
- [~] Add read-only client portal access mode for selected client users. *(the
      `client_viewer` role + per-client grants already approximate this; magic
      links and portal polish remain.)*
- [ ] Add Microsoft 365 mailbox/API ingestion connector.
- [ ] Add Google Workspace/Gmail mailbox/API ingestion connector.
