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

| File | Effect |
|---|---|
| `compose.yml` | the app in `all` mode **and** bundled Postgres — complete on its own |
| `compose.external-db.yml` | removes Postgres and its `depends_on` |
| `compose.split.yml` | adds the worker service **and** sets `APP_MODE: api` on the app |

`COMPOSE_FILE` in `.env` records the choice so day-to-day use stays
`docker compose up -d`.

The base file being the complete quick start — rather than a skeleton that every
user has to overlay — matters for a reason outside the design: the raw URL of
`deploy/compose.yml` is published and in the wild. A `curl` of it has to keep
producing a working stack.

**This reverses the direction first sketched here.** The original plan had the
base file carry no database and an overlay add one, on the assumption that
Compose overrides can only add. They cannot only add: `!reset` (Compose v2.24+)
removes a key, and a service set to `!reset null` disappears from the project.
Verified before building on it. Subtraction being available is what allows the
default to be the useful stack instead of the minimal one.

**Profiles were considered and rejected for the topology axis.** They read
better, but they leave `APP_MODE=all` plus a worker container reachable — two
schedulers claiming the same mailboxes, duplicate IMAP sessions, two processes
racing to mark the same messages seen. Because the overlay flips the mode in the
same file that adds the worker, that state cannot be expressed. The database axis
has no equivalent footgun, but is done the same way for consistency.

The cost of `!reset` is a floor of Compose v2.24 (January 2024) for the overlays.
The base file has no such requirement.

### Kubernetes: a Helm chart

Values mirror the Compose axes one-for-one: `postgres.enabled` and
`mode: combined | split`. Templating earns its place here for the reasons plain
manifests struggle with — conditional resources, a values schema, and
upgrade/rollback as one operation — not because Compose lacks the same
expressiveness, which the `!reset` finding above shows it does not.

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

### Resolved: exactly one worker, enforced in the database

The open question here — whether two workers may run concurrently — has been
answered by reading the claim path. **There is no claim path.** The worker reads
every active mailbox source and iterates; there is no lease, no ownership column,
and no `FOR UPDATE`, `SKIP LOCKED` or advisory lock anywhere in the codebase.

Reports survive a second worker: every insert is `ON CONFLICT DO NOTHING` against
a real unique index, and the loser is detected by affected-row count rather than
an exception. What does not survive is everything around them — duplicate alert
email, a duplicate digest sent before the unique index rejects the second row,
`DbUpdateConcurrencyException` from the retention purge, and a checkpoint that can
move backwards. Every "is it due" gate is an in-memory field, so two processes
share no timer state at all.

So the pin stays, and it moves from the chart into the application:
`WorkerSingleInstanceLock` takes a Postgres advisory lock at startup and the
process exits if another holds it. That matters because the chart's
`worker.replicas` guard only ever covered Kubernetes — nothing in Compose prevents
`--scale worker=2`, or a worker container beside an `APP_MODE=all` one, and both
of those reach exactly the same failure. A lock in the shared database is the only
guard that covers every way of getting there.

The cost is a restart delay: an abruptly killed worker's lock is released only
once Postgres notices the dead connection. A replacement crash-loops until then
and its log says why.

`docs/planning/backlog.md` records what lifting the limit would require, in order
of how much each part buys.
