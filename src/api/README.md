# API Notes

Orientation for the backend. Anything that could go stale points at the file that
owns it rather than restating it.

## Runtime modes

`APP_MODE` selects the host: `api`, `worker`, `all`, or `migrate`. Any other value
fails startup rather than falling back, so a typo cannot leave you serving the
console while ingesting nothing. See
[`AppRuntimeMode`](Application/Hosting/AppRuntimeMode.cs) and the [configuration
reference](../../docs/ops/configuration.md).

## Migrations

**Migrations are applied on startup when `Database__MigrateOnStartup` is true**, and
both shipped Compose files set it, so a normal `docker compose up` migrates itself.
Worker mode never applies migrations whatever the setting says — only the web host
reads it.

The other three paths:

```bash
# a container that migrates and exits, leaving a running instance serving
docker compose run --rm -e APP_MODE=migrate app

# from a source checkout
dotnet-ef database update --project src/api/DmarcAnalyzer.Api.csproj \
  --startup-project src/api/DmarcAnalyzer.Api.csproj
```

…plus `POST /api/v1/admin/database/migrate`, which needs an `agency_admin` session.
All four allow ten minutes, because one shipped migration rewrites every report
record in a single statement.

For upgrading an already-deployed instance — and why a green healthcheck does not
prove the schema is current — see [migrating a running
instance](../../docs/ops/migrating-a-running-instance.md).

## Endpoints

There is no hand-maintained list here; the previous one covered 12 of the routes
that exist and had gone stale. The sources are:

- [`docs/planning/api-contract.md`](../../docs/planning/api-contract.md) §0 — every
  implemented route with the role it requires. Later sections of that document mix
  in planned endpoints and say so.
- [`Modules/`](Modules/) — the routes themselves, one Carter module per feature.
- [`http/api.http`](../../http/api.http) — a request collection for calling them by
  hand. It covers the CRUD and ingestion subset, not everything.

Two things worth knowing before calling anything: every `/api/v1/` path except
login, logout, register, setup, `auth/providers` and the OIDC routes requires a
`dmarc_session` cookie; and reads are scoped to the caller's granted clients, with
a cross-tenant id returning 404 rather than 403, deliberately.

## Layout

- `Modules/` — Carter modules; HTTP shape, auth metadata, status codes.
- `Application/` — the services they call, organised by feature.
- `Data/` — `DmarcAnalyzerDbContext`, entities, and EF migrations.
- `Workers/` — the ingestion loop and its single-instance lock.
- `Middleware/` — session and role enforcement.

Tests are in [`src/api.tests`](../api.tests) and run with `dotnet test
src/api.tests`. They use the EF in-memory provider, so anything depending on raw
SQL or real PostgreSQL behaviour needs a live database instead.
