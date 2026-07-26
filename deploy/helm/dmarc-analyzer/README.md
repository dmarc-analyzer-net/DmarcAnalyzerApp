# dmarc-analyzer Helm chart

Self-hosted DMARC report analysis on Kubernetes. The same image and the same
environment variables as the Compose deployment — see
[docs/ops/configuration.md](../../../docs/ops/configuration.md), which is the
single list of settings for both.

## Install

```bash
helm install dmarc ./deploy/helm/dmarc-analyzer \
  --namespace dmarc --create-namespace \
  --set auth.encryptionKey="$(openssl rand -base64 32)"
```

Then reach the console and create the first admin account:

```bash
kubectl -n dmarc port-forward svc/dmarc-dmarc-analyzer 8080:80
```

That default is a trial: bundled PostgreSQL with no backups, and the encryption
key in your shell history. See [Production](#production) before relying on it.

## The two choices

| | |
|---|---|
| `mode` | `combined` (default) runs the console and ingestion in one pod. `split` runs them as two Deployments. |
| `postgres.enabled` | `true` (default) bundles PostgreSQL. `false` uses `externalDatabase`. |

These are the same two axes the Compose files expose, so a deployment can move
between Compose and Kubernetes without relearning its configuration.

## Production

```bash
kubectl -n dmarc create secret generic dmarc-creds \
  --from-literal=encryption-key="$(openssl rand -base64 32)" \
  --from-literal=db-password='...'

helm install dmarc ./deploy/helm/dmarc-analyzer -n dmarc \
  --set auth.existingSecret=dmarc-creds \
  --set postgres.enabled=false \
  --set externalDatabase.host=db.internal \
  --set externalDatabase.username=dmarc \
  --set image.tag=0.1.0
```

Four things worth doing deliberately:

- **`auth.existingSecret`.** Otherwise the encryption key sits in your values
  file, your shell history, and `helm get values`.
- **Back up that key.** Lose or change it and every stored mailbox credential
  becomes undecryptable; each source has to be entered again. It is not
  recoverable from a database backup.
- **`postgres.enabled=false`.** The bundled StatefulSet has no replication, no
  backups and no pooling. It is there to make a trial one command.
- **Pin `image.tag`.** `latest` and `edge` make a rollback ambiguous.

## Migrations

`migrations.strategy` picks between a Job and applying them at startup. Left
empty it chooses `startup` with bundled PostgreSQL and `job` otherwise, and the
reason is ordering rather than preference: a pre-install hook runs before the
bundled StatefulSet exists, so on a first install a Job would sit waiting for a
database that is not there yet. With an external database there is nothing to
wait for and the Job is strictly better.

The Job runs `APP_MODE=migrate`, which applies pending migrations and exits.
Nothing is served and nothing is ingested, so it completes before any application
pod starts. Re-running it with no pending migrations logs *"No pending
migrations; nothing to do."* and succeeds, so an unchanged upgrade is a clean
no-op.

`activeDeadlineSeconds` defaults to 900. The largest shipped migration rewrites
about 5.3M rows in roughly 94 seconds, so the default has room, but a very large
database may want more.

## What the chart refuses

Some configurations would install successfully and then misbehave in a way that
is hard to attribute. The chart fails at template time instead, with the reason:

| Configuration | Why it is refused |
|---|---|
| `mode=combined` with `app.replicas > 1` | Every app pod runs its own ingestion loop, so N replicas are N schedulers claiming the same mailboxes — duplicate IMAP sessions and duplicate sync runs. Use `mode=split` to scale the console. |
| `worker.replicas != 1` | Whether two workers can claim from the queue concurrently is **unverified**. The chart will not imply a guarantee the queue may not make. |
| `migrations.strategy=startup` with `app.replicas > 1` | Replicas race to apply the same migration. |
| `postgres.enabled=false` with no `externalDatabase.host` | Nowhere to connect. |
| No `auth.encryptionKey` and no `auth.existingSecret` | Mailbox credentials would be stored in plaintext. |

## Scaling

`app.replicas` scales the console freely in `mode=split`. The worker is pinned to
one replica; that is a deliberate limit and not a property of Kubernetes.

## Notes on the internals

- **The Secret and ServiceAccount are `pre-install` hooks.** Hooks run before
  ordinary resources, so the migration Job would otherwise be rejected with
  "error looking up service account" and never create a pod. Both use
  `before-hook-creation` so they persist for the ordinary Deployments to use.
  Consequence: `helm uninstall` leaves them behind.
- **An init container waits for the database.** Kubernetes has no equivalent of
  Compose's `depends_on: condition: service_healthy`, so without it the app
  starts first, fails with "Name or service not known", and restarts until the
  database is up. It converges either way, but the restarts look like a
  crash-loop and the backoff can turn a 20-second wait into minutes. Skipped when
  `externalDatabase.connectionString` is used, since the host is not known
  separately.
- **The worker Deployment uses `Recreate`.** A rolling update would briefly run
  the old and new worker together — the exact overlap the replica pin exists to
  avoid.
- **The worker has no probes.** It serves no HTTP. Its loop already catches every
  exception in a pass and retries with backoff, so a failing pass degrades
  ingestion rather than crash-looping the pod.
- **`readOnlyRootFilesystem` is on**, with an `emptyDir` at `/tmp` because .NET
  needs somewhere to write.
