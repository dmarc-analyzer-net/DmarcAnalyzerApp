# Migrating a running instance

Bringing an already-deployed instance onto a newer image, without losing data and
without assuming anything about what it is currently running.

The one thing to take away: **a green healthcheck does not prove the schema is
current.** The rest of this document is how to establish what is actually true and
apply the change safely.

Throughout, `<project>` is the Compose project name of the running stack and
`<app>` is the service running the web host — `app` in `deploy/compose.yml`, `api`
in the repo-root `docker-compose.yml`. Substitute your own.

## Start read-only: establish the current state

Nothing should be changed until three facts are known — which revision the checkout
is on, which migrations the database has, and how the stack is actually configured.

```bash
# Someone else may be working in this checkout.
git status

# Which migrations does the database have?
docker compose -p <project> exec -T postgres \
  psql -U postgres -d dmarc_analyzer -tAc \
  'select count(*), max("MigrationId") from "__EFMigrationsHistory"'
```

Compare that count against what is on the branch you are deploying:

```bash
ls src/api/Data/Migrations/*.cs | grep -vE 'Designer|Snapshot' | wc -l
```

Note the quoting: the columns are `"MigrationId"`, PascalCase and case-sensitive.
`select migration_id` fails.

**Always pass `-p <project>`.** Without it Compose derives the project name from the
current directory, so running from a worktree or a differently-named clone gives you
a new, empty database on colliding ports rather than the stack you meant to touch.
Derive the published port rather than hard-coding it:

```bash
docker compose -p <project> port <app> 8080
```

Also confirm the runtime configuration before assuming the defaults apply, since an
override file may change it:

```bash
docker compose -p <project> exec <app> env | \
  grep -E 'APP_MODE|ASPNETCORE_ENVIRONMENT|MigrateOnStartup|CredentialEncryptionKey'
```

`ASPNETCORE_ENVIRONMENT=Development` is worth catching here: it loads
`appsettings.Development.json`, which overrides the worker to a 15-second poll
interval and a smaller batch size, and it enables the dev CORS policy. See
[`configuration.md`](configuration.md).

## Back up before changing anything

```bash
docker compose -p <project> exec -T postgres \
  pg_dump -U postgres dmarc_analyzer | gzip > dmarc-backup-$(date +%F).sql.gz
gzip -t dmarc-backup-*.sql.gz && echo "backup readable"
```

The `-T` matters. Without it Compose allocates a TTY and the gzip stream is
corrupted in a way that only shows up when you try to restore.

`-U postgres` and the database name are the defaults; if `DMARC_DB_USER` or
`DMARC_DB_NAME` were set, substitute those. The database is not a complete backup on
its own — `Security__CredentialEncryptionKey` is needed to decrypt stored mailbox
passwords, so back up the environment file too.

This dump is still the right thing to take before a migration specifically: it is
the only artifact that can restore report data, which matters if the release must
be rolled back and the previous image cannot read the new schema. For everyday
backup, `GET /api/v1/admin/config/export` and continuous offload to object storage
(`Backup:*`, see [`configuration.md`](configuration.md#backup-offload-backup)) cover
configuration — clients, domains, mailbox sources, users — separately and far more
cheaply; see the [website's backup page](https://dmarc-analyzer.net/docs/upgrading-and-backup/)
for the full picture.

## Applying the migrations

Two ways. **Prefer the first.**

### The one-shot (recommended)

```bash
git pull
docker compose -p <project> build <app>
docker compose -p <project> run --rm -e APP_MODE=migrate <app>
```

`APP_MODE=migrate` applies pending migrations and exits. It does not serve, does not
ingest, and does not take the worker's lock, so **the running instance keeps serving
throughout**. It logs the pending migrations before applying them and finishes with
`Migrations applied.`; with nothing pending it logs `No pending migrations; nothing
to do.` and exits 0, so running it twice is safe.

Note that "keeps serving" refers to the application: the migrations themselves still
take table locks, and a release that rewrites a large table can block reads while it
runs. It also means the old code briefly runs against the new schema — fine for
additive migrations, not necessarily for others. Check the release notes.

Then restart so the running containers are on the new code:

```bash
docker compose -p <project> up -d --build <app> worker
```

### The restart path

```bash
git pull
docker compose -p <project> up -d --build <app> worker
```

The web host migrates as it boots, if `Database__MigrateOnStartup` is true. Worker
mode never applies migrations, whatever that setting says.

**The trap, and it is a real one:** `docker compose up -d` recreates a container only
when its *configuration or image* changes. A pending migration is a database fact and
Compose cannot see it. Verified on a replica: `up -d` with an unchanged image left the
container untouched — `RestartCount: 0`, no migration applied — while
`/api/v1/auth/setup` cheerfully returned **200**.

`--build` after a `git pull` normally produces a new image and therefore a new
container, so this usually works. If the build is a no-op it silently will not.
`--force-recreate` removes the doubt, and was verified to migrate correctly.

## Confirm rather than assume

Whichever path you took, re-run the count and check it moved:

```bash
docker compose -p <project> exec -T postgres \
  psql -U postgres -d dmarc_analyzer -tAc \
  'select count(*) from "__EFMigrationsHistory"'
```

## What to expect while it runs

- **A migration that rewrites report records does so in one statement.** One shipped
  migration took roughly 94 seconds per 5.3M rows on a database at that scale. All
  four migration paths allow ten minutes — `APP_MODE=migrate`, startup migration, the
  `POST /api/v1/admin/database/migrate` endpoint, and the `dotnet-ef` design-time
  factory.
- **On the restart path the API does not listen until migrations finish**, so expect
  a few minutes of unavailability. That is the migration, not a hang.
- **Each migration is its own transaction.** A failure rolls that migration back and
  leaves the ones before it applied. Confirmed by observing a timeout failure leave
  nothing half-written.
- **The worker takes a Postgres advisory lock at startup** and exits if another
  worker already holds it. A normal `up -d --force-recreate` releases and reacquires
  cleanly. But if a worker is killed with `-9`, the lock is held until Postgres
  notices the dead connection, so a replacement can crash-loop for a minute or two
  first — its log says so.

## Do not

- **Run the retention purge.** It is irreversible deletion of real reports. The
  preview (`GET /api/v1/admin/retention/preview`) is read-only and safe.
- **Delete clients, domains or mailbox sources** to test something.
- **Assume a bare `docker compose up` is safe.** On the repo-root
  `docker-compose.yml` it also starts the `zitadel` dev service and creates a second
  database inside the same Postgres. Scope it to the services you mean.

## Rolling back

There is no supported downgrade path. The runtime image ships no SDK and no
`dotnet-ef`, and there is no "migrate down" mode; `Down()` methods exist but run only
from a source checkout and several of them drop columns. If a release must be undone
after its migrations have applied, restore the backup.

## What has been verified

The behaviours above were dry-run against a local replica of a split api/worker
stack with `MigrateOnStartup: "true"`, using a genuinely pending migration created by
rolling the schema back one step:

| Verified | |
|---|---|
| `pg_dump ... \| gzip` | wrote a valid archive; `gzip -t` clean |
| the migration-count query | correct PascalCase quoting, returns count and newest id |
| `compose -p ... port <app> 8080` | returns the published mapping, so derive it rather than hard-coding |
| `run --rm -e APP_MODE=migrate <app>` | applied the pending migration, API kept serving, idempotent on re-run |
| `up -d` | **did not migrate** — container not recreated, health still 200 |
| `up -d --force-recreate` | migrated correctly; worker reacquired its lock |

What a replica cannot tell you is anything specific to the host you are about to
change: its override file, its actual migration count, its published port, or
whether someone has the repo checked out on a branch. The read-only commands at the
top of this document exist to establish those first.
