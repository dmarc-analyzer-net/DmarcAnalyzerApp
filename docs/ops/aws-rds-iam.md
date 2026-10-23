# Postgres on AWS without a database password

`Database__IamAuth=true` makes every process — console, worker, migration Job,
MTA-STS host — log in to Amazon RDS, Aurora or RDS Proxy with a short-lived IAM
authentication token instead of a stored password. Combined with the S3 report
source and backup offload, which use the same credential chain, an AWS
deployment then holds no database secret at all.

This page is the recipe. [`configuration.md`](./configuration.md) has the
one-line reference for the setting itself.

## How it works, in four sentences

A token is a signed request, valid for 15 minutes, generated locally from the
AWS credentials the SDK finds in the environment: an ECS task role, an EC2
instance profile, or an EKS service account with IRSA. The app generates a fresh
one each time Npgsql opens a physical connection, so the 15-minute limit never
matters and no refresh timer exists. Connections already open stay open; the
token is only checked at login. Because the token *is* a credential, IAM mode
also forces `sslmode=verify-full`, so a host impersonating the database cannot
collect one.

## What AWS needs

- The database user has the `rds_iam` role:
  `GRANT rds_iam TO dmarc;`
- The compute role may connect as that user:
  `rds-db:connect` on `arn:aws:rds-db:<region>:<account>:dbuser:<resource-id>/dmarc`.
  The resource id is the instance's `DbiResourceId` (or the cluster's
  `DbClusterResourceId`), not its name.
- IAM database authentication is enabled on the instance or cluster.
- **The region is the database's region.** The token is signed for whatever
  region the SDK resolves — `AWS_REGION`, `AWS_DEFAULT_REGION`, or the EC2
  instance metadata — not for the region in the hostname. ECS, Fargate and IRSA
  inject it and EC2 answers it from metadata, so it only needs setting by hand
  when the database lives in a different region from the compute. A token for
  the wrong region fails the login.
- **The host is the RDS endpoint**, not a CNAME of your own: the token is signed
  for the hostname, and so is the server certificate.

## Which CA to verify against

This is the one decision the app leaves to you, because two correct answers
exist and nothing in the connection string says which applies:

| Endpoint | Certificate issued by | What `verify-full` needs |
|---|---|---|
| RDS or Aurora instance / cluster endpoint | the Amazon RDS CA | the [RDS global bundle](https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem), mounted into the container and named in the connection string |
| RDS Proxy endpoint | AWS Certificate Manager (Amazon Trust Services) | nothing — the system trust store in the image already holds the Amazon roots |

Npgsql finds the CA the same way `psql` does, first match wins:

1. `Root Certificate=/path/to/bundle.pem` in the connection string (a
   `DATABASE_URL` carries only `sslmode`, so use the next one there);
2. the `PGSSLROOTCERT` environment variable;
3. `~/.postgresql/root.crt`, if the file exists;
4. the system trust store.

The app logs which one it will use, once, at startup:

```
Database IAM auth is on: per-connection RDS tokens for dmarc@db.cluster-abc.eu-west-1.rds.amazonaws.com:5432, server certificate verified against /etc/ssl/rds/global-bundle.pem (PGSSLROOTCERT).
```

If the first connection then fails with a certificate chain error, that line
says where the app looked; the usual cause is an RDS endpoint with no bundle,
or a bundle pointed at a proxy.

`sslmode=verify-ca` is accepted as-is. `require`, `prefer` and `allow` are
raised to `verify-full`; `disable` fails startup.

## Compose

```yaml
services:
  api:
    environment:
      Database__IamAuth: "true"
      ConnectionStrings__Default: "Host=db.cluster-abc.eu-west-1.rds.amazonaws.com;Database=dmarc_analyzer;Username=dmarc"
      PGSSLROOTCERT: /etc/ssl/rds/global-bundle.pem   # RDS endpoint; omit for RDS Proxy
      AWS_REGION: eu-west-1                            # only if the database is in another region
    volumes:
      - ./global-bundle.pem:/etc/ssl/rds/global-bundle.pem:ro
```

The same block applies to the `worker` service. Credentials come from the host:
an instance profile on EC2, or the task role on ECS, where `AWS_REGION` and the
container credential endpoint are already in the environment.

## Helm

The chart already has every hook this needs; nothing is AWS-specific in it.

```bash
kubectl -n dmarc create configmap rds-ca-bundle \
  --from-file=global-bundle.pem=global-bundle.pem   # RDS endpoint only
```

```yaml
# values.yaml
postgres:
  enabled: false
externalDatabase:
  # Password-less. Everything after the host is yours to add; the app only
  # requires Host and Username in IAM mode.
  connectionString: "Host=db.cluster-abc.eu-west-1.rds.amazonaws.com;Database=dmarc_analyzer;Username=dmarc"
auth:
  existingSecret: dmarc-creds        # the encryption key; db-password is unused
serviceAccount:
  annotations:
    eks.amazonaws.com/role-arn: arn:aws:iam::123456789012:role/dmarc-analyzer
extraEnv:
  Database__IamAuth: "true"
  PGSSLROOTCERT: /etc/ssl/rds/global-bundle.pem   # RDS endpoint; omit for RDS Proxy
# RDS endpoint only:
extraVolumes:
  - name: rds-ca
    configMap:
      name: rds-ca-bundle
extraVolumeMounts:
  - name: rds-ca
    mountPath: /etc/ssl/rds
    readOnly: true
```

The volumes and mounts land on the console, the worker and the migration Job
alike, so `migrations.strategy: job` keeps working. IRSA sets `AWS_REGION` on
the pods; nothing else about the region is needed on EKS.

## RDS Proxy specifics

- **Standard IAM authentication** at the proxy (the proxy itself holds a
  Secrets Manager password for the database) works without any change on the
  database side: no `rds_iam` grant, no IAM-enabled instance. The token is
  signed for the proxy hostname, which is what the connection string names.
- **No CA file.** Leave `PGSSLROOTCERT` and `Root Certificate` unset. Pointing
  them at the RDS bundle makes every connection fail verification, because the
  proxy's certificate chains to Amazon Trust Services, not to the RDS CA.
- **The worker's single-instance lock still holds.** It takes a session-level
  advisory lock, which the proxy answers by pinning that client session to one
  database connection for its lifetime — exactly the semantics the lock relies
  on.
- **Every connection will pin.** Npgsql resets a connection with `DISCARD ALL`
  when it returns to the pool, and the proxy pins a session on that statement,
  which removes most of the multiplexing a proxy exists for. Nothing breaks;
  the proxy just behaves like a plain pooler. `No Reset On Close=true` in the
  connection string avoids it, at the cost of session state surviving between
  pool uses — this app sets none, so that trade is usually fine.

## What still uses a password

`dotnet ef` at design time, through `DmarcAnalyzerDbContextFactory`, reads the
connection string directly and knows nothing about IAM. Migrations in a running
deployment go through `APP_MODE=migrate` or startup migration, both of which
use the token.
