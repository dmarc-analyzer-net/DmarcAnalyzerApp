# ADR 0008: Deployment Topologies and the Configuration Contract

- Status: accepted
- Date: 2026-07-26
- Extends ADR 0004 (keeps its single-image decision, adds a third runtime mode
  and the packaging that surrounds it)

## Context

ADR 0004 settled the important part — one image, runtime mode chosen by
environment — and left the packaging as a follow-up: *"provide reference Compose
and K8s manifests/Helm templates."* Since then the repo has grown two Compose
files (a dev stack and a quick-start) and no Kubernetes story at all.

Self-hosters want to vary three things independently:

| Axis | Options |
|---|---|
| Database | bundled Postgres, or point at an existing one |
| Topology | one container doing everything, or API and worker split |
| Platform | Docker Compose, or Kubernetes |

Written out naively that is eight artifacts to keep in step. Deployment docs rot
exactly there: a variable gets added to the one file the author was testing, and
the other seven drift quietly until someone hits it in production.

Two facts make this cheaper than it looks:

- **The API host already registers every service the worker needs** —
  `IMailboxSyncService`, alerts, digest, retention, the DNS cache, `WorkerOptions`.
  Worker mode differs by one line, `AddHostedService<QueueWorkerService>()`.
- **Configuration is already platform-neutral.** ASP.NET's `Section__Key`
  environment binding works identically under Compose and Kubernetes, so no new
  abstraction is needed — only a written-down contract and something that keeps
  it honest.

One trap was investigated and dismissed. Worker mode registers `SystemUserContext`
(agency-staff, `CanAccessClient => true`) where API mode registers the
HTTP-scoped `CurrentUserContext`, so a combined process would hand worker scopes
the HTTP one. That would matter if worker-path services made authorization
decisions — they do not. No worker-path service consumes `ICurrentUserContext`;
the only consumer is `AuditLog`, which branches on `IsAuthenticated`, and that is
`false` under both. The elevated flags on `SystemUserContext` are never
exercised.

## Decision

### A third runtime mode

`APP_MODE` accepts `api`, `worker`, and `all`. In `all` the web host also
registers the worker's hosted service; there is no second process, no second
image, and no separate configuration path.

An unrecognised `APP_MODE` is a startup failure rather than a silent fall back to
`api`. A typo that quietly produces a machine which serves traffic but ingests
nothing is the worst available outcome.

### Compose: overlays, not a file per combination

Three files compose into four topologies:

| File | Adds |
|---|---|
| `compose.yml` | the app in `all` mode, external database |
| `compose.postgres.yml` | Postgres service, volume, healthcheck |
| `compose.split.yml` | the worker service **and** `APP_MODE: api` on the app |

`COMPOSE_FILE` in `.env` records the choice so day-to-day use stays
`docker compose up -d`.

**Profiles were considered and rejected.** They read better, but they leave
`APP_MODE=all` plus a worker container reachable — two schedulers claiming the
same mailboxes. Because the overlay flips the mode in the same file that adds the
worker, that state cannot be expressed.

### Kubernetes: a Helm chart

The bundled-database axis is a conditional *resource*, which is what Compose
cannot express and Helm can. Values mirror the Compose axes one-for-one:
`postgres.enabled` and `mode: combined | split`.

Bundled Postgres is a minimal in-chart StatefulSet, not a Bitnami dependency —
their distribution terms changed in 2025 and a self-hosted product should not
inherit that. It is documented as evaluation-grade, pointing production users at
an operator or a managed instance.

Migrations run as a pre-install/pre-upgrade `Job` with `MigrateOnStartup=false`,
as ADR 0004 anticipated. With more than one replica, startup migration races.

### The configuration contract

The environment variables are the contract, documented once in
`docs/ops/configuration.md` and consumed unchanged by both platforms. Helm
exposes typed values for the dozen that matter (database, mode, encryption key,
OIDC, email) plus `extraEnv` for the rest, so the mapping that has to be
maintained stays small.

**A CI check asserts every key in `appsettings.json` is either documented in the
contract or on an explicit exclusion list.** This is the load-bearing part of the
decision. Without it the contract is accurate the day it is written and slowly
stops being true; with it, adding a setting and forgetting to document it fails
the build.

## Consequences

### Positive

- Four Compose topologies from three files, and the broken one is unrepresentable.
- Combined mode is the obvious default for a single-host self-hoster: one
  container, one log stream, no inter-service healthcheck gate.
- The drift check turns "the settings are the same everywhere" from a claim into
  something enforced.

### Negative

- Three Compose files instead of one is more to explain, and multi-`-f`
  invocations are unfamiliar to some users. Mitigated by `COMPOSE_FILE`.
- A Helm chart is a new artifact to version and publish alongside the image.
- Combined mode gives up independent scaling: a heavy ingestion pass and the UI
  now compete for the same CPU and memory limit. It is the right default for small
  deployments and the wrong one above that, and the docs must say so rather than
  presenting the two as equivalent.
- Sharing a process is less dangerous than it first appears. `QueueWorkerService`
  catches every exception inside its loop precisely so a bad pass cannot stop the
  host, so a failing worker degrades ingestion without taking the console down.
  That was written for worker mode; combined mode inherits it.

### Open

- **Whether two workers may run concurrently** is unverified. It decides whether
  the chart permits `worker.replicas > 1` or pins it to 1. Settle it before
  publishing the chart rather than implying safety the queue may not provide.
