# DMARC Analyzer

[![CI](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dmarc-analyzer-net/DmarcAnalyzerApp?sort=semver&label=release)](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/releases/latest)
[![Docker pulls](https://img.shields.io/docker/pulls/dmarcanalyzernet/dmarc-analyzer?label=docker%20pulls)](https://hub.docker.com/r/dmarcanalyzernet/dmarc-analyzer)
[![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/dmarc-analyzer-net)](https://artifacthub.io/packages/helm/dmarc-analyzer-net/dmarc-analyzer)
[![License](https://img.shields.io/github/license/dmarc-analyzer-net/DmarcAnalyzerApp)](LICENSE.txt)

Open-source, self-hosted DMARC monitoring for agencies. Point it at the mailbox
your `rua=` reports arrive in and it collects, parses and charts them — unlimited
domains, many clients, no per-domain pricing, and the report data never leaves
your infrastructure.

**[Documentation](https://dmarc-analyzer.net/docs)** ·
**[Install guide](https://dmarc-analyzer.net/docs/install)** ·
**[Configuration](https://dmarc-analyzer.net/docs/configuration)** ·
**[Kubernetes](https://dmarc-analyzer.net/docs/kubernetes)**

Built with:

- ASP.NET Core + Carter (`src/api`)
- React + Vite (`src/web`)
- PostgreSQL (for local/dev and container deployments)

## Quick Start (prebuilt image)

Run the analyzer from the published image — no build, no account, your data
stays on your machine. Requires Docker.

```bash
mkdir dmarc-analyzer && cd dmarc-analyzer
curl -fsSL -o compose.yml https://raw.githubusercontent.com/dmarc-analyzer-net/DmarcAnalyzerApp/main/deploy/compose.yml
# generate the key that encrypts mailbox credentials at rest
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d
```

Then open **http://localhost:8080** and create the first admin account
(registration is locked after this first-run bootstrap).

What you get: the console on port 8080 and a background loop polling your
mailboxes for DMARC reports, both in one container (`APP_MODE=all`), plus
PostgreSQL. Two containers.

Two overlays sit next to that file if the defaults do not fit — they need
Compose v2.24 or newer:

| Instead of the bundled database or the single container | |
|---|---|
| Use a Postgres you already run | `-f compose.yml -f compose.external-db.yml` |
| Run the worker separately | `-f compose.yml -f compose.split.yml` |
| Both | add both `-f` flags |

Set `COMPOSE_FILE=compose.yml:compose.split.yml` in `.env` and day-to-day use
stays `docker compose up -d`. Every combination reads the same environment
variables.

- Image: `ghcr.io/dmarc-analyzer-net/dmarc-analyzer`, for `linux/amd64` +
  `linux/arm64`. Mirrored to Docker Hub as `dmarcanalyzernet/dmarc-analyzer`;
  GHCR is recommended because it has no anonymous pull rate limits.
- Tags: **`latest`** is the most recent release (what the compose file uses), or
  pin a version such as `0.5.0`. **`edge`** tracks `main` and is unreleased —
  useful for trying a fix early, not for production.
- Next steps: add a client, a domain, and a mailbox source (the inbox your
  `rua=` reports arrive in) — see `docs/ops/mailbox-sync.md`.
- Upgrading: `docker compose pull && docker compose up -d` (schema migrations
  run automatically on startup via `Database__MigrateOnStartup`).

## One-Click Deploy

| Provider | Deploy | Notes |
|---|---|---|
| **Render** | [![Deploy to Render](https://render.com/images/deploy-to-render-button.svg)](https://render.com/deploy?repo=https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp) | Provisions the app + a managed Postgres and wires them together automatically via `DATABASE_URL` — see `render.yaml`. |
| **Railway** | [![Deploy on Railway](https://railway.com/button.svg)](https://railway.com/deploy/dmarc-analyzernet) | Same shape as Render: a Postgres plugin plus the app image, wired together via `DATABASE_URL`. |
| **Coolify** | [![Deploy-Coolify](https://img.shields.io/badge/Deploy-Coolify-6B46C1?style=for-the-badge&logo=docker)](./deploy/compose.yml) | Import `deploy/compose.yml` directly — Coolify runs Compose files natively. |
| **Dokploy** | [![Deploy-Dokploy](https://img.shields.io/badge/Deploy-Dokploy-00B4D8?style=for-the-badge&logo=docker)](./deploy/compose.yml) | Same file — Dokploy also imports Compose services as-is. |

Coolify and Dokploy both need `DMARC_ENCRYPTION_KEY` and a Postgres
connection set the same way as the Quick Start above — see
`docs/ops/configuration.md` for every variable and where it applies.

<!-- TODO: Zeabur / Northflank one-click buttons need a template minted
under our own account on each platform's dashboard (their deploy URLs
embed an account-linked template ID, not a generic repo reference).
Create the templates, then replace this comment with the resulting badges. -->

## Repository Layout

- `src/api` - backend app (api / worker / all modes via `APP_MODE`)
- `src/web` - frontend app
- `docs/planning` - roadmap, backlog, architecture, API contract, and data model
- `docs/planning/adr` - architecture decision records

## Run Locally (Recommended for Development)

Run API and frontend directly for fast iteration and hot reload.

Prerequisites:

- .NET SDK 10
- Node.js 22+

Terminal 1 - API (hot reload):

```bash
APP_MODE=api dotnet watch --project src/api
```

Terminal 2 - Frontend (hot reload):

```bash
cd src/web
npm install
npm run dev
```

App URLs:

- Frontend: `http://localhost:5173`
- API status: `http://localhost:5076/api/v1/system/status`

Vite is configured to proxy `/api` to the local ASP.NET API in development.

## Run with Docker Compose

Build and run the single image in two modes (`api`, `worker`) plus PostgreSQL:

```bash
# once per clone — the key that encrypts mailbox credentials at rest is not in
# the repo, so compose refuses to start until you generate your own
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d --build
```

Keep that key once you have added a mailbox source: changing it makes every
stored mailbox password undecryptable.

Services:

- API: `http://localhost:8080`
- Postgres: `localhost:5432`
- Worker: same image, `APP_MODE=worker`

Stop:

```bash
docker compose down
```

## Single-Image Runtime Model

The same container image (`dmarc-analyzer-net:dev`) runs in three modes:

- `APP_MODE=api` - serves API + static React build (`wwwroot`)
- `APP_MODE=worker` - runs background worker host
- `APP_MODE=all` - both in one process; the simplest way to self-host on a
  single machine
- `APP_MODE=migrate` - applies pending migrations and exits; for orchestrators
  that need the schema settled before any app pod starts

Any other value fails startup rather than falling back to `api`, so a typo
cannot leave you with a container that serves the console and ingests nothing.

## Useful Commands

From repo root:

```bash
dotnet build DmarcAnalyzerApp.slnx
```

From `src/web`:

```bash
npm run build
```

## API Request File (.http)

Use `http/api.http` with VS Code REST Client or JetBrains HTTP client to run API requests quickly during development.

- File: `http/api.http`
- Default base URL: `http://localhost:5076`

## Key API Endpoints

Everything lives under `/api/v1`; full contract in
`docs/planning/api-contract.md`. A sample to get oriented:

- `GET /api/v1/system/status` — health/version
- `GET /api/v1/domains`, `GET /api/v1/clients` — the core resources
- `GET /api/v1/analytics/summary` — dashboard-level pass/fail totals
- `GET /api/v1/analytics/domains/{domainId}/sources` — per-domain sending sources
- `GET /api/v1/alerts` — configured alert rules
- `GET /api/v1/mailbox-health`, `GET /api/v1/mailbox-sync-runs`,
  `POST /api/v1/mailbox-sources/{id}/sync` — mailbox ingestion status/trigger

Try these against a running instance with `http/api.http` (see above).

## Mailbox Sync Monitoring

Key operational endpoints:

- `GET /api/v1/mailbox-health`
- `GET /api/v1/mailbox-sync-runs`
- `POST /api/v1/mailbox-sources/{id}/sync` (manual trigger)

Ops runbook:

- `docs/ops/configuration.md` — every environment variable, and the same set in
  every deployment shape. Kept true by `ConfigurationContractTests`.
- `docs/ops/migrating-a-running-instance.md` — bringing a running instance onto a
  newer image, including why a green healthcheck does not prove the schema is current.
- `docs/ops/upgrading-postgresql.md` — the PostgreSQL 17 → 18 migration. A major
  version cannot upgrade in place; this is the dump-and-restore window.
- `docs/ops/mailbox-sync.md`
- `docs/ops/oidc-zitadel.md`
- `docs/ops/release.md`

## Planning Docs

Start here before implementation details:

- `docs/planning/architecture.md`
- `docs/planning/api-contract.md`
- `docs/planning/data-model.md`
- `docs/planning/roadmap.md`
