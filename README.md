# DmarcAnalyzerApp

Agency-first DMARC analyzer platform (inspired by tools like dmarcian/EasyDMARC) built with:

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

What you get: the API + console on port 8080, a background worker polling your
mailboxes for DMARC reports, and PostgreSQL — three containers, one image.

- Image: `ghcr.io/dmarc-analyzer-net/dmarc-analyzer` (`latest`, `sha-…`, or a
  version tag; `linux/amd64` + `linux/arm64`)
- Next steps: add a client, a domain, and a mailbox source (the inbox your
  `rua=` reports arrive in) — see `docs/ops/mailbox-sync.md`.
- Upgrading: `docker compose pull && docker compose up -d` (schema migrations
  run automatically on startup via `Database__MigrateOnStartup`).

## Repository Layout

- `src/api` - backend app (API mode + worker mode via `APP_MODE`)
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
docker compose up -d --build
```

Services:

- API: `http://localhost:8080`
- Postgres: `localhost:5432`
- Worker: same image, `APP_MODE=worker`

Stop:

```bash
docker compose down
```

## Single-Image Runtime Model

The same container image (`dmarc-analyzer-net:dev`) runs in two modes:

- `APP_MODE=api` - serves API + static React build (`wwwroot`)
- `APP_MODE=worker` - runs background worker host

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

## Mailbox Sync Monitoring

Key operational endpoints:

- `GET /api/v1/mailbox-health`
- `GET /api/v1/mailbox-sync-runs`
- `POST /api/v1/mailbox-sources/{id}/sync` (manual trigger)

Ops runbook:

- `docs/ops/mailbox-sync.md`

## Planning Docs

Start here before implementation details:

- `docs/planning/architecture.md`
- `docs/planning/api-contract.md`
- `docs/planning/data-model.md`
- `docs/planning/roadmap.md`
