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

## Planned Next

- Repository/service pattern hardening and broader indexing strategy.
- Authentication baseline (`agency_user`, session flow).
- Mailbox credential encryption strategy implementation.
- Attachment extraction hardening for unsupported compression methods.
- Client-facing analytics dashboards over `dmarc_report*` datasets.
- Dashboard analytics pages for DMARC/SPF/DKIM trends.
- Alerting, digest delivery, and export workflows.

## Notes

- `docs/planning/backlog.md` is the prioritized task source of truth.
- `docs/planning/roadmap.md` defines milestone sequencing.
- `docs/planning/api-contract.md` and `docs/planning/data-model.md` include both implemented and planned target state.
