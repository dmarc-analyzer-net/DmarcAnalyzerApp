# Upgrading PostgreSQL (18 → 19)

PostgreSQL cannot upgrade a data directory in place. Major versions use
incompatible on-disk catalogs, so 19 will not open a cluster that 18 created —
the data has to be **rewritten**, not moved. Changing the image tag is therefore
not an upgrade; it is half of one.

This is a documented maintenance window, not a risky operation: **a premature
attempt is fully reversible.** If you pull 19 before migrating, Postgres refuses
to start with

```
FATAL:  database files are incompatible with server
DETAIL: The data directory was initialized by PostgreSQL version 18,
        which is not compatible with this version 19.0.
```

and your data is untouched. Roll the tag back to `18-alpine` and you are running
again. Verified: a canary row written under 18 survives a failed 19 start and a
rollback, as it did for 17 → 18.

## Do you need to do this now?

No. The app runs on 18 and 19 alike: it uses nothing that is new in 19, and a
dump of a real instance restored into 19 served the same answers on every read
endpoint as the same dump on 18. PostgreSQL 18 is supported upstream until
November 2030. What changes is the default: the shipped compose files, the Helm
chart and the app-store packages now name 19, and the test suite runs against 19.

So you can either migrate now, or pin `postgres:18-alpine` in your own override
and migrate when it suits you. What you must not do is let the tag move under an
existing volume without the steps below — though if that happens, the error
above is all you will get.

A managed or external database (`compose.external-db.yml`,
`postgres.enabled=false` in the chart, Render, Railway) is upgraded by its
provider's process, not this one. Render's blueprint stays on 18: Render does not
offer 19 yet, and `postgresMajorVersion` cannot be changed after a database is
created.

## Before you start

- Roughly 5 minutes of downtime for a small instance. For scale: the 17 → 18
  migration of a 5.35-million-record instance made a 671 MB compressed dump and
  took 32 s to restore.
- Disk for the dump plus a second copy of the cluster, if you keep one.
- `PGDATA` is pinned in the shipped compose files. Leave it alone. It is what
  turns a mistimed upgrade into the loud failure above rather than a silent empty
  database. Since 18 the image defaults to `/var/lib/postgresql/<major>/docker`
  and declares its volume at `/var/lib/postgresql`, which is not where the
  shipped volume is mounted.

## The migration

Names below are for `deploy/compose.yml`; adjust the volume name for your setup.
These steps were run as written, from a stack on 18 holding a copy of a real
instance's data to the same stack on 19: identical counts, no restore errors and
no pending migrations.

**1. Record what you expect to end up with.** Exact counts, not estimates —
`pg_stat_user_tables.n_live_tup` is a stale approximation and will not match.

```bash
docker compose exec postgres psql -U postgres -d dmarc_analyzer -c \
  'SELECT count(*) FROM dmarc_report;  SELECT count(*) FROM dmarc_report_record;'
```

**2. Dump, while still on 18.**

```bash
docker compose exec -T postgres pg_dump -U postgres -d dmarc_analyzer -Fc -Z6 > dmarc-pre-19.dump
```

Wait for the file size to stop growing before trusting it, then check it is
readable:

```bash
docker compose exec -T postgres pg_restore -l < dmarc-pre-19.dump | grep -c 'TABLE DATA'
```

**3. Stop the stack.**

```bash
docker compose down
```

**4. Point Postgres at a new, empty volume and the 19 image.** Don't delete the
old volume. Keeping the 18 cluster is the whole rollback plan, and it costs only
disk:

```yaml
# compose override
services:
  postgres:
    image: postgres:19-alpine
    volumes: !override
      - dmarc-pgdata-19:/var/lib/postgresql/data
volumes:
  dmarc-pgdata-19:
```

If you have already pulled a release whose compose file names 19, the `image:`
line is redundant but harmless.

**5. Start only Postgres**, on 19, and let it initialise the empty cluster.

```bash
docker compose up -d postgres
docker compose exec postgres psql -U postgres -c 'SELECT version();'
```

**6. Restore.** Copy the dump into the container first — `pg_restore` refuses
`-j` from standard input (`parallel restore from standard input is not
supported`), and the parallel jobs are what make the index rebuilds quick.

```bash
docker compose cp dmarc-pre-19.dump postgres:/tmp/restore.dump
docker compose exec postgres pg_restore -U postgres -d dmarc_analyzer \
  --no-owner --no-privileges -j 4 /tmp/restore.dump
docker compose exec postgres rm /tmp/restore.dump
```

Expect zero errors. Anything else, stop and read it before continuing.

`podman-compose` has no `cp`. Use `podman cp dmarc-pre-19.dump <postgres container>:/tmp/restore.dump`
instead, with the container name from `podman ps`.

**7. Verify against step 1, with `ANALYZE` first** so the planner has fresh
statistics:

```bash
docker compose exec postgres psql -U postgres -d dmarc_analyzer -c 'ANALYZE;'
docker compose exec postgres psql -U postgres -d dmarc_analyzer -c \
  'SELECT count(*) FROM dmarc_report;  SELECT count(*) FROM dmarc_report_record;'
```

**8. Start the application.** It should report *no* pending migrations — the
restore brought the schema with it. `GET /health/ready` returning 200 means it is
talking to the new cluster.

**9. Keep the old volume** until a few ingestion passes have succeeded and the
numbers on the Dashboard look right. Then remove it.

## Rolling back

Point the volume back at the 18 cluster and set the image tag back to
`18-alpine`. Nothing written to the 19 cluster comes with you, so roll back
before the worker has ingested mail you care about, or re-ingest afterwards —
report inserts are idempotent, so a re-scan costs time and not correctness.

## pg_upgrade

`pg_upgrade --link` finishes in seconds regardless of size, but needs a container
carrying *both* major versions' binaries; the official images ship one each. For
a database of tens of gigabytes it is worth the trouble. Below that, dump and
restore is simpler and has fewer ways to go wrong. It has not been tested with
this app.

## Kubernetes

The chart pins `PGDATA` too, so a `postgres.image` bump fails the same loud way
with the PVC intact. To stay on 18 for now, set `postgres.image=postgres:18-alpine`.
The bundled StatefulSet is documented as evaluation-grade; if you run
`postgres.enabled=false` against a managed database, its upgrade is your
provider's process and none of the above applies.

## Earlier upgrades

The same procedure took the project from 17 to 18, with `17`/`18` in place of
`18`/`19`. That release is also where the image changed its default `PGDATA`,
which is why the shipped files pin it: without the pin, a bumped tag starts
cleanly on a new, empty cluster instead of failing.
