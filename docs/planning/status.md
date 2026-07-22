# Implementation Status

Current implementation snapshot for `DmarcAnalyzerApp`.

## Implemented Now

- Repository structure and planning docs baseline under `docs/planning`.
- Single image runtime model for API and worker (`APP_MODE=api|worker`).
- Docker Compose baseline with API, worker, and PostgreSQL.
- ASP.NET Core API with Carter modules and EF Core + PostgreSQL integration.
- Core and ingestion/report schema migrations in place for:
  - `client`
  - `domain`
  - `mailbox_source`
- API vertical slice endpoints:
  - clients: list/get/create/patch
  - domains: list/get/create/patch
  - mailbox sources: list/create/patch/sync
  - mailbox health: list
  - mailbox sync runs: list
  - admin migrate endpoint
- Application service layer extraction (modules delegate to services).
- Local API request collection in `http/api.http`.
- Frontend redesign to list-first operations UX:
  - sidebar navigation
  - searchable data tables
  - modal create/edit flows
  - mailbox operations dashboard filters (failed / parse failures / stale success)
- DMARC RUA parsing integration via `DmarcRua` with fixture tests.
- Worker-driven mailbox ingestion orchestration:
  - sequential mailbox processing
  - checkpointed sync (`LastProcessedUid`, `LastProcessedUidValidity`)
  - retry/backoff and run timeout controls
- Sync operational history persisted in `mailbox_sync_run`.
- Domain-resolved report persistence:
  - global unique domain resolution with auto-create when missing
  - full-fidelity DMARC storage in:
    - `dmarc_report`
    - `dmarc_report_record`
    - `dmarc_report_record_dkim_auth_result`
    - `dmarc_report_record_spf_auth_result`
- Frontend design system foundation:
  - Tailwind setup
  - shadcn-style component primitives
  - reusable UI utility helpers

- Analytics endpoints over ingested DMARC data:
  - `GET /api/v1/analytics/summary` (compliance totals, daily trend, top failing domains, top reporters, dispositions, mailbox rollup)
  - `GET /api/v1/analytics/domains` (per-domain compliance, DKIM/SPF pass rates, volume, sources, reporters, status classification)
  - relative windows anchored to newest report data (`days` query parameter)
- Dashboard frontpage with compliance overview and URL routing for all console pages.

- Authentication baseline:
  - `agency_user` and `user_session` entities with EF Core configuration
  - local username/password auth with PBKDF2-SHA256 password hashing
  - HTTP-only secure cookie session (12h idle timeout, 7d absolute max)
  - session auth middleware protecting all `/api/v1/` endpoints
  - auth endpoints: register, login, logout, me
  - CORS credentials support for frontend dev

- Mailbox credential encryption at rest:
  - AES-256-GCM via `Security:CredentialEncryptionKey` (base64, 32 bytes)
  - legacy plaintext rows re-protected lazily on first sync
  - plaintext passthrough with startup warning when no key is configured

## Planned Next

- Repository/service pattern hardening and broader indexing strategy.
- Per-domain drill-down analytics (sources, auth-result detail over time).
- Persist `policy_published` fields from reports to show published DMARC policy per domain.
- Alerting, digest delivery, and export workflows.

## Notes

- `docs/planning/backlog.md` is the prioritized task source of truth.
- `docs/planning/roadmap.md` defines milestone sequencing.
- `docs/planning/api-contract.md` and `docs/planning/data-model.md` include both implemented and planned target state.
