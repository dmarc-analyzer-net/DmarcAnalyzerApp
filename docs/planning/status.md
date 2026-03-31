# Implementation Status

Current implementation snapshot for `DmarcAnalyzerApp`.

## Implemented Now

- Repository structure and planning docs baseline under `docs/planning`.
- Single image runtime model for API and worker (`APP_MODE=api|worker`).
- Docker Compose baseline with API, worker, and PostgreSQL.
- ASP.NET Core API with Carter modules and EF Core + PostgreSQL integration.
- Initial schema and migration for core entities:
  - `client`
  - `domain`
  - `mailbox_source`
- API vertical slice endpoints:
  - clients: list/get/create/patch
  - domains: list/get/create/patch
  - mailbox sources: list/create/patch
  - admin migrate endpoint
- Application service layer extraction (modules delegate to services).
- Local API request collection in `http/api.http`.
- Frontend redesign to list-first operations UX:
  - sidebar navigation
  - searchable data tables
  - modal create/edit flows
- Frontend design system foundation:
  - Tailwind setup
  - shadcn-style component primitives
  - reusable UI utility helpers

## Planned Next

- Repository/service pattern hardening and broader indexing strategy.
- Authentication baseline (`agency_user`, session flow).
- Mailbox credential encryption strategy implementation.
- Ingestion jobs and mailbox polling orchestration.
- DMARC RUA parsing integration via `DmarcRua`.
- Dashboard analytics pages for DMARC/SPF/DKIM trends.
- Alerting, digest delivery, and export workflows.

## Notes

- `docs/planning/backlog.md` is the prioritized task source of truth.
- `docs/planning/roadmap.md` defines milestone sequencing.
- `docs/planning/api-contract.md` and `docs/planning/data-model.md` include both implemented and planned target state.
