# Configuration

Every setting is an environment variable, and the same variable means the same
thing in every deployment shape — the Compose quick start, either overlay, and
the Kubernetes chart. There is no per-platform configuration format to learn.

`ConfigurationContractTests` fails the build if a setting exists in code but is
missing here, or is listed here but no longer exists. So this page is checked,
not merely maintained.

## How the names work

ASP.NET reads configuration from nested sections. In an environment variable the
nesting is a **double underscore**:

| Setting | Environment variable |
|---|---|
| `Worker:ScheduleIntervalSeconds` | `Worker__ScheduleIntervalSeconds` |
| `Auth:Oidc:ClientId` | `Auth__Oidc__ClientId` |

A single underscore will not work, and nothing will warn you — the value is
simply ignored and the default applies. If a setting seems to have no effect,
check the underscores first.

List values take an index: `Network__TrustedNetworks__0`,
`Network__TrustedNetworks__1`, and so on.

## Required

Two settings have no usable default.

| Variable | Notes |
|---|---|
| `Security__CredentialEncryptionKey` | AES-256-GCM key protecting stored mailbox passwords. Generate with `openssl rand -base64 32`. **Back it up.** Lose or change it and every stored mailbox credential becomes undecryptable; each source has to be entered again. |
| `ConnectionStrings__Default` | Npgsql connection string. The Compose files build this from `DMARC_DB_*`; set it directly if you prefer. |
| `DATABASE_URL` | A `postgres://user:pass@host:port/database` URI, converted to Npgsql format internally. Takes priority over `ConnectionStrings__Default` when both are set. For platforms (Render, Heroku, Railway, ...) that hand you a managed database as a URI rather than an ADO.NET string. A `sslmode` query parameter is honored (`disable`, `allow`, `prefer`, `require`, `verify-ca`, `verify-full`). |

## Runtime shape

| Variable | Default | Meaning |
|---|---|---|
| `APP_MODE` | `api` | `api` (console + HTTP), `worker` (ingestion loop only), `all` (both in one process), `migrate` (apply pending migrations and exit), or `mta-sts` (only the public MTA-STS policy routes plus health probes — an internet-facing policy host separate from the console; see [mta-sts-hosting.md](./mta-sts-hosting.md)). Any other value fails startup rather than falling back. |
| `Database__MigrateOnStartup` | `false` | Apply pending EF migrations at boot. The Compose files set it true. On Kubernetes leave it false and use the migration Job — with more than one replica, startup migration races. |
| `ASPNETCORE_URLS` | `http://+:8080` | Set in the image; override only for an unusual port inside the container. |

Worker mode does **not** apply migrations, whatever this is set to — only the web
host reads it. That is why the split Compose overlay gates the worker on the app
being healthy.

## Ingestion (`Worker`)

Read in `worker` and `all` modes.

Exactly one worker may run against a database. The process takes a Postgres
advisory lock at startup and exits if another holds it, so the limit applies
however you deploy — the Helm chart's `worker.replicas` guard only covers
Kubernetes, and nothing in Compose prevents `--scale worker=2`. If a worker is
killed abruptly its lock is released once Postgres notices the dead connection,
which can take a couple of minutes; a replacement container will crash-loop until
then and its log says so.

| Variable | Default | Meaning |
|---|---|---|
| `Worker__ScheduleIntervalSeconds` | `3600` | Gap between polling passes. Floored at 15s. |
| `Worker__MaxMessagesPerSync` | `500` | Messages fetched per **batch**, not per pass. A pass keeps drawing batches until the mailbox is drained or the drain budget below runs out, committing its checkpoint between them — so this bounds what a crash mid-drain costs in re-fetching, not how much a pass can ingest overall. Memory does not scale with it either way — messages are fetched and released one at a time — and a 500-message batch measured 21s average, 28s worst, well inside the drain budget below. |
| `Worker__MailboxDrainBudgetMinutes` | `20` | How long one source may keep drawing batches before the pass moves on, so one large backlog cannot starve the other sources. At least one batch always runs, however tight this is. **Silently clamped to `SyncRunTimeoutMinutes - 1`** when set equal to or above it, with a warning logged: the timeout cancels the run and records it `partial`, whereas the budget is meant to stop the drain gracefully first. |
| `Worker__MaxReportEntryBytes` | `67108864` (64 MB) | Cap on a single decompressed report payload. A `rua=mailto:` address is published in DNS, so this decompressor's address is advertised to strangers by design; without a cap, anyone who can read that record decides how much memory the worker allocates. Far above any real aggregate report — it exists to stop a bomb, not to police size. The refusal names this setting in the log and counts as a parse failure. |
| `Worker__MaxReportAttachmentBytes` | `134217728` (128 MB) | Cap on everything one attachment expands to across all its entries. Not redundant with the per-entry cap: every payload in an attachment is extracted before any is parsed, so they are all resident at once, and many entries each just under the per-entry cap is the same attack with more steps. Breaching it discards the whole attachment, including payloads already extracted from it. |
| `Worker__MaxReportArchiveEntries` | `512` | Cap on archive entries examined per attachment, so an archive of millions of tiny members costs bounded work even when nothing inside it is large. Counted before the `.xml`/`.json` filter, because an attacker picks the names. Entries past the cap are ignored and a warning is logged; unlike the byte caps this truncates rather than refusing. |
| `Worker__MaxPushedReportRequestBytes` | `33554432` (32 MB) | Ceiling on a single POST to `/api/v1/reports`, before decompression. The mailbox path never needed one because a mail server already caps message size; an HTTP endpoint has no such upstream. Checked against `Content-Length` where it is declared, and enforced again while reading, because a chunked request declares nothing. Exceeding it is a 413 naming this setting. |
| `Worker__MailboxRetentionGraceDays` | `30` | Extra days on top of a client's retention window before report mail is deleted from the mailbox. Deliberately generous: this is the one pass that removes data the app does not own, and the margin is what stops a clock skew or a mid-incident retention change from destroying mail the database has not re-read. |
| `Worker__MailboxRetentionIntervalHours` | `24` | Gap between mailbox retention passes. Retention is measured in months, so daily is plenty. |
| `Worker__MaxRetryAttempts` | `3` | Attempts before a queued item is dead-lettered. |
| `Worker__RetryBaseDelaySeconds` | `2` | Base for exponential retry backoff. |
| `Worker__StaleRunTimeoutMinutes` | `90` | A sync run still marked running after this is closed as abandoned — recovers from a worker killed mid-pass. |
| `Worker__SyncRunTimeoutMinutes` | `30` | Ceiling on a single mailbox sync. |
| `Worker__RetentionEnabled` | `true` | Run the retention purge pass. |
| `Worker__RetentionIntervalHours` | `24` | Gap between purge passes. |
| `Worker__RetentionBatchSize` | `500` | Rows deleted per purge batch. Smaller batches hold locks for less time. |
| `Worker__EnforceSingleInstance` | `true` | Refuse to start when another worker already holds the ingestion lock (a Postgres advisory lock). Two loops duplicate every sync pass, inflate the sync-run counts, and can send duplicate alert and digest email. Turning this off removes the only guard that works on every platform. |

## Retention (`Retention`)

| Variable | Default | Meaning |
|---|---|---|
| `Retention__AuditRetentionDays` | `730` | Age at which audit entries are purged. Per-client report retention is set in the console, not here. |

## Backup offload (`Backup`)

Ships the configuration artifact — clients, domains, mailbox sources, recipients, users
and grants — to S3-compatible object storage on a schedule. Report data is deliberately
not included: it arrived over IMAP and can arrive again, and it outweighs the rest by
roughly four orders of magnitude.

**`Backup__Bucket` empty disables the whole feature**, the same way an empty `Email__Host`
makes alerts and digests inert. The manual export endpoint
(`GET /api/v1/admin/config/export`) works regardless.

Two things are worth knowing before you turn this on:

- **The artifact is a credential file.** It carries `enc:v1:` mailbox ciphertext and
  PBKDF2 password hashes. The bucket must be private, and
  `Security__CredentialEncryptionKey` must **never** be stored in the same bucket — the
  pair together is the thing that exposes your mailbox passwords. The manifest carries a
  key *fingerprint*, not the key.
- **Offload refuses to run with no encryption key configured.** In that state the app
  stores mailbox passwords in plaintext, so the artifact would be a plaintext credential
  file. That is a failure, not a warning, and it is logged as one.

| Variable | Default | Meaning |
|---|---|---|
| `Backup__Bucket` | *(empty)* | Destination bucket. Empty disables offload. |
| `Backup__IntervalMinutes` | `30` | Gap between offload passes. **Effective resolution is `Worker__ScheduleIntervalSeconds`** — with the shipped hourly schedule, 30 here still means roughly hourly. Shorten the schedule interval too if the cadence matters. |
| `Backup__Endpoint` | *(empty)* | Custom S3 endpoint for MinIO, Cloudflare R2, Backblaze B2. Empty targets AWS. |
| `Backup__Region` | `us-east-1` | AWS region. Used only as the signing region when `Endpoint` is set. |
| `Backup__AccessKeyId` | *(empty)* | Static credential. Leave both key settings empty to use the ambient chain — an instance role or IRSA beats a long-lived key in configuration. |
| `Backup__SecretAccessKey` | *(empty)* | Paired with the above. Put it in a secret, not in `compose.yml`. |
| `Backup__Prefix` | `dmarc` | Key prefix, so one bucket can hold more than one install. |
| `Backup__ForcePathStyle` | `true` | Address the bucket as a path segment rather than a subdomain. Required by MinIO and most S3-compatible services; harmless on AWS. |
| `Backup__DailySnapshot` | `true` | Also write a dated copy of each snapshot. `config/latest.json` is overwritten every pass, so without either this or bucket versioning one bad write ends the only copy. |
| `Backup__IncludeHistory` | `true` | Ship the append-only tables (audit, alerts, digests, sync runs, ingest ledger) as immutable dated objects. These are the rows no report replay can reconstruct. |
| `Backup__HistoryOverlapMinutes` | `15` | Minutes of history re-shipped every pass. Deliberately not `0`: a row committed just after a pass read the clock would otherwise be skipped for good. Duplicates cost nothing because import de-duplicates on the primary key. |
| `Backup__ArchiveReportMail` | `false` | Archive raw report mail to the bucket as it is ingested, so report history survives independently of the mailbox. Off by default — it is the largest thing this feature can be asked to store, and it needs its own lifecycle rule. |

**Bucket versioning is strongly recommended.** `config/latest.json` is overwritten on every
pass, and versioning is what makes a bad overwrite recoverable. The app checks the bucket's
versioning state and warns when it is off, but does not refuse to run — several
S3-compatible backends report versioning inconsistently, and a backup that will not run is
worse than an unversioned one.

**Kubernetes.** The chart has no `backup:` block; its `values.schema.json` sets
`additionalProperties: false`, so use the existing escape hatches — `extraEnv` for the
non-secret settings and `extraEnvFromSecret` for `Backup__SecretAccessKey`.

## Email (`Email`)

Needed for alerts and digests. With `Email__Host` empty, nothing is sent and both
features are inert regardless of their own settings.

| Variable | Default | Meaning |
|---|---|---|
| `Email__Host` | *(empty)* | SMTP host. Empty disables outbound mail entirely. |
| `Email__Port` | `587` | SMTP port. |
| `Email__UseStartTls` | `true` | Upgrade the connection with STARTTLS. |
| `Email__Username` | *(empty)* | SMTP username; empty means unauthenticated. |
| `Email__Password` | *(empty)* | SMTP password. |
| `Email__FromAddress` | *(empty)* | Envelope sender. Required for mail to send. |
| `Email__FromName` | `DMARC Analyzer` | Display name on outbound mail. |
| `Email__ReplyToAddress` | *(empty)* | Optional Reply-To header. Empty means replies go to `Email__FromAddress` — useful when the sender is a no-reply transactional address (e.g. Scaleway TEM) but replies should land somewhere read. |
| `Email__BaseUrl` | *(empty)* | Public URL of this instance, used to build links in mail. Without it, links in alerts point nowhere useful. |

## Alerts (`Alerts`)

| Variable | Default | Meaning |
|---|---|---|
| `Alerts__Enabled` | `true` | Evaluate alert conditions each pass. |
| `Alerts__IntervalMinutes` | `60` | Gap between evaluations. |
| `Alerts__ComplianceDropPercent` | `15` | Percentage-point fall against the baseline that triggers an alert. |
| `Alerts__MinMessages` | `100` | Minimum message volume before a domain is considered — stops a handful of messages producing noise. |
| `Alerts__BaselineDays` | `7` | Window the current period is compared against. |
| `Alerts__CooldownHours` | `24` | Silence after an alert fires, per domain. |

## Monthly digest (`Digest`)

| Variable | Default | Meaning |
|---|---|---|
| `Digest__Enabled` | `true` | Send the monthly summary. |
| `Digest__DayOfMonth` | `1` | Day it is sent. |
| `Digest__CheckIntervalHours` | `6` | How often the worker checks whether it is due. |

## DNS policy checks (`Dns`)

| Variable | Default | Meaning |
|---|---|---|
| `Dns__Enabled` | `true` | Resolve each domain's published DMARC record so the console can show live policy. |
| `Dns__RefreshIntervalHours` | `6` | Gap between refreshes. |

## MTA-STS checks (`MtaSts`)

The worker checks each active domain's MTA-STS posture: the `_mta-sts` TXT
record, the policy file at `https://mta-sts.<domain>/.well-known/mta-sts.txt`,
and whether the policy's `mx` patterns cover the live MX records. Domains
without an MTA-STS record cost one TXT query per pass; only domains that
publish one get the HTTPS fetch and MX lookup on top.

| Variable | Default | Meaning |
|---|---|---|
| `MtaSts__Enabled` | `true` | Run the check pass and keep per-domain MTA-STS state fresh. |
| `MtaSts__CheckIntervalHours` | `6` | Gap between passes. |
| `MtaSts__FetchTimeoutSeconds` | `10` | Total budget for one policy-file fetch, connect included. |
| `MtaSts__MaxConcurrentChecks` | `4` | Domains checked concurrently during a pass. |
| `MtaSts__AllowPrivateNetworks` | `false` | Let the policy fetch connect to loopback/private/link-local addresses. Off because `mta-sts.<domain>` hostnames derive from operator-entered domains; turn on only for instances that monitor intranet mail domains. |
| `MtaSts__PolicyHost` | *(empty)* | The hostname client CNAMEs point at for hosted policies — shown as the CNAME target in the console's publish instructions. Empty shows a configure-me hint instead. See [mta-sts-hosting.md](./mta-sts-hosting.md). |
| `MtaSts__ServeCacheSeconds` | `60` | In-memory TTL and `Cache-Control: max-age` for served policy bodies; also how long a dedicated `mta-sts` container may serve a superseded body after a console edit. |

## Single sign-on (`Auth:Oidc`)

Off by default; local accounts work with no identity provider. See
[oidc-zitadel.md](./oidc-zitadel.md) for a worked example.

| Variable | Default | Meaning |
|---|---|---|
| `Auth__Oidc__Enabled` | `false` | Turn on the SSO front door. Local login stays available. |
| `Auth__Oidc__Authority` | *(empty)* | Issuer URL. |
| `Auth__Oidc__ClientId` | *(empty)* | Client id. |
| `Auth__Oidc__ClientSecret` | *(empty)* | Client secret. |
| `Auth__Oidc__Scopes__0` | `openid`, `profile`, `email` | Requested scopes, one indexed variable each. Setting any replaces the whole list. |
| `Auth__Oidc__DisplayName` | `SSO` | Label on the login button. |
| `Auth__Oidc__DefaultRole` | `client_viewer` | Role given to auto-provisioned users. The least-privileged role is deliberate. |
| `Auth__Oidc__AutoProvision` | `false` | Create an account on first successful SSO login. Off means users must exist already. |
| `Auth__Oidc__TrustUnverifiedEmail` | `false` | Allow linking a login to an existing local account when the provider asserts *nothing* about the address — neither `email_verified` nor `xms_edov`. Microsoft Entra ID never issues `email_verified`, which otherwise refuses every Entra user who already has an account. It does **not** override a provider that answers: an explicit `email_verified=false` is still refused. Leave it off unless the provider's addresses are administered — against a multi-tenant authority (`/common`, `/organizations`) any tenant can assert any address. Prefer the `xms_edov` optional claim, which needs no flag; see [oidc-entra.md](./oidc-entra.md). |
| `Auth__Oidc__RequireHttpsMetadata` | `true` | Only turn off against a local test provider over plain HTTP. |
| `Auth__Oidc__DisableLocalLogin` | `false` | Turn off password sign-in and redirect the login page straight to this provider. Requires `Enabled=true` — refused at startup otherwise, since that combination would leave no way to sign in. Registration is unaffected: it already refuses itself once the first account exists, so the first admin can still bootstrap locally before this is turned on. |

## Behind a reverse proxy (`Network`)

Off by default. Without it the audit trail records the proxy's address — on the
default Compose stack, Docker's bridge gateway for every entry.

| Variable | Default | Meaning |
|---|---|---|
| `Network__UseForwardedHeaders` | `false` | Believe `X-Forwarded-For` / `X-Forwarded-Proto`. |
| `Network__TrustedProxies__0` | *(empty)* | Proxy address whose headers are believed, e.g. `10.0.1.5`. |
| `Network__TrustedNetworks__0` | *(empty)* | Proxy network in CIDR form, e.g. `172.16.0.0/12`. Use for Docker or Kubernetes networks where the proxy address is not fixed. |
| `Network__ForwardLimit` | `1` | Proxy hops to walk back. Each extra hop is another address you are choosing to trust. |

**Turning this on with neither trust list set is refused**, with an error in the
log, and forwarded headers stay ignored. An empty trust list would let any caller
forge the address recorded against its own audit entries — worse than recording
the gateway honestly.

## Logging

| Variable | Default | Meaning |
|---|---|---|
| `Logging__LogLevel__Default` | `Information` | Standard ASP.NET logging configuration. |
| `Logging__LogLevel__Microsoft.AspNetCore` | `Warning` | Quietens framework request logging. |
| `AllowedHosts` | `*` | Host filtering. Usually left alone; a reverse proxy is the better place for this. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables the dev CORS policy for the Vite dev server. Never set it in a real deployment. |

## Telemetry (OpenTelemetry)

Off by default and free when off: with none of these set, the SDK is never
registered and nothing changes. Setting an endpoint is the single switch that
turns on traces, metrics and logs together.

These are the OpenTelemetry specification's own variable names, not settings of
ours, so whatever you already use for other services works here unchanged — and
the values can be pasted straight into any OTel tool. Everything past the table
(protocol, headers, timeouts, samplers, resource attributes) is read by the SDK
itself and behaves exactly as the spec says; we deliberately do not re-implement
or restrict it.

| Variable | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset)* | Collector address, e.g. `http://collector:4317`. Setting it enables all three signals. |
| `OTEL_TRACES_EXPORTER` | follows the endpoint | `otlp`, `console`, or `none`. `console` prints spans with no collector — useful for seeing where a slow request spends its time locally. |
| `OTEL_METRICS_EXPORTER` | follows the endpoint | Same values. |
| `OTEL_LOGS_EXPORTER` | follows the endpoint | Same values. Exported records include the formatted message and scopes, so a log arrives with its values rather than as a bare template. |
| `OTEL_SERVICE_NAME` | `dmarc-analyzer` | `service.name` on every signal. |
| `OTEL_SDK_DISABLED` | `false` | `true` turns everything off regardless of the above. |

An unrecognised exporter value falls back rather than failing startup: a typo in
a telemetry variable must not be why a mail-ingesting service refuses to boot.

What is instrumented:

- **Requests** — ASP.NET Core, one span per request. Probe paths are excluded, or
  they bury everything else: `/health/*`, and also `/api/v1/auth/setup`, which is
  the readiness target in both the Compose healthcheck and the chart. That one is
  a boolean "does an admin exist" check, so little is lost — but note the
  console's first-load call to it is not traced either.
- **PostgreSQL** — command spans from the Npgsql driver, plus its connection-pool
  and command-duration meters. Driver-level rather than EF-level on purpose: EF's
  own `Executed DbCommand` duration stops at the first row, so a query that
  streams for seconds can log milliseconds. That gap is how a 7.7s request once
  looked like 1s of SQL.
- **Outbound HTTP** — the `HttpClient` calls made during OIDC sign-in.
- **Runtime** — GC, thread pool and allocation meters.

Both halves of a split deployment report the same `service.name` and are told
apart by the `app.mode` resource attribute (`api`, `worker`, `all`, `migrate`),
so one trace can span the console and an ingestion pass.

## Compose convenience variables

These are not read by the application. The shipped Compose files use them to
build the settings above, so a single-host deployment can set five obvious things
in `.env` rather than assembling a connection string.

| Variable | Default | Used for |
|---|---|---|
| `DMARC_ENCRYPTION_KEY` | *(required)* | Becomes `Security__CredentialEncryptionKey`. |
| `DMARC_DB_HOST` | `postgres` | Part of `ConnectionStrings__Default`. |
| `DMARC_DB_PORT` | `5432` | ditto |
| `DMARC_DB_NAME` | `dmarc_analyzer` | ditto, and the bundled database's name |
| `DMARC_DB_USER` | `postgres` | ditto, and the bundled database's user |
| `DMARC_DB_PASSWORD` | `postgres` | ditto, and the bundled database's password |
| `POSTGRES_PASSWORD` | *(unset)* | Superseded by `DMARC_DB_PASSWORD`, still honoured so existing `.env` files keep working. |
| `DMARC_HTTP_PORT` | `8080` | Host port published for the console. |
| `COMPOSE_FILE` | *(unset)* | Records an overlay choice, e.g. `compose.yml:compose.split.yml`, so `docker compose up -d` keeps working unadorned. |
