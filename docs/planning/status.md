# Implementation Status

Current implementation snapshot for `DmarcAnalyzerApp`.

## Implemented Now

- Repository structure and planning docs baseline under `docs/planning`.
- Single image runtime model for API and worker (`APP_MODE=api|worker`).
- **Deployment**: one image, five `APP_MODE` values (`api`, `worker`, `all`,
  `migrate`, `mta-sts` — the last a dedicated internet-facing MTA-STS policy
  host serving only the public routes and health probes). Docker Compose ships as a single combined container plus PostgreSQL,
  with overlays for an external database and for splitting console from worker.
  A Helm chart (`deploy/helm/dmarc-analyzer`) exposes the same two axes, applies
  migrations via a pre-install Job, and is published to
  `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer` on a release tag.
  Exactly one ingestion worker may run per database, enforced by a Postgres
  advisory lock. A Render Blueprint (`render.yaml`, repo root) is a third deploy
  path — provisions the app plus a managed Postgres and wires them together via
  `DATABASE_URL`, which the app accepts as a `postgres://` URI alongside the
  existing ADO.NET `ConnectionStrings__Default`.
- **Backup, offload and recovery** (ADR 0009, design detail in
  `config-export-and-recovery.md`). A JSON configuration artifact — clients, domains,
  mailbox sources with their encrypted credentials, recipients, users, identities,
  grants — is the primary backup; `pg_dump` is demoted to the pre-upgrade artifact a
  rollback across a migration needs. Report data is deliberately excluded, because the
  mailbox is the archive and re-ingestion is idempotent.
  - Manual export (`GET /api/v1/admin/config/export`), **refused** when no credential
    encryption key is configured, because the mailbox passwords in it would be plaintext.
  - Continuous offload to S3-compatible object storage on the worker loop
    (`Backup:*`): `config/latest.json` promoted only after being staged and
    length-verified, a dated daily copy, and the append-only history tables
    (audit, alerts, digests, sync runs, ingest ledger) shipped as immutable JSONL with an
    overlap window so a row committed mid-pass is never skipped.
  - Import as a first-run console action, `restore` (only into an install nothing
    has been added to yet — the bootstrapped `default` client does not count) or `merge`.
    **Additive: never deletes a row.** On an email collision the imported user wins and
    only that account's sessions are revoked. An encryption-key fingerprint mismatch is
    an acknowledgeable warning, not a hard block — the rest of the artifact still
    imports, and the console reports which mailbox sources need their password
    re-entered by hand.
  - Optional report-mail archive to the same bucket, and **opt-in mailbox retention
    deletion** so the system has one retention window instead of two — cut on the widest
    window the source serves, suspended entirely for any source serving a client under
    legal hold, with a grace margin, a preview, and an audit row.
  - Not built: replaying reports back from the bucket archive. Until it exists the
    archive is evidence, not a restore path.
- ASP.NET Core API with Carter modules and EF Core + PostgreSQL integration.
- Core and ingestion/report schema migrations in place for:
  - `client`
  - `domain`
  - `mailbox_source`
- API vertical slice endpoints:
  - clients: list/get/create/patch. `slug` is immutable after creation. Every
    install is bootstrapped with a `default` client, because a domain and a
    mailbox source both require one
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
- Responsive console (single `lg` breakpoint at 1024px; desktop layout unchanged):
  - below `lg` the sidebar is an off-canvas drawer behind a top bar — backdrop, Escape, body scroll lock, focus moved in on open and returned to the trigger on close, and `invisible` while closed so the hidden menu is not in the tab order
  - page headers and header action rows stack; search fields and filter selects go full-width
  - every `Table` sits in a horizontal scroll container; `Dialog` caps at `calc(100dvh-2rem)` and scrolls internally
  - `Input`/`Select` render at 16px below `sm` (iOS Safari zooms the page on a focused field smaller than that) and controls gain 4px of height for touch
- Running version reported by the instance itself (`<Version>` in `src/Directory.Build.props`, read from `AssemblyInformationalVersionAttribute` — never from an environment variable, so an image cannot be made to misreport itself):
  - a release build reports `0.10.0`; every other build carries the commit as `0.10.0+a1b2c3d`, stamped from git locally and from the `SOURCE_REVISION` build arg in the container
  - surfaced at the foot of the console sidebar as a link to the matching release notes, in `GET /api/v1/system/status`, and as `service.version` on both OpenTelemetry resource builders
  - `/api/v1/system/status` reports the real `APP_MODE` rather than a hardcoded `api`

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
  - user delete (`DELETE /users/{id}`), guarded against deleting your own account or the last active `agency_admin`; sessions, OIDC identity links, and client grants cascade at the DB level
  - authN/authZ split: authorization is always in-app, authentication is pluggable

- Optional OIDC login (pluggable authentication):
  - hybrid flow (Microsoft OIDC handler → short-lived cookie → app-minted `dmarc_session`); local password and OIDC are interchangeable front doors by default
  - `user_identity` mapping with JIT provisioning (verified-email linking; configurable auto-provision + default role)
  - `Auth:Oidc` config, off by default; dev Zitadel in compose + `docs/ops/oidc-zitadel.md`
  - verified against Microsoft Entra ID as well as Zitadel; its provider-specific behaviour has its own runbook, `docs/ops/oidc-entra.md`. Entra needs a client secret (a Web-platform redirect URI is a confidential client), and the callback is pinned to a redirect rather than the handler's default form_post — a form_post callback is a cross-site POST, which carries no `SameSite=Lax` correlation cookie and failed every login with "Correlation failed" up to and including 0.7.0 (#114)
  - email assurance is three-state, not a boolean: `email_verified`, Entra's `xms_edov` optional claim ("email domain owner verified") in its place, or *unstated* — Entra issues no `email_verified` at all, and reading its silence as "unverified" refused every Entra user who already had a local account (#140). Silence is refused by default under its own error code, and `Auth:Oidc:TrustUnverifiedEmail` accepts it for providers whose addresses are administered; an explicit "not verified" is still refused either way. Provisioning is deliberately exempt — a new account lands on `DefaultRole` and has nothing to take over
  - `Auth:Oidc:DisableLocalLogin` turns off password sign-in and auto-redirects the login page to the provider, for deployments that want SSO-only; registration stays open until the first account exists, so bootstrap is unaffected; refused at startup without `Enabled`
  - admins can create passwordless (SSO-only) accounts: `password` is optional on `POST /api/v1/users`, which stores an empty hash that no password can satisfy — how you pre-provision at a chosen role with `AutoProvision=false`. `hasPassword` on the users list drives a "Sign-in" column that warns when such an account exists with no provider configured
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
  - optional `clientId` filter, applied before the top-N ranking so one client's worst offenders can't be pushed out of view by another client's larger volume

- Record inspection (published vs observed):
  - `IDnsTxtResolver` (DnsClient against the host's configured resolver — no third-party DoH) with short-lived caching
  - `GET /api/v1/analytics/domains/{id}/records` — live `_dmarc`/SPF TXT records parsed tag-by-tag (multiple-record permerror, missing rua, +all, 10-lookup count) and compared field-by-field against the latest `policy_published` reporters observed
  - Domain Detail "Record inspection" card, fetched separately so slow DNS never blocks the analytics render

- MTA-STS monitoring (RFC 8461):
  - a worker pass (`MtaSts:*`) checks every active domain: the `_mta-sts` TXT
    record (found / missing / lookup_failed / **invalid** — two records or bad
    syntax read as "no policy", the way senders behave), the policy file over
    HTTPS, its syntax, and whether the policy's `mx` patterns cover the live MX
    records (the MX-migration-under-enforce footgun)
  - the policy fetch is the codebase's first outbound HTTP: redirects reported
    rather than followed (RFC 8461 §3.3), 64 KB bound, certificate failures
    captured with their reason as findings rather than exceptions, and an
    egress guard that refuses private/loopback address space by default
    (`MtaSts__AllowPrivateNetworks`)
  - current state persists on `mta_sts_state`, one row per domain, with the
    DnsPolicyCache doctrine: failed lookups keep last-known values, a missing
    record is definitive and clears them; policy-id changes keep the previous
    id and a timestamp
  - three alert rules read that state (no network in the evaluator):
    `mta_sts_policy_change` (info), `mta_sts_broken` and `mta_sts_mx_mismatch`
    (critical under mode `enforce`, warning otherwise)
  - `GET /api/v1/analytics/domains/{id}/mta-sts` (database only, instant) and
    staff-only `POST …/mta-sts/recheck` (live check, persisted)
  - Domain Detail "Transport security (MTA-STS)" card; domains without a
    record render a deliberately quiet "Not configured" line — publishing
    MTA-STS is optional and most domains don't

- Hosted MTA-STS policies (RFC 8461 serving):
  - per-domain policy rows (`mta_sts_policy`: mode, max_age, mx patterns,
    server-generated id) served anonymously at `/.well-known/mta-sts.txt`,
    keyed on the request's Host header — paths outside `/api/v1/` are
    anonymous by construction, and the explicit route beats the SPA fallback
  - the id bumps exactly when the rendered policy content changes (senders
    only refetch on an id move); the console surfaces the CNAME + TXT records
    to publish, with the new TXT called out after every content change
  - `/mta-sts/ask` answers Caddy's on-demand-TLS gate, so certificate issuance
    is limited to hostnames this instance actually serves
  - CRUD (`GET`/`PUT`/`DELETE /api/v1/domains/{id}/mta-sts-policy`, writes
    admin-only, audited) plus client-level bulk apply
    (`POST /api/v1/clients/{id}/mta-sts-policy/apply`) for same-provider
    fleets — per-domain outcomes report exactly which TXT records changed
  - `APP_MODE=mta-sts` runs a dedicated internet-facing policy host (no
    console, no API, no auth stack, no migrations; readiness probes the
    policy table); the default remains serving from the api/all process
  - hosted policies are validated by the monitoring pass like any external
    one; a policy that has never been fetched successfully reads as setup
    guidance, not breakage, and the `mta_sts_broken` alert is suppressed for
    that window
  - policies ride in the backup artifact (id verbatim, so a restore forces no
    TXT updates); ops doc: `docs/ops/mta-sts-hosting.md`

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
  - **Alerts** — history with severity, type, status, per-domain links, whether
    each was emailed, an admin "Evaluate now" action, and triage
    (acknowledge / close / reopen) via `PATCH /alerts/{id}`
  - **Notifications** — recipient management (per-client or agency-wide) and a
    test-send button that surfaces the API's configuration error verbatim
  - **Clients** — retention window, legal hold, and per-client alert settings
    (enable, compliance-drop threshold, minimum messages) are editable in the
    console rather than API-only

- SMTP TLS reports (TLS-RPT, RFC 8460) are **ingested and stored**:
  - extraction now returns typed payloads — every mail attachment classifies by
    content into DMARC XML or TLS JSON and routes to its parser; zip entries
    are content-classified too (the old suffix-only skip dropped mislabeled
    entries), gzip'd non-reports keep their legacy route to the DMARC parser so
    its failure accounting is unchanged
  - `TlsRptReportParser` (System.Text.Json, no new dependency) is lenient where
    the wild is: `mx-host` vs `mx-host-pattern` both accepted (string or
    array — the RFC's prose and its own example disagree), counts as numbers or
    strings, unknown result types kept raw, missing per-detail counts read as 1
  - storage is four tables: `smtp_tls_report` (no DomainId — one report can
    span several policy-domains), `smtp_tls_report_policy` (where tenancy and
    the analytics window live, one row per policy-domain, resolved through the
    same create-or-get DMARC ingestion uses — now hoisted to
    `DomainIngestResolver`), `smtp_tls_failure_detail` (raw result type plus a
    stored **sts/dane/transport/other** category — the STS-vs-transport split
    that will gate MTA-STS promotion), and the `tls_report_ingest` ledger
    (parallel to the DMARC one; the unique key swaps the policy domain for the
    organization name)
  - dedupe via `ON CONFLICT DO NOTHING` on `(org, report-id, range)`, all in
    one transaction per report, mirroring the DMARC block
  - sync runs count `TlsReportsInserted` / `TlsReportsSkippedAsDuplicate`
    end-to-end (run rows, health rollup, console); TLS parse failures fold into
    the existing parse-failure counter with a format-naming log line
  - retention purges policy rows per client (window-end keyed), the ledger
    likewise, then sweeps report rows left with no policies past the longest
    retention any client has — which makes legal hold safe by construction
  - the three report tables join the backup exclusion list (replayable from
    the mailbox); the ledger ships as its own history stream
  - the analytics surface: `GET /api/v1/analytics/domains/{id}/tls-rpt` —
    sessions, success rate, failure breakdown by category and result type,
    failures per receiving MX — anchored to the newest **TLS** data the caller
    can see (TLS reporting lags DMARC; anchoring to DMARC's anchor would blank
    the panel), rendered in the Transport security card with the page's window
    selector; the no-reporter case renders quietly, it is the norm
  - the **testing→enforce gate**: a pure evaluator combining the monitoring
    checks (TXT, fetch, syntax, MX coverage), the hosted policy's
    time-in-testing clock (`ModeChangedAtUtc`), and the TLS-RPT evidence — no
    STS-category failure sessions in 14 wall-clock days (promotion is a
    decision about *now*, so the gate deliberately does not use data-anchored
    windows). Domains no reporter covers fall back to 28 clean days, and the
    verdict names its basis (`tls_rpt` vs `time_in_testing`). The ready state
    renders a promote button that reuses the audited hosted-policy PUT;
    readiness rides inside the mta-sts GET, null when no policy is hosted here

- Client addresses behind a proxy: `Network:UseForwardedHeaders` (off by
  default) makes the audit trail record the real caller instead of the proxy,
  but only from hops listed in `Network:TrustedProxies` / `TrustedNetworks`.
  Enabling it with an empty trust list is refused and logged, because that would
  let any caller forge the address on its own audit entries.

- Audit logging:
  - `audit_event` records who did what: sign-in success and failure, sign-out,
    first-run registration, client/domain/mailbox-source/user changes, grant
    changes, manual sync triggers, alert triage, recipient changes, and admin
    migrations — with IP and user agent
  - actor email **and client name** are denormalised and the table has **no
    foreign keys**, so the trail outlives the rows it refers to and does not
    re-label history when a client is renamed. Rows written before the client
    name was captured fall back to the current name
  - `IAuditLog` never throws: a failed audit write is logged, not propagated, so
    it cannot break the operation it describes
  - read-only over HTTP (`GET /api/v1/admin/audit-events`, filterable by day
    range, event-type prefix, actor and client) — there is no edit or delete
  - startup migrations are audited too: the API records
    `admin.database.migrated` as a system actor when a boot actually applies
    pending migrations, listing them in `Details`, and records nothing when
    there was nothing to apply
  - surfaced in the console at `/audit` (admin only): the same filters, paged 100
    at a time with the unpaged total shown, and a per-row expander for details,
    target and user agent
  - aged out by the retention pass on its own 2-year window
    (`Retention:AuditRetentionDays`), independent of client retention and legal
    hold

- Published DMARC policy comes from **live DNS, not from reports**, and is the
  policy that *applies* rather than the one the domain publishes. A subdomain with
  no record of its own is not unprotected: RFC 7489 §6.6.3 has the receiver fall
  back to the organisational domain and apply its `sp=`, else its `p=`.
  `DmarcPolicyResolver` walks up label by label and takes the first record found,
  which is what a receiver does — a tree walk as DMARCbis specifies, so there is no
  Public Suffix List data file to keep current. Before it existed, six domains on a
  real instance reported no policy while five were in fact covered by `p=reject`.
  - the effective policy is cached on the domain row with the ancestor it came
    from, so list views render from one query and a wrong inheritance is legible
    ("p=reject via yulsn.io") rather than silent
  - a subdomain publishing its own *weaker* record still wins, so one that has
    opted out of a parent's enforcement is not shown as enforced
  - a failed lookup keeps the last known policy **and** source: a transient
    SERVFAIL must not make a `p=reject` domain look unprotected

- Domains list groups sibling subdomains, but only where two or more monitored
  domains share a parent — 3 groups over 8 rows on a 56-domain instance, against
  the ~36 single-child headings that grouping everything would have added. The
  parent is usually *not* monitored (reports only arrive for the sending
  subdomain), so that heading is a label with no metrics and nothing to click.
  Sort order is preserved: a group appears where its first member landed, so
  worst-compliance-first still puts the worst group first.

- OpenTelemetry, configured by the specification's own `OTEL_*` variables and off
  until one is set — see `docs/ops/configuration.md`. Traces, metrics and logs over
  OTLP, or `console` with no collector at all. Instrumented: ASP.NET Core requests,
  **Npgsql at the driver level**, outbound `HttpClient`, runtime meters. Driver-level
  is the point: EF's `Executed DbCommand` duration stops at the first row, so a
  query that streams for seconds logs milliseconds — the gap that made a 7.7s
  request look like 1s of SQL. Probe paths are excluded, including
  `/api/v1/auth/setup`, which stays the readiness target because a 200 from it
  proves migrations were applied and `CanConnectAsync` does not.
  - structured JSON console logging, which ADR 0006 also lists as decided, is
    **not** implemented — the console logger is plain text. Backlog item.
  - resource attributes are the SDK's job except for two the process knows about
    itself: `app.mode`, and `service.version` — the build's version and, when it is
    not a release, its commit.

- The running build identifies itself. `<Version>` lives in
  `src/Directory.Build.props` (bumped with `Chart.yaml`, enforced by
  `VersionReferenceTests`) and the build stamps the commit onto it as
  `InformationalVersion` — from git locally, from the `SOURCE_REVISION` build
  argument in the container, where `.dockerignore` excludes `.git`. Read from the
  assembly and never from an environment variable: an image whose version can be set
  at run time can be made to lie about itself. Surfaced in `/api/v1/system/status`,
  in `service.version`, in the backup manifest's `appVersion`, and at the foot of
  the console sidebar.
  - Three shapes, and only one of them is a release. A bare version is the release,
    and CI produces it by passing the build argument *explicitly empty* on a tag.
    Version-plus-commit is a build past a release — an `edge` image, or a working
    tree. Version-plus-`local` is the container build's default, so an image built
    from `docker compose` or `docker build .` cannot claim to be the release by
    passing nothing: the claim has to be made, and staying silent makes the safe
    one. The sidebar links the first two to the release notes and to the commit; it
    renders `local`, and an unstamped `unknown`, as plain text, because neither
    resolves to anything on the remote.

- Report parsing tolerates malformed real-world reports rather than discarding
  them. One bad token used to fail an entire `<feedback>` document and lose every
  record in it, 28 on average:
  - values the strict enums reject are replaced with a documented fallback and
    named in a warning; the accepted sets are hand-maintained lists that started
    from the `XmlEnum` names on the DmarcRua enums, and as of 2.0.1 are
    deliberately narrower than them — see the note on that upgrade below
  - `unknown` and `error` in an SPF auth result are *translated* to `permerror`
    and `temperror` — the RFC 4408 names for what RFC 7208 renamed
  - a document truncated after its last complete `</record>` is completed and
    parsed; one truncated mid-record is still refused, because ingesting a partial
    report as whole would under-count permanently once the unique index keeps it
  - measured on a real mailbox: parse failures fell from ~1.5% of attachments to
    0.0%, and one reporter went from 100% discarded to ingesting
  - the parser's `ValidationMessages` are still discarded by `MailboxSyncService`,
    so these repairs leave no trace an operator can find. Backlog item.

- **DmarcRua is pinned at 2.0.1, and the alignment tags are read around it.** The
  library publishes no releases or changelog, so the upgrade was reviewed by diffing
  the commits embedded in the two nuspecs (`5d30703` → `7a59061`). It is worth taking:
  parsing became namespace-agnostic, a `trusted_forwarded` → `trusted_forwarder`
  typo was fixed, and enum values seen in the wild were added. Despite the patch
  version it is **source-breaking** — `PolicyPublishedType.Adkim`/`.Aspf` went from
  settable `AlignmentType?` to get-only properties computed from new
  `AdkimRaw`/`AspfRaw` strings — and the helper behind them calls `Regex.Replace`
  on a value that can be null. Both tags are `minOccurs="0"` in DmarcRua's own
  schema, so for a reporter that omits them, *reading* the property throws
  `ArgumentNullException` after deserialization has already succeeded. That is 1.5%
  of the 3241 real reports vendored in 2.0.1's own test resources, Mail.Ru and
  Fastmail among them, and it would have failed ingestion for every report from
  those reporters where 2.0.0 quietly returned null.
  - `DmarcRuaReportParser` therefore reads `AdkimRaw`/`AspfRaw` and maps them
    itself, which preserves 2.0.0's behaviour and does not wait on an upstream fix.
    Absent means `relaxed`, the fixed RFC 7489 §6.3 default for both tags. Reported
    upstream as [danielsen/DmarcRua#11](https://github.com/danielsen/DmarcRua/issues/11).
    Note the library merges contributions by reimplementing them in its own commits
    rather than by merging pull requests — every PR since 2022 is closed unmerged,
    including one of ours — so treat a fix as arriving whenever it arrives, and keep
    the workaround until a release actually carries one.
  - the upgrade was verified by running the parser over all 3242 reports in that
    corpus on both versions: no regressions, identical output on every report both
    parse, and one report gained — a `trusted_forwarder` report that 2.0.0 discarded
    whole over the typo.
  - our normalization now *pre-empts* the library on three values rather than
    mirroring it. The one that matters is `policy_evaluated` `none`, which 2.0.1
    aliases to `Pass` (`None = Pass`); we keep reading it as `fail`, because a
    mechanism the reporter never evaluated must not manufacture a DMARC pass and
    inflate compliance.
  - the upgrade also retired a workaround it made obsolete: **SPF `scope=helo` is
    recorded as sent** rather than rewritten to `mfrom`. 2.0.0 modelled only `mfrom`,
    so `helo` was fatal and the rewrite existed to save the document — at the cost of
    storing a scope the reporter never reported and showing it in the per-source SPF
    table (82 auth results across the 3242-report corpus). `scope` moved into
    `EnumRepairs` instead, because the enum still has no empty member. Historic rows
    stay `mfrom` and cannot be backfilled: the value was destroyed at parse time.
  - and fixed a latent bug found on the way. `EnumRepairs` matched values
    case-insensitively but wrote the reporter's spelling back, while `XmlSerializer`
    matches `XmlEnum` names case-sensitively — so `PASS` was accepted here and then
    rejected by the serializer, losing every record in the document. It now writes the
    canonical spelling; case-only corrections raise no warning.
  - the namespace-stripping pass is **not** redundant on 2.0.1, though it looks it.
    See the rejected backlog item: removing it turns one explanatory warning into 31
    `Could not find schema information` warnings per namespaced report.

## Planned Next

- Repository/service pattern hardening and broader indexing strategy.
- Alerting, digest delivery, and export workflows.

## Notes

- `docs/planning/backlog.md` is the prioritized task source of truth.
- `docs/planning/roadmap.md` defines milestone sequencing.
- `docs/planning/api-contract.md` and `docs/planning/data-model.md` include both implemented and planned target state.
- **There is deliberately no admin password recovery path.** No reset endpoint, no
  forgot-password flow, and no `APP_MODE=reset-password`. It was proposed after an
  agent lost track of a dev instance's admin password and rejected on the grounds
  that a mode able to rewrite any user's password is more attack surface than the
  scenario is worth, and that the scenario was self-inflicted rather than something
  operators hit. Recovery, if ever needed, is a deliberate database operation.
  Please do not re-propose it.
