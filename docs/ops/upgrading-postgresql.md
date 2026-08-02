# Upgrading PostgreSQL (17 → 18)

PostgreSQL cannot upgrade a data directory in place. Major versions use
incompatible on-disk catalogs, so 18 will not open a cluster that 17 created —
the data has to be **rewritten**, not moved. Changing the image tag is therefore
not an upgrade; it is half of one.

This is a documented maintenance window, not a risky operation: **a premature
attempt is fully reversible.** If you pull 18 before migrating, Postgres refuses
to start with

```
FATAL:  database files are incompatible with server
DETAIL: The data directory was initialized by PostgreSQL version 17,
        which is not compatible with this version 18.4.
```

and your data is untouched. Roll the tag back to `17-alpine` and you are running
again. Verified: a canary row written under 17 survives a failed 18 start and a
rollback.

## Before you start

- Roughly 5 minutes of downtime for a small instance. The reference migration
  below moved 5.35 million records — 671 MB compressed dump, 32 s to restore.
- Disk for the dump plus a second copy of the cluster, if you keep one.
- `PGDATA` is pinned in the shipped compose files. Leave it alone. It is what
  turns a mistimed upgrade into the loud failure above rather than a silent empty
  database, because the 18 image changed its own default to
  `/var/lib/postgresql/18/docker` and moved its declared volume with it.

## The migration

Names below are for `deploy/compose.yml`; adjust the volume name for your setup.

**1. Record what you expect to end up with.** Exact counts, not estimates —
`pg_stat_user_tables.n_live_tup` is a stale approximation and will not match.

```bash
docker compose exec postgres psql -U postgres -d dmarc_analyzer -c \
  'SELECT count(*) FROM dmarc_report;  SELECT count(*) FROM dmarc_report_record;'
```

**2. Dump, while still on 17.**

```bash
docker compose exec -T postgres pg_dump -U postgres -d dmarc_analyzer -Fc -Z6 > dmarc-pre-18.dump
```

Wait for the file size to stop growing before trusting it, then check it is
readable:

```bash
docker compose exec -T postgres pg_restore -l < dmarc-pre-18.dump | grep -c 'TABLE DATA'
```

**3. Stop the stack.**

```bash
docker compose down
```

**4. Point Postgres at a new, empty volume** rather than deleting the old one.
Keeping the 17 cluster is the whole rollback plan, and it costs only disk:

```yaml
# compose override
services:
  postgres:
    volumes: !override
      - dmarc-pgdata-18:/var/lib/postgresql/data
volumes:
  dmarc-pgdata-18:
```

**5. Start only Postgres**, on 18, and let it initialise the empty cluster.

```bash
docker compose up -d postgres
docker compose exec postgres psql -U postgres -c 'SELECT version();'
```

**6. Restore.** Copy the dump into the container first — `pg_restore` refuses
`-j` from standard input (`parallel restore from standard input is not
supported`), and the parallel jobs are what make the index rebuilds quick.

```bash
docker compose cp dmarc-pre-18.dump postgres:/tmp/restore.dump
docker compose exec postgres pg_restore -U postgres -d dmarc_analyzer \
  --no-owner --no-privileges -j 4 /tmp/restore.dump
docker compose exec postgres rm /tmp/restore.dump
```

Expect zero errors. Anything else, stop and read it before continuing.

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

Point the volume back at the 17 cluster and set the image tag back to
`17-alpine`. Nothing written to the 18 cluster comes with you, so roll back
before the worker has ingested mail you care about, or re-ingest afterwards —
report inserts are idempotent, so a re-scan costs time and not correctness.

## pg_upgrade

`pg_upgrade --link` finishes in seconds regardless of size, but needs a container
carrying *both* major versions' binaries; the official images ship one each. For
a database of tens of gigabytes it is worth the trouble. Below that, dump and
restore is simpler and has fewer ways to go wrong.

## Kubernetes

The chart pins `PGDATA` too, so a `postgres.image` bump fails the same loud way
with the PVC intact. The bundled StatefulSet is documented as evaluation-grade;
if you run `postgres.enabled=false` against a managed database, its upgrade is
your provider's process and none of the above applies.
