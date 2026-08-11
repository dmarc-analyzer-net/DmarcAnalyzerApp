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
      order of how much each buys: a real claim on `report_source`
      (`SELECT … FOR UPDATE SKIP LOCKED`, or reinstate the `running` row *and* the
      partial unique index and write it before the IMAP connect); a unique
      constraint on `alert_event` over client/domain/rule/cooldown-bucket with the
      insert committed before the send; the digest `SendAsync` moved to after a
      successful `DigestDelivery` insert; the retention purge switched to
      `ExecuteDeleteAsync` on a bounded subquery so a 0-row delete is not an error;
      and a conditional checkpoint write (`WHERE "LastProcessedUid" < @new`) so it
      can only move forward. Nothing here is needed for a single worker.

- [x] (done) **A report could be left with zero records, permanently.**
      `MailboxSyncService` inserted the parent `dmarc_report` row *before* opening the
      transaction that inserts its records, so the report auto-committed on its own.
      A failed records insert left the report with no children — and because dedupe
      keys on that row, every later sync saw a duplicate and skipped it, so the
      records were never backfilled. One bad record made a report permanently empty
      and silently wrong. Fixed by moving the report insert inside the same
      transaction. Domain resolution deliberately stays outside: a domain is shared
      by every report for it, not owned by one.

      Proven against real Postgres by forcing a NOT NULL violation on the records
      insert: the old statement order leaves 1 report / 0 records and a retry
      inserts nothing; the new order leaves 0 / 0, so a retry can succeed. Happy
      path and duplicate path both re-verified.

- [ ] (todo) **An integration-test harness for the ingestion path.** This is the
      second real bug in `MailboxSyncService` that the current test suite cannot
      reach, and the reason is structural: every test uses `UseInMemoryDatabase`,
      which supports neither the raw SQL nor the transactions this code depends on,
      and the service needs an IMAP connection. Both fixes were verified by hand
      against real Postgres, which is honest but not repeatable. Options are
      Testcontainers for Postgres plus an `IMailStore` seam over MailKit, or a
      narrower seam that lets the report-and-records write be driven directly.
      Worth doing before the next change to this file.

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
- [x] (done) Make the console usable on a phone. The shell was the whole problem: a permanent 230px sidebar plus `main`'s flat `px-8` left ~96px of content at 390px, and there was no breakpoint anywhere in `ConsoleLayout`. Below `lg` the sidebar is now an off-canvas drawer (backdrop, Escape, scroll lock, focus moved in and returned, `invisible` so the closed menu is not tabbable) behind a top bar; at `lg` and up the layout is byte-for-byte the old one. Per-page pass on top: headers and action rows stack, search fields go fluid, the three tables that lacked a scroll container got one, dialogs cap at `100dvh` and scroll internally (the taller forms previously pushed their submit button off a short viewport with no way to reach it), and inputs go 16px below `sm` because iOS Safari zooms the page on any smaller focused field. Verified at 320/390/1023/1024/1280 — zero horizontal overflow on all 11 routes at 320px.
- [ ] (todo) Add Kubernetes deployment assets — Helm chart(s) with health checks and stateless service patterns, supporting both self-contained (bundled PostgreSQL, local auth) and bring-your-own deployments (external managed PostgreSQL, external OIDC), toggled via chart values.
- [ ] (todo) Add branded PDF report generation (server-side HTML to PDF) with agency logo/colors/footer.
- [x] (done) Add monthly email digest delivery and SMTP relay configuration (`DigestService`, previous-whole-month period, `digest_delivery` for idempotency, worker check pass, admin preview/send endpoints).
- [x] (done) Add alert engine for failure spikes and policy regression with per-client thresholds (`AlertEvaluationService`, hourly worker pass, `alert_event` history with cooldown, per-client overrides on `client`, email notification, `GET /alerts` + admin evaluate endpoint).
- [x] (done) Add core audit logging for login events, config changes, and manual sync triggers (`audit_event`, `IAuditLog`, admin query endpoint, `/audit` console page, 2-year retention). Scheduled sync runs are covered by `mailbox_sync_run` rather than duplicated; magic-link events will be added with magic links.
- [ ] (todo) Surface parse validation warnings instead of discarding them. `DmarcRuaReportParser` returns `ValidationMessages`, `HasValidationWarnings` and `HasValidationErrors`, and `MailboxSyncService` references none of them — so every normalization the parser performs is invisible in production. That currently hides three repairs: stripped DMARCbis namespaces, SPF `scope=helo` rewritten to `mfrom`, and empty `policy_evaluated` dkim/spf read as `fail`. The last one substitutes a verdict the reporter never sent, which is defensible only if an operator can find out it happened. Nothing is persisted either — `dmarc_report` has no column for it — so a report cannot be traced back to the repairs applied to it. Found while fixing the empty-result crash, not from a report.
- [x] (done) Instrument the API and worker with OpenTelemetry — traces, metrics and logs over OTLP, configured entirely through the specification's own `OTEL_*` variables so the values a self-hoster already has work unchanged. Off by default and free when off: with none set the SDK is never registered. Instrumented: ASP.NET Core requests, **Npgsql at the driver** (the point of the exercise — EF's `Executed DbCommand` duration stops at the first row, so a query that streams for seconds logs milliseconds, which is why the 7.7s `/enforcement` request appeared to spend ~1s in SQL), outbound `HttpClient`, and runtime meters. Probe paths are excluded from tracing, including `/api/v1/auth/setup` — the readiness target in both Compose and the chart, kept there rather than moved to `/health/ready` because a 200 from it proves migrations were applied and `CanConnectAsync` does not. One `IHostApplicationBuilder` extension covers every `APP_MODE`; both halves share a `service.name` and are told apart by an `app.mode` resource attribute.
- [x] (done) Fix the analytics window scans. The plan recorded here — indexes on `dmarc_report (DomainId, RangeBeginUtc)` and `(RangeEndUtc)` — was measured and **does not work**: with the window filter reaching through the record->report navigation, the planner still hash-joined and sequentially scanned all 5.27M records. Tested by creating the index inside a rolled-back transaction, so the disproof cost nothing. What worked instead was denormalising the report's range start onto `dmarc_report_record` and indexing that (`ReportRangeBeginUtc`), turning a window into a bitmap index scan: 179,849 rows located in 3.5ms over 1,137 heap blocks, rather than a 740MB scan repeated once per aggregate. `/analytics/summary` 1074ms -> 380ms. The window anchor (`MAX(RangeEndUtc)`) needs no index of its own; it measures 19-26ms.
- [x] (done) Fix per-group correlated subqueries in the analytics aggregates. `Min`/`Max`/`COUNT(DISTINCT)` over a navigation inside a `GroupBy` makes EF emit one subquery per output group. `/enforcement` spent 7.7s wall against 70ms of process CPU and ~1s of logged SQL (`GroupAggregate ... actual time=567..25497 rows=1136`); `/threats` spent 4.1s. Rewritten as explicit joins over a flattened projection — keeping InMemory testability rather than dropping to raw SQL — and the results checked against independently written SQL, all six aggregates matching exactly. `/enforcement` 7.7s -> ~70ms, `/threats` 4.1s -> 162ms.
- [x] (done) Fix the last correlated subquery, in `ListDomainAnalyticsAsync`. `Reporters` counts distinct `OrganizationName`, which lives on the parent report, so despite the flattened projection EF still emitted a per-domain subplan that re-joined `dmarc_report` twice (as both `d2` and `d3`) — 1,930ms of a 1,988ms request, while the window scan beneath it took 239ms. The method's own comment already claimed this pathology was avoided, which was true for `Reports` and `Sources` (plain record columns) but not for `Reporters`: a `Select` projection is not a derived table, and EF composes it away and re-resolves the navigation inside each aggregate. An explicit join makes the report a real table in the same `FROM`, so all three become plain `count(DISTINCT ...)`. **`/analytics/domains` 1,988ms -> 230ms.** Output verified unchanged against independently written SQL: 30 domains x 10 fields, zero mismatches, and the 26 domains with no rows in the window still read zero rather than dropping out. `DomainListAnalyticsTests` now pins the three counts, which nothing previously asserted.
- [x] (done) Fix horizontal overflow in the two widest tables — Domains needed 1110px and the sending-sources table 1425px inside a 1038px container. Both now fit exactly, with nothing truncated: the options listed here (truncate hostnames with a `title`, narrow the compliance meter) were rejected in favour of letting content occupy a second line and share width across columns. Domains stacks the client under the domain name in one column, which alone reclaimed 178px of the 446px those two columns used to take. Sources moves the reverse-DNS hostname to its own row spanning the first four columns, so a 395px hostname costs the Source IP column nothing, and moves Quarantined and Rejected into the row expansion that already existed. Each source is now its own `<tbody>` so the divider and hover belong to the source rather than to each of its rows. Worth recording because it is counter-intuitive: after the hostname moved, **IPv6 was the entire remaining overflow**. Shortening every hostname in the rendered table changed the Source IP column not at all (348px before and after); shortening every IP took it from 348px to 119px. A `<wbr>` after each colon lets long IPv6 fold at a group boundary — 198 of 1136 rows wrap, IPv4 never does. Sorting by client, quarantined and rejected went away with their headers; the client filter already covers the first.
- [x] (done) Record the SPF `helo` scope as sent instead of rewriting it to `mfrom`. DmarcRua 2.0.0 modelled only `mfrom`, so `helo` — legal per RFC 7208 and sent by real reporters — was fatal to the whole document, and the parser rewrote it to save the report. That stored a scope the reporter never reported and surfaced it in the per-source SPF table on `DomainDetailPage`, so it was a wrong value rather than a missing one: **82 auth results across the 3242-report corpus**. 2.0.1 added `SpfDomainScope.Helo`, so the rewrite is gone and `scope` moved into `EnumRepairs` (`["mfrom", "helo"]`, falling back to `mfrom`) — the enum still has no empty member, so `<scope/>` or an unrecognised value is fatal and must still be repaired. No migration: the information was destroyed at parse time, so historic rows stay `mfrom` and cannot be recovered. Frontend needed no change; `scope` is already `string | null` there.
- [x] (done) Write the canonical spelling, not the reporter's, when `EnumRepairs` accepts a value case-insensitively. Latent bug, found while moving `scope` into that table: the pass matched `OrdinalIgnoreCase` and then wrote the reporter's own spelling back, but `XmlSerializer` matches `XmlEnum` names **case-sensitively** — so `PASS` or `HELO` was accepted here and then rejected by the serializer, losing every record in the document, which is the exact failure this pass exists to prevent. Case-only corrections raise no warning, since they substitute no meaning.
- [ ] (todo) ~~Drop the namespace-stripping pass now that 2.0.1 ignores namespaces.~~ **Measured and rejected — do not do this.** The claim was that `NamespaceIgnorantXmlReader` makes `NormalizeReportXml`'s namespace pass redundant. It does not: that reader only hides namespaces from the *serializer*, while the validating reader beneath it still sees them, and `rua.xsd` declares no `targetNamespace`. With the pass removed, a namespaced report deserializes but matches no schema, so **every element** raises `Could not find schema information` — 31 warnings on a one-record report, and `HasValidationWarnings` true. Stripping first keeps schema validation meaningful and costs one explanatory message instead of 31 useless ones. Kept as a `[ ]` rather than deleted so the idea is not re-proposed. Unrelated but worth knowing: DmarcRua declares `NamespaceIgnorantXmlReader` as a `public` type in the *global* namespace, so it is visible unqualified everywhere in the API project.
- [ ] (todo) Emit structured JSON logs, which ADR 0006 lists as an accepted decision and nothing implements — the console logger is plain text, and no `AddJsonConsole` call exists anywhere. Cheap on its own, and it is the half of that ADR still outstanding now that the OTEL pipeline is in: OTLP log export covers a deployment with a collector, and this covers the far more common one that just reads `docker logs`.
- [x] (done) Add a test framework to `src/web` — vitest + jsdom + testing-library, wired into CI next to the type-check so it actually runs. Sixteen tests to start: the subdomain grouping helper (including the invariant that every domain is rendered exactly once, and that a group lands where its first member fell in the sort rather than at the end), and a render test of the Domains table asserting what the screen shows — a label heading for an unmonitored parent, a monitored parent promoted to its own heading rather than listed twice, the `via yulsn.io` marker on an inheriting row, and no marker on a subdomain publishing its own weaker record. Written because the grouping shipped verified only by transpiling the module and running it over real domain names; the rendering itself was unverified, and checking it in a browser needed a session. Verified load-bearing by mutation: grouping single-child parents fails three of them.
- [ ] (todo) Virtualise or page the sending-sources table. The busiest domain reports 1,136 distinct sources in a 30-day window and the table renders every one of them at once: 1,236 `<tr>`, 9,188 `<td>`, and 22,931 DOM nodes — 98% of everything on the page. Re-sorting by clicking a column header costs 253-306ms of blocking main-thread work, three to four times the `/sources` request it is displaying (~80ms), so the page feels slow for a reason no server timing will ever show. Found while measuring the overflow fix above, not from a report. Options: windowed rendering, server-side paging with the sort pushed into SQL, or a "top N + show all" disclosure — note that sorting is currently client-side over the full set, so paging would have to move it to the server to stay correct. Pairs with the OpenTelemetry item above: browser-side cost is invisible to backend instrumentation in the same way row streaming is invisible to EF's command duration.
- [x] (done) Add guided path to enforcement: per-domain policy recommendation engine surfacing the next safe policy step (none -> quarantine -> reject) and the sources still blocking full enforcement (`/enforcement` endpoint + Domain Detail panel).
- [x] (done) Persist published DMARC policy (`policy_published` from reports) and add a record-inspection view comparing published DMARC/SPF records (live DNS via host resolver) against observed report data (`/records` endpoint + Domain Detail card).
- [x] (done) Add a threat feed view: dedicated list of unauthenticated/failing sending sources with IP, volume, and first/last-seen for spoofing investigation (`/threats` endpoint + Threats page in sidebar).


### Deployment follow-ups

- [x] (done) **Live-instance migration brief.** `docs/ops/migrating-a-running-instance.md`,
      dry-run against a local replica of a split api/worker stack rather than written
      from code-as-intended. Found that `docker compose up -d api worker` does **not**
      apply a pending migration when the image is unchanged — Compose recreates on
      config change and a pending migration is invisible to it — while
      `/api/v1/auth/setup` still returns 200, so a green healthcheck proves nothing
      about the schema. The brief leads with `run --rm -e APP_MODE=migrate api`
      instead, which needs no container recreation and keeps the instance serving.
      Still needs someone on that machine to run it; it is unreachable from
      `hermes-agent` (the mesh peer exists but is offline).

- [x] (done) **The app reports its own version** (issue #150). `<Version>` in
      `src/Directory.Build.props`, stamped with the commit as
      `InformationalVersion`, surfaced in `/api/v1/system/status`, in
      `service.version`, and as a link in the console sidebar — release notes for a
      release, the commit for a build past one. Until this, a running instance could
      not answer which version it was: nothing set a version at all, so every
      release published an assembly saying `1.0.0`, and the only real version lived
      in `Chart.yaml` and the image tags. Someone on `:latest` had no way to find
      their own release notes. The commit is stamped on every build *except* a tag,
      which is what makes `0.9.0` mean the release and `0.9.0+a1b2c3d` mean an
      `edge` image built past it.

- [x] (done) **Publish the chart.** A release tag pushes it to
      `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer`, with `version` and
      `appVersion` both taken from the tag so the chart version alone determines the
      application version. CI renders all six supported value combinations and
      asserts every guardrail still refuses, on every run — the combinations do not
      share a code path, which is how a migration Job that could never create a pod
      reached a real cluster.

## Documentation debt (from the July 2026 review)

Every doc in this repo was checked against the code. The live-migration
handover, `src/api/README.md`, the MailKit advisories and the two items above
(session cleanup, erasure) came out of the same pass and are done. What follows
is what was found and not fixed. Line numbers were accurate in July 2026.

The theme: **`architecture.md` and `api-contract.md` describe a system that was
designed and then built differently.** `data-model.md` and the newer ops docs
are accurate; the older planning docs are not, and they are what a new
contributor reads first.

### `architecture.md` — five drift items, two of them now fixed

- [ ] (todo) **§7 documents a DB-backed job queue** (`job_type`, `payload`,
      `attempt_count`, `locked_by`, dead-letter states, backoff). There is no
      job entity and no job table among the 17 `DbSet`s;
      `QueueWorkerService.cs:213` reads active sources and iterates in-process.
      ADR 0008 states it plainly — "there is no claim path" — and
      `data-model.md:320` already records the truth. The class name is the only
      surviving trace of the decision.
- [x] (done) **§5 listed 20 entities; 12 did not exist** — eight were never
      created (`mailbox_source_client`, `sync_checkpoint`, `raw_report`,
      `alert_rule`, `digest_schedule`, `export_job`, `magic_link_nonce`,
      `retention_policy`) and four shipped under other names (`sync_run`,
      `dmarc_record`, `dmarc_auth_result`, `alert_recipient`). The list is gone
      rather than re-synced, because a second copy of the schema is what let it
      drift; §5 now points at `data-model.md` and keeps only what is specific to
      the design — tenancy, the two dedup keys, and a table recording where the
      build departed from the sketch. That table is the part worth keeping: a
      corrected list would have erased the fact that these names were ever
      intended, which is what makes the departures legible.
- [x] (done) **"All client-scoped entities include `client_id`" was false** for
      the four report tables and `mailbox_sync_run`. §5 now states the transitive
      path (`dmarc_report.DomainId → domain.ClientId`) and says why global domain
      uniqueness is load-bearing rather than tidy, drops the three composite
      indexes that never existed, and records that isolation is enforced in
      application code with no `HasQueryFilter` anywhere — so nothing in the data
      layer catches a query that forgets it.
- [ ] (todo) **Alerts, digest, retention, notifications and audit are marked
      "planned, not built"** across §98, §128–130 and two "Not implemented"
      banners. All five ship, with modules, services, worker passes and console
      pages. Only exports, PDF and magic links are genuinely unbuilt.
- [ ] (todo) **Smaller drift in the same file:** the Kubernetes section
      describes a `CronJob` (the chart ships a long-running Deployment, and the
      chart is absent from the doc entirely); observability claims structured
      JSON logs and an OTEL exporter (neither is wired — no package, no
      `AddJsonConsole`); §362 says "Playwright Chromium is already a dependency"
      (it is not, in either project or the image); Compose services are named
      `app-api`/`app-worker`, which exist in no file; the worker settings list
      omits four `Worker__*` keys plus the whole `Alerts`/`Digest`/`Dns`/
      `Retention`/`Email` sections and `WorkerSingleInstanceLock`; and the
      ingestion flow gets steps 1, 2, 8 and 10 wrong.

### `api-contract.md` — §0 is accurate, the rest has drifted

The route inventory is genuinely complete: all 51 implemented routes documented,
zero undocumented. The problems are everywhere else.

- [ ] (todo) **Five auth levels are documented stricter than the code
      enforces** — `GET /clients`, `/clients/{id}`, `/domains`, `/domains/{id}`
      and `/system/status` are all `AllowClientViewer`, not staff-only. Rows are
      grant-scoped so there is no cross-tenant read, but a `client_viewer` does
      receive the full `ClientDto` including `retentionMonths`, `legalHold` and
      the alert thresholds. Worth deciding whether those belong in a
      viewer-visible DTO, then making doc and code agree. **No admin-only
      endpoint is actually open** — all five run the safe direction.
- [ ] (todo) **The shared error envelope does not exist.** §113 documents
      `{error:{code,message,details[],traceId}}`; every path returns flat
      `{"error":"string"}`, and `Program.cs` registers neither
      `AddProblemDetails()` nor `UseExceptionHandler()`, so an unhandled
      exception is a bare 500. Most 404s have no body at all. The frontend
      parses exactly `{error?: string}`.
- [ ] (todo) **The `page`/`pageSize` pagination envelope is implemented
      nowhere.** Every list returns a bare array; only `/admin/audit-events`
      pages, and it uses `limit`/`offset` + `{total, items}`. `/alerts`
      truncates at 500 rows with no cursor and no total, so a busy tenant
      silently gets an incomplete list.
- [ ] (todo) **Seven endpoints are documented in *unmarked* sections and do not
      exist** — both password-reset routes, `DELETE /clients/{id}`,
      `DELETE /mailbox-sources/{id}`, `test-connection`, nested `sync-runs`, and
      `GET /admin/health` (the real probes are `/health/live` and
      `/health/ready`). `http/api.http` agrees with the code, so the doc is the
      outlier. Marking them planned, as §7/§11–13 already do, is enough.
- [ ] (todo) **§9 and §10 say "Not implemented" for the alert engine and the
      digest**, both of which ship — §0 says so. Only the specific paths they
      describe are absent. §16 also claims `agency_analyst` has "read/write
      operational endpoints, limited admin settings"; an analyst has exactly two
      write routes and zero `/admin/*` access.
- [ ] (todo) **§1 describes magic-link tokens as a current auth mechanism** (no
      token or bearer path exists; §12 and §16 concede this) and omits the real
      second mechanism, OIDC. §17 lists 202 and 429, which nothing returns, and
      omits 502 (two live endpoints) and 503 (`/health/ready`).
- [ ] (todo) **Undocumented validation:** the 10-character password minimum
      (nowhere in the contract), `clearAlertThresholds` as the only way to null
      an override, `kind ∈ {alert,digest,both}` with 409 on duplicate
      recipients, and the `mailbox-sync-runs` limit (default 50, clamped 1–200,
      documented as "default server value").

### ADRs

- [ ] (todo) **Four ADRs say "accepted" while describing decisions the code did
      not follow.** 0002's DB job queue and Kubernetes CronJob were never built;
      0006's structured JSON logging and OTEL pipeline are not wired
      (`backlog.md:177` already knows); 0004's dedicated migration Job is not
      the default in any shipped topology. 0003 and 0004 also carry no
      supersession pointer even though 0007 and 0008 say they supersede/extend
      them — a reader arriving at 0003 first has no signal to keep reading.
      Adding statuses and pointers is a small edit with a large payoff.
- [ ] (todo) **The ADR README index lists filenames only** — no titles, no
      statuses, so it cannot warn about supersession. 0004's filename
      (`deployment-compose-and-kubernetes`) also disagrees with its own title.
- [ ] (todo) **Ten architecturally significant decisions have no ADR.** In
      rough order of value: mailbox-credential encryption (single install-wide
      AES-256-GCM key, versioned `enc:v1:` envelope, **no rotation path** — the
      consequence lives only in deployment comments); alerting and digest design
      (report-relative evaluation, DB-row idempotency, SMTP-only, PDF deferred);
      Carter modules plus an API-hosted SPA in one image; the in-process
      scheduling loop; UTC-only date boundaries and the fact `client.Timezone`
      is stored, validated, exposed — and read by nothing; no rate limiting or
      login lockout on an unauthenticated endpoint; DNS policy caching;
      reverse-proxy trust ordering; the migration strategy split three ways; and
      the Apache-2.0 licence choice.
- [ ] (todo) **ADR 0005's routing decision is half-implemented, and the two
      halves can disagree.** Domain resolution honours ownership, but
      `MailboxSyncService.cs:200` writes the ingest ledger with the *receiving
      source's* `DefaultClientId` unconditionally — so the ledger (and the
      retention purge scope keyed off it) can attribute a report to a different
      client than `DmarcReport` does. Either resolve the owner before writing,
      or amend the ADR to say the ledger is per-source by design.

### Planning docs and entry points

- [ ] (todo) **`status.md`, `roadmap.md` and `backlog.md` disagree on nine
      features**, because each is stale at a different commit. `roadmap.md` is
      the worst offender: it marks the alert engine, the monthly digest, audit
      logging and the Helm chart as not started (all ship) and the DB job queue
      as done (it does not exist), and quotes "59 tests" where there are 214.
      `status.md` carries no date or commit anchor despite being the document
      `planning/README.md` tells readers to check first. Consider making
      `roadmap.md` milestone-shaped only and letting `backlog.md` own per-item
      status, rather than maintaining three inventories.
- [ ] (todo) **`.github/profile/README.md` advertises two features that do not
      exist** — threat detection "with sending IP, volume, and geography" (no
      geolocation anywhere; already tracked in the Parking Lot) and "white-label
      client reports … per domain" (the shipped digest is one unbranded email
      per client). This is the most public surface the project has.
- [ ] (todo) **Open-source hygiene files are missing across both repos:** no
      `SECURITY.md` (the disclosure policy exists only as website content, so
      GitHub's "Report a vulnerability" affordance is absent for a product that
      stores mailbox credentials), no `CONTRIBUTING.md`, no `CODE_OF_CONDUCT.md`,
      no issue or PR templates, no `CHANGELOG.md`, no licence statement in the
      README, and no licence at all on the website repo. Also: no documented way
      to seed a dev database — sample RUA XML exists at
      `src/api.tests/Fixtures/` and nothing tells a contributor it is there.
- [ ] (todo) **`src/web/README.md` is the unmodified Vite starter template**,
      while `AGENTS.md:19` links it as the authoritative frontend notes. Nothing
      in it describes this app — not the `/api` proxy, the `@` alias, the design
      tokens, or the lint gate.
- [ ] (todo) **`docs/ops/configuration.md` mis-states two required settings.**
      `Security__CredentialEncryptionKey` is listed as having "no usable
      default" — the app starts without it and stores mailbox passwords in
      *plaintext* with only a log warning (only Compose and Helm refuse); and
      `ConnectionStrings__Default` does have a default, so a typo'd variable
      name produces "cannot reach localhost:5432" rather than a clear failure.
      It also describes `Worker__MaxRetryAttempts` as dead-lettering a queued
      item — there is no queue and no dead-letter — and documents only one of
      roughly a dozen silent value clamps.
- [ ] (todo) **`docs/ops/mailbox-sync.md` omits four behaviours an operator
      needs mid-incident:** that dedup makes reprocessing safe and idempotent;
      that checkpointing means a manual sync re-fetches nothing already seen;
      that one bad password produces *three* failed rows per pass (retry ×
      backoff) and the source is never auto-deactivated; and how to force a full
      re-sync (there is no API — it is a SQL `UPDATE` clearing
      `LastProcessedUid`, safe because of dedup, bounded by
      `MaxMessagesPerSync`).
- [ ] (todo) **Replay reports from the bucket archive** (spec phase 8, the one
      phase of ADR 0009 not built). `Backup__ArchiveReportMail` stores the whole
      message gzipped at `reports/yyyy/MM/dd/<source>/<uidvalidity>-<uid>.eml.gz`,
      but nothing reads it back, so **the archive is currently evidence, not a
      restore path** — and the docs say so. Needs a bucket-sourced ingestion path
      (`POST /api/v1/admin/reports/replay?from=&to=`) walking the prefix and
      feeding each object through `ExtractXmlStreamsAsync`; safe to re-run because
      report dedup is idempotent. Also missing: a picker for the dated daily
      snapshots, so recovering from a bad `latest.json` still means fetching the
      dated copy by hand, and a console surface for
      `MailboxSource.DeleteAfterRetention` (today it is settable only in the
      database, and `mailbox-retention/preview` is the only way to see which
      sources have it on).
- [ ] (todo) **Missing runbooks:** a restore-from-backup *drill* — now scriptable
      end to end, since export → import into a throwaway instance → compare
      manifest counts → sync a mailbox source is the check that actually proves
      the encryption key round-tripped; encryption-key rotation (the consequence is
      documented, the procedure is not, and the code's legacy-passthrough makes
      a staged rotation genuinely possible); deployment rollback when a release
      ships a migration the previous image cannot read; and ingestion-backlog
      response, where nobody has written down that 200 messages/pass/hour means
      a 10,000-message backlog takes about two days.

## Low Priority

- [ ] (todo) Add export options for analytics (CSV and JSON).
- [ ] (todo) Add onboarding and deployment docs for local Docker-based development.
- [x] (done) Add optional OIDC support for external identity providers (hybrid handler + JIT provisioning; Zitadel tested, any OIDC provider via config).
- [ ] (todo) Add read-only client portal mode for selected clients.
- [ ] (todo) Add mailbox connectors for Microsoft 365 and Google Workspace APIs.
- [ ] (todo) Add forensic/failure (RUF) report ingestion and parsing (MVP is scoped to aggregate/RUA only; marketing site advertises "aggregate and forensic").
- [ ] (todo) **Purge expired `user_session` rows.** Session expiry is enforced
      lazily at read time (`AuthService.GetSessionUserAsync`) and nothing ever
      deletes the rows, so the table grows without bound — and each row carries
      the sign-in IP address and user agent, so this is a data-protection point
      as well as hygiene. The retention pass already runs daily and batches
      deletes; adding expired-session cleanup there is the natural shape. Until
      it exists, the docs site's monitoring page tells operators to prune by
      hand with SQL — this item retires that workaround.
- [ ] (todo) **An erasure path: delete a domain, offboard a client.** There is
      no `DELETE` endpoint for a domain or a client today; the only supported
      data removal is the retention window plus the purge. That leaves two real
      cases unserved: removing a single domain (and its reports) that was added
      by mistake or has left the portfolio, and offboarding a client entirely —
      which for an agency is a GDPR-adjacent obligation, not a nicety. The docs
      site's data-protection page currently documents the gap honestly
      ("faster-than-retention erasure means lowering the window and purging, or
      SQL"); shipping this turns that paragraph into a feature. Cascades already
      exist from `dmarc_report` down, and the audit trail should record the
      deletion rather than be deleted.

### MTA-STS and TLS-RPT (issue #115)

The arc that makes the app the one place an agency runs MTA-STS across client
domains: monitor it everywhere, host the policy files, and read the TLS-RPT
feedback that says whether enforcing would break anything. Sequenced; each
step is independently shippable.

- [x] (step 1, done 2026-08-06) **MTA-STS monitoring.** Worker pass +
      `mta_sts_state` — the `_mta-sts` TXT record (including the RFC 8461 rule
      that two records or bad syntax read as *no policy*), the policy fetch
      (the codebase's first outbound HTTP: no redirects, 64 KB bound, cert
      failures as findings, private-address egress guard), syntax, and the
      MX cross-check. Three alert rules read the persisted state; the Domain
      Detail "Transport security" card renders it, with a staff-only recheck.
- [x] (step 2, done 2026-08-06) **Hosted MTA-STS policies.** Per-domain policy
      rows (mode, max_age, mx patterns, server-bumped id) served anonymously
      at `/.well-known/mta-sts.txt` keyed on the Host header. A new
      `APP_MODE=mta-sts` runs an internet-facing policy-host container
      separate from the console; `/mta-sts/ask` gates Caddy's on-demand-TLS
      cert issuance; client-level bulk apply covers same-provider fleets;
      policies ride in the backup artifact with ids verbatim; ops doc with
      the reverse-proxy recipes (`docs/ops/mta-sts-hosting.md`). Onboarding a
      client domain is one CNAME plus one TXT record.
- [x] (step 3a, done 2026-08-06) **TLS-RPT ingestion and storage.** Typed
      extraction (zip entries content-classified; gzip'd non-reports keep
      their legacy route to the DMARC parser), a lenient System.Text.Json
      parser, four tables (`smtp_tls_report` with no DomainId — tenancy and
      windows live on the per-policy rows — failure details with a stored
      sts/dane/transport/other category, and the parallel `tls_report_ingest`
      ledger: the dedupe decision as resolved, `dmarc_report_ingest`
      untouched), sync-run counters end-to-end (which supersedes the
      skipped-TLS-counter side item below), retention passes with a
      legal-hold-safe orphan sweep, and backup exclusions + a ledger history
      stream. `ResolveOrCreateDomainIdAsync` hoisted to `DomainIngestResolver`
      as the backlog item anticipated.
- [x] (step 3b, done 2026-08-06) **TLS-RPT surface and rollout gate.** The
      per-domain TLS-RPT panel (sessions, failure categories, receiving MX)
      with its own window anchor, and the testing→enforce readiness gate:
      monitoring checks green + no STS-category failure sessions in 14
      wall-clock days, a 28-clean-day time-in-testing fallback for domains no
      reporter covers (the verdict names its basis), one-click promote through
      the audited hosted-policy PUT. **The arc is complete** — follow-ups live
      below as ordinary items (fleet-wide MTA-STS overview page; a TLS-RPT
      failure-spike alert rule; digest coverage for MTA-STS/TLS-RPT).

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
- [x] (done 2026-08-06) **Ingest and store SMTP TLS reports (TLS-RPT, RFC
      8460).** Scoped 2026-07-25; shipped as step 3a of the MTA-STS and
      TLS-RPT arc under Medium Priority — see that entry. The scoped notes
      below stand as the design record; the *surface* half continues as step
      3b. The skipped-TLS-report counter side item became real
      `TlsReportsInserted`/`TlsReportsSkippedAsDuplicate` counters, since
      ingestion landed with it.

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
