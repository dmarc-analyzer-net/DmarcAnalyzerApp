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

2. **Deployment topologies.** In progress — see the block under Medium Priority
   and ADR 0008. Bundled-or-external Postgres × combined-or-split × Compose-or-K8s,
   packaged so the combinations stay in step instead of drifting.

Smaller, independent items to slot in opportunistically: **POP3 ingestion**, the
**report upload/query API endpoints** (which would also make seeding test data
far easier), and **CSV/JSON export**. Larger, deferred until a deployment calls
for them: **branded PDF reports** and **M365/Google Workspace connectors**.
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

### Deployment topologies (ADR 0008)

Sequenced; each step is independently shippable.

- [ ] (step 1) **`APP_MODE=all` — combined runtime mode.** One container running
      the API and the worker loop in-process. The web host already registers every
      service the worker needs, so this is `AddHostedService<QueueWorkerService>()`
      plus mode parsing. Also make an unrecognised `APP_MODE` fail startup instead
      of silently defaulting to `api` — a typo that serves traffic but ingests
      nothing is the worst outcome available.
- [x] (step 2, done) **Compose overlays.** `deploy/compose.yml` (app in `all`
      mode + bundled Postgres, complete on its own) plus `compose.external-db.yml`
      and `compose.split.yml`, giving four topologies from three files. Shipped
      inverted from the sketch above: the overlays *subtract*, because `!reset`
      turns out to remove services, so the published quick-start URL keeps
      producing a working stack with no overlay at all. All four combinations
      booted and verified to run exactly one worker.
- [x] (step 3, done) **`docs/ops/configuration.md` + drift check.** Every setting
      documented once — 42 bound properties across eight sections, plus the
      settings read straight from `IConfiguration` and the Compose-side variables. `ConfigurationContractTests` asserts the contract from four
      directions — every bound property documented, every documented variable still
      bound, every `*Options` class registered, every `appsettings.json` leaf
      covered — and each direction was verified by injecting the drift and watching
      it fail. Runs in `dotnet test`, so it needed no CI change.
- [x] (step 4, done) **Helm chart.** `deploy/helm/dmarc-analyzer`.
      `postgres.enabled` and `mode: combined|split` mirror the Compose axes;
      `existingSecret` for the encryption key and DB password; bundled Postgres is
      a minimal in-chart StatefulSet documented as evaluation-grade. Needed a new
      `APP_MODE=migrate` (apply pending migrations and exit) — without it there was
      no way to migrate *before* an app pod, which is what the Job requires.
      Guardrails refuse `combined` with `app.replicas>1`, `worker.replicas!=1`,
      `startup` migrations with multiple replicas, and a missing encryption key.
- [x] (step 5, done) **Installed for real** against the k3d cluster on
      `hermes-agent`, not just `helm template`. Two chart bugs only a real install
      surfaced: the app raced Postgres DNS and crash-looped until it was up (fixed
      with a `wait-for-database` init container, since Kubernetes has no
      `depends_on: service_healthy`), and the pre-install migration Job named a
      ServiceAccount that hooks create *after* hooks run, so it never made a pod.
      Both topologies then came up clean, plus an upgrade verified as a no-op.
- [x] (done) **Decided: two workers is not safe, and the limit is now enforced in
      code.** Read the claim path rather than guessing. There is no claim mechanism
      at all — `QueueWorkerService.cs:213` reads every active source with
      `AsNoTracking()` and iterates, no lease, no ownership column, and repo-wide
      there is no `FOR UPDATE`, `SKIP LOCKED`, advisory lock or CAS anywhere.
      Reports themselves are safe (every insert is `ON CONFLICT DO NOTHING` on a
      real unique index, and the loser is detected by affected-row count), but four
      things break: duplicate alert email (`AlertEvaluationService.cs:267` cooldown
      is read-then-write with only non-unique indexes behind it), a duplicate
      digest sent at `DigestService.cs:287` *before* the unique index rejects the
      second row at `:291`, `DbUpdateConcurrencyException` from
      `RetentionPurgeService.cs:183` deleting a batch another worker already
      deleted, and a checkpoint that can move *backwards* because
      `MailboxSyncService.cs:209` writes unconditionally with no concurrency token.
      Every "is it due" gate is an in-memory field (`QueueWorkerService.cs:85`,
      `:117`, `:150`, `:177`), so two processes share no timer state.

      Also found: `20260402150000_AddMailboxSyncActiveRunUnique` added exactly the
      partial unique index that would guard this, and
      `20260403143000_RemoveMailboxSyncActiveRunUnique` dropped it a day later with
      no rationale. It guarded nothing, because no code ever writes a `running`
      row — `MailboxSyncRun` is only ever constructed with `success`
      (`MailboxSyncService.cs:222`) or `failed` (`:259`), after the sync finishes.
      That also makes `CloseStaleRunningSyncsAsync` dead code against current
      writers.

      `WorkerSingleInstanceLock` now takes a Postgres advisory lock at startup and
      the process exits if another holds it, so the limit holds however you deploy
      — the chart's `worker.replicas` guard only covered Kubernetes, and nothing in
      Compose prevents `--scale worker=2` or a worker beside an `APP_MODE=all`
      container. `Worker__EnforceSingleInstance` can turn it off.

- [ ] (todo) **Lifting the one-worker limit**, if it is ever wanted. Needs, in
      order of how much each buys: a real claim on `mailbox_source`
      (`SELECT … FOR UPDATE SKIP LOCKED`, or reinstate the `running` row *and* the
      partial unique index and write it before the IMAP connect); a unique
      constraint on `alert_event` over client/domain/rule/cooldown-bucket with the
      insert committed before the send; the digest `SendAsync` moved to after a
      successful `DigestDelivery` insert; the retention purge switched to
      `ExecuteDeleteAsync` on a bounded subquery so a 0-row delete is not an error;
      and a conditional checkpoint write (`WHERE "LastProcessedUid" < @new`) so it
      can only move forward. Nothing here is needed for a single worker.

- [ ] (todo) **A report can be left with zero records, permanently.**
      `MailboxSyncService.cs:162` inserts the parent `dmarc_report` row and commits
      it *before* `BeginTransactionAsync` at `:180` opens the transaction that
      inserts the records. If the records insert fails, the report row survives with
      no children — and because dedupe is keyed on that row, every later sync sees a
      duplicate and skips it, so the records are never backfilled. Single-worker
      bug, unrelated to concurrency; found while answering the concurrency
      question. Fix is to insert the report inside the same transaction as its
      records, keeping the `ON CONFLICT DO NOTHING` semantics.

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
- [x] (done) Fix the analytics window scans. The plan recorded here — indexes on `dmarc_report (DomainId, RangeBeginUtc)` and `(RangeEndUtc)` — was measured and **does not work**: with the window filter reaching through the record->report navigation, the planner still hash-joined and sequentially scanned all 5.27M records. Tested by creating the index inside a rolled-back transaction, so the disproof cost nothing. What worked instead was denormalising the report's range start onto `dmarc_report_record` and indexing that (`ReportRangeBeginUtc`), turning a window into a bitmap index scan: 179,849 rows located in 3.5ms over 1,137 heap blocks, rather than a 740MB scan repeated once per aggregate. `/analytics/summary` 1074ms -> 380ms. The window anchor (`MAX(RangeEndUtc)`) needs no index of its own; it measures 19-26ms.
- [x] (done) Fix per-group correlated subqueries in the analytics aggregates. `Min`/`Max`/`COUNT(DISTINCT)` over a navigation inside a `GroupBy` makes EF emit one subquery per output group. `/enforcement` spent 7.7s wall against 70ms of process CPU and ~1s of logged SQL (`GroupAggregate ... actual time=567..25497 rows=1136`); `/threats` spent 4.1s. Rewritten as explicit joins over a flattened projection — keeping InMemory testability rather than dropping to raw SQL — and the results checked against independently written SQL, all six aggregates matching exactly. `/enforcement` 7.7s -> ~70ms, `/threats` 4.1s -> 162ms.
- [x] (done) Fix the last correlated subquery, in `ListDomainAnalyticsAsync`. `Reporters` counts distinct `OrganizationName`, which lives on the parent report, so despite the flattened projection EF still emitted a per-domain subplan that re-joined `dmarc_report` twice (as both `d2` and `d3`) — 1,930ms of a 1,988ms request, while the window scan beneath it took 239ms. The method's own comment already claimed this pathology was avoided, which was true for `Reports` and `Sources` (plain record columns) but not for `Reporters`: a `Select` projection is not a derived table, and EF composes it away and re-resolves the navigation inside each aggregate. An explicit join makes the report a real table in the same `FROM`, so all three become plain `count(DISTINCT ...)`. **`/analytics/domains` 1,988ms -> 230ms.** Output verified unchanged against independently written SQL: 30 domains x 10 fields, zero mismatches, and the 26 domains with no rows in the window still read zero rather than dropping out. `DomainListAnalyticsTests` now pins the three counts, which nothing previously asserted.
- [x] (done) Fix horizontal overflow in the two widest tables — Domains needed 1110px and the sending-sources table 1425px inside a 1038px container. Both now fit exactly, with nothing truncated: the options listed here (truncate hostnames with a `title`, narrow the compliance meter) were rejected in favour of letting content occupy a second line and share width across columns. Domains stacks the client under the domain name in one column, which alone reclaimed 178px of the 446px those two columns used to take. Sources moves the reverse-DNS hostname to its own row spanning the first four columns, so a 395px hostname costs the Source IP column nothing, and moves Quarantined and Rejected into the row expansion that already existed. Each source is now its own `<tbody>` so the divider and hover belong to the source rather than to each of its rows. Worth recording because it is counter-intuitive: after the hostname moved, **IPv6 was the entire remaining overflow**. Shortening every hostname in the rendered table changed the Source IP column not at all (348px before and after); shortening every IP took it from 348px to 119px. A `<wbr>` after each colon lets long IPv6 fold at a group boundary — 198 of 1136 rows wrap, IPv4 never does. Sorting by client, quarantined and rejected went away with their headers; the client filter already covers the first.
- [ ] (todo) Virtualise or page the sending-sources table. The busiest domain reports 1,136 distinct sources in a 30-day window and the table renders every one of them at once: 1,236 `<tr>`, 9,188 `<td>`, and 22,931 DOM nodes — 98% of everything on the page. Re-sorting by clicking a column header costs 253-306ms of blocking main-thread work, three to four times the `/sources` request it is displaying (~80ms), so the page feels slow for a reason no server timing will ever show. Found while measuring the overflow fix above, not from a report. Options: windowed rendering, server-side paging with the sort pushed into SQL, or a "top N + show all" disclosure — note that sorting is currently client-side over the full set, so paging would have to move it to the server to stay correct. Pairs with the OpenTelemetry item above: browser-side cost is invisible to backend instrumentation in the same way row streaming is invisible to EF's command duration.
- [x] (done) Add guided path to enforcement: per-domain policy recommendation engine surfacing the next safe policy step (none -> quarantine -> reject) and the sources still blocking full enforcement (`/enforcement` endpoint + Domain Detail panel).
- [x] (done) Persist published DMARC policy (`policy_published` from reports) and add a record-inspection view comparing published DMARC/SPF records (live DNS via host resolver) against observed report data (`/records` endpoint + Domain Detail card).
- [x] (done) Add a threat feed view: dedicated list of unauthenticated/failing sending sources with IP, volume, and first/last-seen for spoofing investigation (`/threats` endpoint + Threats page in sidebar).


### Deployment follow-ups

- [x] (done) **Live-instance migration brief.** `docs/ops/live-migration-handover.md`,
      dry-run against a local replica of the Omarchy stack rather than written from
      code-as-intended. Found that `docker compose up -d api worker` does **not**
      apply a pending migration when the image is unchanged — Compose recreates on
      config change and a pending migration is invisible to it — while
      `/api/v1/auth/setup` still returns 200, so a green healthcheck proves nothing
      about the schema. The brief leads with `run --rm -e APP_MODE=migrate api`
      instead, which needs no container recreation and keeps the instance serving.
      Still needs someone on that machine to run it; it is unreachable from
      `hermes-agent` (the mesh peer exists but is offline).

- [x] (done) **Publish the chart.** A release tag pushes it to
      `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer`, with `version` and
      `appVersion` both taken from the tag so the chart version alone determines the
      application version. CI renders all six supported value combinations and
      asserts every guardrail still refuses, on every run — the combinations do not
      share a code path, which is how a migration Job that could never create a pod
      reached a real cluster.

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
- [x] (done) Move the GitHub Actions off the Node 20 runtime. Every action is now
      pinned to the *first* major declaring `using: node24`, verified by reading
      `action.yml` at each tag: checkout/setup-node/setup-dotnet v4 -> v5,
      login/setup-buildx/setup-qemu v3 -> v4, metadata v5 -> v6, build-push
      v6 -> v7.

      Correction to the original note, which said the `docker/*` actions were
      already current and not part of this: they were all node20. That was judged
      from version numbers looking recent rather than from the declared runtime,
      which is the same mistake the item was written to avoid.


- [ ] (todo) Evaluate optional BIMI support after DMARC MVP.
- [ ] (todo) **Ingest and store SMTP TLS reports (TLS-RPT, RFC 8460).** Scoped
      2026-07-25. Attachments are already recognised and skipped cleanly, so this
      is additive rather than a fix.

      *Why:* two competitors researched the same day ship it — DMARCwise hosts
      MTA-STS and TLS-RPT, and `cry-inc/dmarc-report-viewer` parses TLS reports
      in a 10 MB binary. Adoption is real: 4 of 13 sampled domains publish
      `_smtp._tls` records, including `skat.dk` and `borger.dk`. It answers a
      different client question from DMARC — "is mail to this domain actually
      encrypted in transit" rather than "is anyone spoofing us".

      *Reusable as-is:* IMAP polling, message iteration, checkpointing, the job
      queue with retry and dead-lettering, sync-run bookkeeping, and
      `ResolveOrCreateDomainIdAsync` — it takes a domain string and does not care
      which report produced it.

      *The work:*
      - **Extraction** returns typed payloads. `ReportPayloadFormat.Classify`
        already identifies TLS JSON; the extraction path needs to hand it on
        instead of skipping.
      - **Parser.** Small: I-JSON, ~8 top-level fields, two nested arrays
        (`policies[]` holding `policy` / `summary` / `failure-details`).
        `System.Text.Json`, no new dependency, and none of the DmarcRua quirks
        that produced the `sp` defect.
      - **Model.** 2–3 tables: report, policy, failure detail.
      - **Dedupe — the one real design decision.** `dmarc_report_ingest` is
        DMARC-shaped (`PolicyDomain`, `RecordCount`, a five-column unique index).
        Either add a report-type discriminator, which is cleaner but touches that
        index and the retention purge, or add a parallel `tls_report_ingest`.
        Decide before writing the migration.
      - **Retention.** A second purge pass mirroring the DMARC one, keyed on the
        report window end.
      - **Surface.** The larger half. `AnalyticsQueryService` alone has ~40
        `DmarcReport` references, so TLS needs its own queries and endpoints. The
        cheapest useful UI is a panel on the domain detail page — successful vs
        failed sessions, failure types, MX hosts — not a second product.

      *Also worth adding while in here:* a skipped-TLS-report counter on
      `mailbox_sync_run`, surfaced beside parse failures, so operators can see
      TLS traffic arriving before support lands. Left out of the fix above
      because it needs a migration and a UI change.
