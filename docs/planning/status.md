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
- Console visual redesign (new "ink-green/teal" design system):
  - design tokens as CSS vars + Tailwind theme; self-hosted fonts (Space Grotesk / Public Sans / JetBrains Mono) via Fontsource, no CDN
  - primitives ported from the design handoff (Button/Badge/Card/Input/Select/Dialog/Table/Icon/StatCard/PolicyBadge/ComplianceBar/DaysSelector/TrendChart)
  - new sidebar shell; all six screens rebuilt (Dashboard, Domains, Domain Detail, Clients, Users, Mailbox Sources) + Login
  - Domains/Detail surface published policy (PolicyBadge p=…) and enforcement status (Enforced/Ramping/Spoofing/Monitoring)

- Analytics endpoints over ingested DMARC data:
  - `GET /api/v1/analytics/summary` (compliance totals, daily trend, top failing domains, top reporters, dispositions, mailbox rollup)
  - `GET /api/v1/analytics/domains` (per-domain compliance, DKIM/SPF pass rates, volume, sources, reporters, status classification)
  - relative windows anchored to newest report data (`days` query parameter)
- Dashboard frontpage with compliance overview and URL routing for all console pages.
- Published DMARC policy persistence:
  - parse & store `policy_published` (p, sp, pct, adkim, aspf) per report on `dmarc_report`
  - expose latest-per-domain policy + derived enforcement status (enforced/ramping/spoofing/monitoring/no_data) in domain analytics list and drill-down
  - historical reports default to `p=none` until re-ingested; new ingestion captures real policy

- Per-source drill-down (`/domains/{id}`):
  - domain drilldown/sources/source-detail analytics endpoints
  - per-IP DMARC results with evaluated DKIM×SPF combos, raw auth breakdowns, identifiers, reporters, and per-source trend
  - linkable expanded state via `?source=` query parameter

- Tenant isolation and RBAC:
  - roles: `agency_admin`, `agency_analyst`, `client_viewer` with deny-by-default endpoint enforcement (`RoleAuthorizationMiddleware` + route metadata)
  - per-request `ICurrentUserContext` with client grants (`user_client_grant`) scoping all reads for viewers; cross-tenant ids read as 404
  - admin user management endpoints + Users page; registration locked to first-run bootstrap (`GET /auth/setup`)
  - authN/authZ split: authorization is always in-app, authentication is pluggable

- Optional OIDC login (pluggable authentication):
  - hybrid flow (Microsoft OIDC handler → short-lived cookie → app-minted `dmarc_session`); local password and OIDC are interchangeable front doors
  - `user_identity` mapping with JIT provisioning (verified-email linking; configurable auto-provision + default role)
  - `Auth:Oidc` config, off by default; dev Zitadel in compose + `docs/ops/oidc-zitadel.md`
  - see ADR 0007

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

- Guided path to enforcement:
  - `GET /api/v1/analytics/domains/{id}/enforcement` — server-computed recommendation for the next safe policy step (none → quarantine → reject), rationale, `readyToAdvance`, and the blocking sources still sending unaligned mail
  - Domain Detail "Path to enforcement" panel upgraded with the server guidance banner + blocking-source quick links (expand via `?source=`)

- Threat feed (spoofing investigation):
  - `GET /api/v1/analytics/threats` — tenant-scoped list of (source IP, domain) pairs with fully unauthenticated volume (DKIM and SPF both failed), worst first, with first/last-seen
  - Threats page in the sidebar: reverse-DNS enrichment, policy badges, rows deep-link into the domain drill-down with the source pre-expanded

- Record inspection (published vs observed):
  - `IDnsTxtResolver` (DnsClient against the host's configured resolver — no third-party DoH) with short-lived caching
  - `GET /api/v1/analytics/domains/{id}/records` — live `_dmarc`/SPF TXT records parsed tag-by-tag (multiple-record permerror, missing rua, +all, 10-lookup count) and compared field-by-field against the latest `policy_published` reporters observed
  - Domain Detail "Record inspection" card, fetched separately so slow DNS never blocks the analytics render

- Retention enforcement:
  - `RetentionPurgeService` deletes DMARC data whose **reporting window end**
    (`RangeEndUtc`) predates each client's `RetentionMonths` window (default 27) —
    keyed off the report window rather than ingest date, so a backfill doesn't
    grant old reports a fresh lease
  - `client.LegalHold` exempts a client entirely; a non-positive `RetentionMonths`
    falls back to 27 rather than deleting everything
  - batched deletes; report deletion cascades to records and auth results at the
    database level; the `dmarc_report_ingest` ledger is purged on the same window
  - runs as a daily worker pass (`Worker:RetentionEnabled`,
    `RetentionIntervalHours`, `RetentionBatchSize`)
  - `GET /api/v1/admin/retention/preview` (non-destructive) and
    `POST /api/v1/admin/retention/purge` for operators

- Alerting and email delivery:
  - SMTP relay via MailKit (`Email:*`); delivery degrades to logging when
    unconfigured so a self-hosted install without SMTP still works
  - `notification_recipient` — per-client or agency-wide (null `ClientId`),
    with `alert` / `digest` / `both` kinds
  - `AlertEvaluationService` raises **failure spike** (newest day's compliance vs
    the preceding baseline) and **policy regression** (published policy weakened),
    per active domain
  - thresholds from `Alerts:*` with per-client overrides on `client`
    (`AlertsEnabled`, `AlertComplianceDropPercent`, `AlertMinMessages`)
  - `alert_event` history with a cooldown so the same problem isn't emailed
    repeatedly; alerts are recorded even when no recipient or relay exists
  - hourly worker pass; `GET /api/v1/alerts`,
    `POST /api/v1/admin/alerts/evaluate`,
    `POST /api/v1/admin/notifications/test`, and recipient CRUD

- Monthly digest:
  - `DigestService` composes a per-client summary for the **previous whole
    calendar month**: compliance and its change against the prior month, volume,
    failing sources, domains at enforcement, alerts raised, and the worst domains
  - `digest_delivery` has a unique `(ClientId, PeriodStartUtc)`, which is what
    makes sending idempotent — a restart or extra pass cannot email a month twice.
    A period is recorded even when delivery fails, so a broken relay doesn't
    retry forever
  - worker check pass (`Digest:Enabled`, `DayOfMonth`, `CheckIntervalHours`);
    `GET /api/v1/admin/digest/preview` renders without sending,
    `POST /api/v1/admin/digest/send` sends anything due

- Console pages for notifications:
  - **Alerts** — history with severity, type, per-domain links, whether each was
    emailed, and an admin "Evaluate now" action
  - **Notifications** — recipient management (per-client or agency-wide) and a
    test-send button that surfaces the API's configuration error verbatim

## Planned Next

- Repository/service pattern hardening and broader indexing strategy.
- Alerting, digest delivery, and export workflows.

## Notes

- `docs/planning/backlog.md` is the prioritized task source of truth.
- `docs/planning/roadmap.md` defines milestone sequencing.
- `docs/planning/api-contract.md` and `docs/planning/data-model.md` include both implemented and planned target state.
