# Handover: bring the live instance up to `main`

For a Claude Code session on the Omarchy PC. That machine holds the only instance
with real DMARC data, and it is not reachable from `hermes-agent`, so this was
written there and dry-run against a local replica of the same stack — same Compose
project name, same 8081 remap, same split api/worker topology.

**Read the "What was verified, and what wasn't" section at the end before
trusting any of it.**

## Start read-only: nothing here knows the current state

I do not know which migrations that database has. Earlier in this workstream I
asserted "three pending" and that number came from a throwaway rig seeded from an
old baseline, not from the live instance. Treat it as unknown and find out:

```bash
cd ~/dev/DmarcAnalyzerApp

# Someone else's Claude Code session may be working in here.
git status

# Which migrations does the live database have?
docker compose -p dmarcanalyzerapp exec -T postgres \
  psql -U postgres -d dmarc_analyzer -tAc \
  'select count(*), max("MigrationId") from "__EFMigrationsHistory"'
```

Compare that count against `ls src/api/Data/Migrations/*.cs | grep -vE 'Designer|Snapshot' | wc -l`
on `main`. At the time of writing `main` has 19.

Note the quoting: the columns are `"MigrationId"`, PascalCase and
case-sensitive. `select migration_id` fails.

**Do not pass `-p dmarcanalyzerapp` from a worktree directory and expect the live
stack** — actually the reverse: you *must* pass it. Without it the directory name
becomes the project name, and you get an empty database on colliding ports.

## Back up before changing anything

```bash
docker compose -p dmarcanalyzerapp exec -T postgres \
  pg_dump -U postgres dmarc_analyzer | gzip > ~/dmarc-backup-$(date +%F).sql.gz
gzip -t ~/dmarc-backup-*.sql.gz && echo "backup readable"
```

The `-T` matters. Without it Compose allocates a TTY and the gzip stream is
corrupted in a way that only shows up when you try to restore.

## Applying the migrations

Two ways. **Prefer the first.**

### The one-shot (recommended)

```bash
git pull
docker compose -p dmarcanalyzerapp build api
docker compose -p dmarcanalyzerapp run --rm -e APP_MODE=migrate api
```

`APP_MODE=migrate` applies pending migrations and exits. It does not serve, does
not ingest, and does not take the worker's lock, so **the running instance keeps
serving throughout**. It logs each migration by name and finishes with
`Migrations applied.`; with nothing pending it logs `No pending migrations;
nothing to do.` and exits 0, so running it twice is safe.

Verified on the replica: rolled the schema back one migration, ran this, watched
it apply exactly that one, confirmed the column returned and the API kept
answering 200 the whole time. Re-ran it and got the no-op.

Then restart so the running containers are on the new code:

```bash
docker compose -p dmarcanalyzerapp up -d --build api worker
```

### The restart path

```bash
git pull
docker compose -p dmarcanalyzerapp up -d --build api worker
```

The API migrates as it boots (`Database__MigrateOnStartup: "true"`).

**The trap, and it is a real one:** `docker compose up -d` recreates a container
only when its *configuration or image* changes. A pending migration is a database
fact and Compose cannot see it. On the replica, `up -d api worker` with an
unchanged image left the API container untouched — `RestartCount: 0`, no
migration applied — while `/api/v1/auth/setup` cheerfully returned **200**. A
green healthcheck does not mean the schema is current.

`--build` after a `git pull` normally produces a new image and therefore a new
container, so this usually works. If the build is a no-op it silently will not.
`--force-recreate` removes the doubt, and was verified to migrate correctly.

Either way, confirm rather than assume:

```bash
docker compose -p dmarcanalyzerapp exec -T postgres \
  psql -U postgres -d dmarc_analyzer -tAc 'select count(*) from "__EFMigrationsHistory"'
```

## What to expect while it runs

- **`AddDmarcReportRecordRangeBegin` rewrites every report record in one
  statement** — about 94 seconds per 5.3M rows measured on a copy of this
  database's scale. All three migration paths allow ten minutes.
- **The API does not listen until migrations finish** on the restart path, so
  expect a few minutes of unavailability. That is the migration, not a hang.
- **Each migration is its own transaction.** A failure rolls that migration back
  and leaves the ones before it applied. Confirmed by observing a timeout failure
  leave nothing half-written.

## New since this instance was last deployed

- **`APP_MODE=all`** runs console and ingestion in one container. The live stack is
  split api/worker and there is no reason to change it.
- **`APP_MODE=migrate`**, used above.
- **A single-worker lock.** The worker now takes a Postgres advisory lock at
  startup and *exits* if another worker holds it. The live stack has exactly one
  worker, so this changes nothing — verified that a normal
  `up -d --force-recreate api worker` releases and reacquires cleanly, with no
  refusal in the log.

  Two consequences worth knowing. Starting a second worker, or an `APP_MODE=all`
  container beside the existing worker, will now fail loudly instead of quietly
  double-syncing. And if the worker is killed with `-9`, its lock is held until
  Postgres notices the dead connection — a replacement can crash-loop for a
  minute or two first, and says so in its log.

- **The Compose files were restructured.** `deploy/compose.yml` is now a
  self-contained combined-mode stack with two overlays. **This does not affect the
  live instance**, which runs the repo-root `docker-compose.yml` plus its
  gitignored `docker-compose.override.yml`.

## Do not

- **Run the retention purge.** Irreversible deletion of real reports.
- **Delete real clients, domains or mailbox sources.**
- Assume `docker compose up` without a service list is safe — a bare `up` also
  starts the `zitadel` dev service and creates a second database inside the live
  Postgres. Scope it: `up -d api worker`.

Alerts and digests **cannot** send mail on this instance — `Email:Host` is empty
and `notification_recipient` is empty — so "Evaluate now" and digest preview are
safe. Earlier briefs warned against them on false grounds and lost coverage.

## What was verified, and what wasn't

Dry-run against a local replica (same project name, 8081 remap, split topology,
`MigrateOnStartup: "true"`), with a genuinely pending migration created by
rolling the schema back:

| Verified | |
|---|---|
| `pg_dump ... \| gzip` | wrote a valid archive; `gzip -t` clean |
| the migration-count query | correct PascalCase quoting, returns count and newest id |
| `compose -p ... port api 8080` | returns `0.0.0.0:8081`, so derive the port rather than hard-coding |
| `run --rm -e APP_MODE=migrate api` | applied the pending migration, API kept serving, idempotent on re-run |
| `up -d api worker` | **did not migrate** — container not recreated, health still 200 |
| `up -d --force-recreate api worker` | migrated correctly; worker reacquired its lock |

**Not verified, because the machine was unreachable:** anything specific to that
host. The gitignored `docker-compose.override.yml`, the actual current migration
count, whether 8081 is still the mapped port, and whether another session has the
repo checked out on a branch. The first three commands in this document exist to
establish those facts before anything is changed.
