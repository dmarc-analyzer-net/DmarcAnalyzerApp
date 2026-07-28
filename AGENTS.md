# AGENTS.md

Orientation for AI coding agents (and new contributors) working in **DmarcAnalyzerApp** — an agency-first, self-hosted DMARC analyzer (ASP.NET Core + Carter API, React + Vite frontend, PostgreSQL). Read this first, then follow the links into the detailed docs.

## What this project is

One agency workspace monitors DMARC aggregate (RUA) reports for many clients across many domains. Reports are pulled from mailboxes over IMAP, parsed, stored, and surfaced as compliance analytics with per-source drill-down. Multi-tenant (client-scoped), role-gated, and packaged as a single container.

- Product overview & local/Docker run: [`README.md`](README.md)
- Current implementation snapshot (what's actually built): [`docs/planning/status.md`](docs/planning/status.md)
- Prioritized work + recommended sequence: [`docs/planning/backlog.md`](docs/planning/backlog.md)

## Repository layout

- `src/api` — backend. One image and one entrypoint (`Program.cs`), running in the mode `APP_MODE` selects: `api` (REST API + the built React app from `wwwroot`), `worker` (mailbox-sync host), `all` (both in one process), or `migrate` (apply migrations and exit). Backend notes: [`src/api/README.md`](src/api/README.md).
  - `Application/` — service layer (Auth, Analytics, Clients, Domains, MailboxSources, Ingestion, Reports, Security, Users). Carter modules in `Modules/` are thin and delegate here.
  - `Data/` — EF Core `DmarcAnalyzerDbContext`, entities, and `Migrations/`. A design-time factory (`DmarcAnalyzerDbContextFactory`) lets `dotnet ef` run without building the web host.
  - `Middleware/` — `SessionAuthMiddleware` (cookie session → `ICurrentUserContext`) then `RoleAuthorizationMiddleware` (endpoint role enforcement).
- `src/web` — React 19 + Vite + TypeScript + Tailwind v3. Pages in `src/pages`, primitives in `src/components/ui` + `src/components/data`, shared helpers in `src/lib`. Frontend notes: [`src/web/README.md`](src/web/README.md).
- `src/api.tests` — xUnit tests (EF Core InMemory provider; note raw-SQL paths can't run under InMemory).
- `http/api.http` — REST Client request collection for manual API calls.
- `docs/` — see the doc map below.

## Build, test, run

```bash
# Backend build + tests (from repo root)
dotnet build DmarcAnalyzerApp.slnx       # or: dotnet build src/api/DmarcAnalyzer.Api.csproj
dotnet test src/api.tests

# Frontend (from src/web)
npm install
npm run build     # tsc -b && vite build   (must pass)
npm run lint      # eslint .                (must be clean)
npm run dev       # Vite dev server, proxies /api to the local API

# EF Core migrations (from repo root)
dotnet ef migrations add <Name> --project src/api/DmarcAnalyzer.Api.csproj

# Full dev stack in Docker (api + worker + postgres + dev Zitadel)
# Note: the repo-root compose file is the *development* stack and keeps api and
# worker separate. deploy/compose.yml is the shipped one and runs APP_MODE=all.
# Once per clone: the mailbox-credential encryption key is not committed, so
# compose refuses to start until .env has one. Keep it — changing the key makes
# stored mailbox passwords undecryptable.
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d --build
```

**Local dev URLs:** frontend `http://localhost:5173`, API `http://localhost:5076` (Vite proxies `/api`). **Docker:** API on `http://localhost:8080`, Postgres `localhost:5432`. (You can override host ports locally with a gitignored `docker-compose.override.yml`.)

## Architecture & conventions — where to read

- System architecture: [`docs/planning/architecture.md`](docs/planning/architecture.md)
- Data model (entities, keys, tenancy paths, retention): [`docs/planning/data-model.md`](docs/planning/data-model.md)
- API contract (implemented + target endpoints): [`docs/planning/api-contract.md`](docs/planning/api-contract.md)
- Milestone sequencing: [`docs/planning/roadmap.md`](docs/planning/roadmap.md)
- Planning decisions & product direction: [`docs/planning/README.md`](docs/planning/README.md)

### Architecture Decision Records — [`docs/planning/adr/`](docs/planning/adr/README.md)
1. [Tenant & domain ownership](docs/planning/adr/0001-tenant-and-domain-ownership.md)
2. [Ingestion & worker execution](docs/planning/adr/0002-ingestion-and-worker-execution.md)
3. [Authentication & client access](docs/planning/adr/0003-authentication-and-client-access.md)
4. [Deployment: Compose & Kubernetes](docs/planning/adr/0004-deployment-compose-and-kubernetes.md)
5. [Report routing, dedup & retention](docs/planning/adr/0005-report-routing-dedup-and-retention.md)
6. [Observability & operations baseline](docs/planning/adr/0006-observability-and-operations-baseline.md)
7. [Authorization & pluggable authentication](docs/planning/adr/0007-authorization-and-pluggable-authentication.md)
8. [Deployment topologies & config contract](docs/planning/adr/0008-deployment-topologies-and-config-contract.md)
9. [Configuration export & recovery](docs/planning/adr/0009-configuration-export-and-recovery.md)

### Operations runbooks — [`docs/ops/`](docs/ops/)
- [Directory listings](docs/ops/directory-listings.md) — Artifact Hub and awesome-selfhosted, and why the second is blocked on a category decision
- [Configuration reference](docs/ops/configuration.md) — **every** setting, and the same set on Compose and Kubernetes. `ConfigurationContractTests` fails the build if a setting exists in code and is missing here, so treat it as canonical; the website's configuration page links to it rather than copying it.
- [Cutting a release](docs/ops/release.md) — tag-driven; merging to `main` does not publish a release. A tag also publishes the Helm chart to `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer`.
- [Migrating a running instance](docs/ops/migrating-a-running-instance.md) — and why a green healthcheck does not prove the schema is current.
- [Mailbox sync operations](docs/ops/mailbox-sync.md)
- [OIDC login with Zitadel (dev setup)](docs/ops/oidc-zitadel.md)

## Key domain concepts (so you don't misread the code)

- **Tenancy**: `client` is the tenant root. `domain.ClientId` and `mailbox_source.DefaultClientId` are direct keys; reports/records derive tenancy transitively through the domain. Domain names are globally unique. New domains auto-create under the mailbox's default client.
- **AuthN is pluggable, authZ is always in-app** (ADR 0007). Local password or OIDC both mint the same `dmarc_session` cookie; roles + per-client grants are decided in the app, never by the IdP. Roles: `agency_admin` (all), `agency_analyst` (all clients, read + ops), `client_viewer` (granted clients only, read-only). Endpoints are **deny-by-default for client_viewer** — new endpoints must opt in via `.AllowClientViewer()`.
- **Enforcement status** (Domains/Detail): derived from the **effective** DMARC policy + compliance — `enforced` (p=reject) / `ramping` (p=quarantine) / `spoofing` (unprotected + failing) / `monitoring` / `no_data`. "Effective" matters: a subdomain publishing no record of its own inherits the organisational domain's `sp=` (else `p=`) via a DNS tree walk, so it can be `enforced` on a policy it does not publish. `domain.DnsPolicyInheritedFrom` records where that came from. A subdomain publishing its own weaker record still wins.
- **Analytics windows** anchor to the newest report date, not wall-clock (data is often backfilled).
- **Exactly one ingestion worker per database.** There is no claim mechanism in the queue — `QueueWorkerService` reads every active source and iterates. Reports survive a second worker (`ON CONFLICT DO NOTHING` on real unique indexes), but alert and digest email duplicate, the retention purge throws, and the mailbox checkpoint can move *backwards*. `WorkerSingleInstanceLock` takes a Postgres advisory lock so a second worker exits instead of starting. Lifting the limit is scoped in the backlog; do not assume it is only a Kubernetes concern.

## Configuration (env vars / appsettings)

- `APP_MODE` — `api`, `worker`, `all` (both in one process — what the shipped compose file uses), or `migrate` (apply pending migrations and exit). **Any other value fails startup** rather than defaulting, because a typo that serves the console while ingesting nothing passes every check an operator makes.
- `ConnectionStrings__Default` — Postgres connection.
- `Database__MigrateOnStartup` — `true` applies EF migrations on API start (enabled in compose).
- `Security__CredentialEncryptionKey` — base64 32-byte key; AES-256-GCM at rest for mailbox passwords. Absent ⇒ plaintext passthrough + startup warning (dev only).
- `Auth__Oidc__*` — optional OIDC front door (`Enabled`, `Authority`, `ClientId`, `ClientSecret`, `Scopes`, `DisplayName`, `DefaultRole`, `AutoProvision`, `RequireHttpsMetadata`). Off by default. See the [Zitadel guide](docs/ops/oidc-zitadel.md).
- `Worker__*` — polling interval, batch sizes, retry/timeout controls, and `EnforceSingleInstance`.
- `OTEL_*` — OpenTelemetry, using the specification's own variable names rather than settings of ours. Off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set (or an exporter is set to `console`); with none set the SDK is never registered. Traces cover ASP.NET Core, **Npgsql at the driver level**, outbound `HttpClient` and runtime meters. Full table in [`docs/ops/configuration.md`](docs/ops/configuration.md).
- `Network__*` — forwarded headers behind a proxy. Off by default, and **refused if turned on with an empty trust list**; without it every audit entry records the proxy's address.

The full list with defaults is in [`docs/ops/configuration.md`](docs/ops/configuration.md), which is build-enforced. Add a setting there in the same PR that adds it to the code, or the build fails.

## Working agreements

- **`main` is protected**: no direct pushes. Branch → implement → verify → open a PR (`gh pr create`). Merges happen via PR.
- **Verify before shipping**: build + tests + lint, and for user-facing changes run the stack (`docker compose up -d --build`) and check the real app. `docs/planning/status.md` and `backlog.md` should be updated as part of feature PRs.
- **Backend**: modules stay thin; put logic in `Application/` services. Prefer EF LINQ; when a query needs raw SQL (e.g. `DISTINCT ON`, per-group aggregates), keep it tenant-scoped and remember InMemory tests can't execute it.
- **Ingestion changes need a real database.** Every test uses `UseInMemoryDatabase`, which supports neither the raw SQL nor the transactions `MailboxSyncService` depends on — two real bugs there were invisible to the suite and had to be found by hand against Postgres. Until the integration harness in the backlog exists, verify changes to that file against a real database and say so in the PR.
- **Frontend**: TypeScript strict, sentence-case copy, no emoji, mono font for technical values (domains, IPs, policies). Use the design tokens (CSS vars + Tailwind theme) and existing primitives — the old shadcn light-blue tokens are gone.
- **Tone/content**: plain, technical, no hype (see the design/content notes referenced from the planning docs).

## Quick task pointers

- Add/adjust an endpoint → Carter module in `src/api/Modules/` + service in `src/api/Application/**`, stamp role metadata, add the type to `src/web/src/lib/*.ts`, consume in a page.
- Change the schema → edit the entity + `DmarcAnalyzerDbContext` mapping, then `dotnet ef migrations add`.
- Ingestion/parsing changes → `src/api/Application/Ingestion/MailboxSyncService.cs` + `src/api/Application/Reports/DmarcRuaReportParser.cs`; see [`docs/ops/mailbox-sync.md`](docs/ops/mailbox-sync.md).
