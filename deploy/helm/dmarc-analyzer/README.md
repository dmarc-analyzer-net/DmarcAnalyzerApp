# dmarc-analyzer Helm chart

Self-hosted DMARC report analysis on Kubernetes. The same image and the same
environment variables as the Compose deployment — see
[docs/ops/configuration.md](../../../docs/ops/configuration.md), which is the
single list of settings for both.

## Install

```bash
helm install dmarc oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer \
  --version 0.11.1 \
  --namespace dmarc --create-namespace \
  --set auth.encryptionKey="$(openssl rand -base64 32)"
```

Or from a clone, which is the same chart without the version pin:

```bash
helm install dmarc ./deploy/helm/dmarc-analyzer -n dmarc --create-namespace \
  --set auth.encryptionKey="$(openssl rand -base64 32)"
```

A published chart's `version` and `appVersion` are equal and come from the release
tag, so the chart version determines the default application version. Setting
`image.tag` or `image.digest` deliberately overrides that default.

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
  --set image.tag=0.11.1
```

Where policy calls for immutable images, pin the digest instead of the tag:
`--set-string image.digest=sha256:...` renders `repository@sha256:...` for
every container in the release. Use the digest published for the release you
intend to run — the chart refuses a shortened or malformed one. The two cannot
be set together, and leaving both empty follows the chart's `appVersion`.

Five things worth doing deliberately:

- **`auth.existingSecret`.** Otherwise the encryption key sits in your values
  file, your shell history, and `helm get values`.
- **Back up that key.** Lose or change it and every stored mailbox credential
  becomes undecryptable; each source has to be entered again. It is not
  recoverable from a database backup.
- **`postgres.enabled=false`.** The bundled StatefulSet has no replication, no
  backups and no pooling. It is there to make a trial one command.
- **Turn on backup offload.** The chart has no `backup:` block — set
  `Backup__Bucket` and friends via `extraEnv`, and `Backup__SecretAccessKey`
  via `extraEnvFromSecret` — but the feature is the same one Compose gets:
  a configuration export shipped to S3-compatible object storage, refused
  outright if the credential key above isn't set. See
  [`docs/ops/configuration.md`](../../../docs/ops/configuration.md#backup-offload-backup).
- **Pin `image.digest` for immutable deployments.** A tag is still convenient
  for evaluation, and an empty `image.tag` follows the chart's `appVersion`, but
  tags can be moved. The chart refuses a non-empty tag and digest together so
  the selected image is unambiguous.

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

## Values validation

`values.schema.json` is checked by Helm before install or upgrade, and Artifact
Hub renders it as a values reference. It catches the class of mistake templates
cannot see — a mistyped key, a string where a number belongs, a value outside the
allowed set — and it catches them before anything reaches the cluster:

```
Error: values don't meet the specifications of the schema(s) in the following chart(s):
dmarc-analyzer:
- at '/postgres': additional properties 'enable' not allowed
```

`auth.encryptionKey` is pattern-checked against what the application will actually
accept: base64 decoding to exactly 32 bytes. Getting that wrong used to surface as
a crash loop after install.

`image.digest` is likewise restricted to a complete lowercase SHA-256 digest
(`sha256:` plus 64 hexadecimal characters), so a shortened or malformed pin is
rejected before Kubernetes sees it.

The cross-field rules below stay in the templates, because they need to explain
themselves rather than just fail.

## What the chart refuses

Some configurations would install successfully and then misbehave in a way that
is hard to attribute. The chart fails at template time instead, with the reason:

| Configuration | Why it is refused |
|---|---|
| `mode=combined` with `app.replicas > 1` | Every app pod runs its own ingestion loop, so N replicas are N schedulers claiming the same mailboxes — duplicate IMAP sessions and duplicate sync runs. Use `mode=split` to scale the console. |
| `worker.replicas != 1` | There is no claim mechanism in the queue. Two loops duplicate every sync pass and can send duplicate alert and digest email. The application refuses it too, with a Postgres advisory lock, so extra replicas crash-loop. |
| `migrations.strategy=startup` with `app.replicas > 1` | Replicas race to apply the same migration. |
| `postgres.enabled=false` with no `externalDatabase.host` | Nowhere to connect. |
| No `auth.encryptionKey` and no `auth.existingSecret` | Mailbox credentials would be stored in plaintext. |
| Both `image.tag` and `image.digest` | A mutable tag and immutable digest name two competing image-selection strategies. Leave the tag empty when using a digest. |

## Scaling

`app.replicas` scales the console freely in `mode=split` — `APP_MODE=api` takes no
lock and runs no loop.

The worker is pinned to one replica. That is a property of the application, not of
Kubernetes: it holds a Postgres advisory lock for the life of the process and a
second worker exits rather than starting. `docs/planning/backlog.md` lists what
lifting the limit would take.

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
