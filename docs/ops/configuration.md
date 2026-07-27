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

## Runtime shape

| Variable | Default | Meaning |
|---|---|---|
| `APP_MODE` | `api` | `api` (console + HTTP), `worker` (ingestion loop only), `all` (both in one process), or `migrate` (apply pending migrations and exit). Any other value fails startup rather than falling back. |
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
| `Worker__MaxMessagesPerSync` | `500` | Messages fetched per mailbox per pass. Also the throughput ceiling: with the default hourly schedule this is 500 messages an hour, so a mailbox receiving more than that during a burst falls behind until it catches up. Memory does not scale with it — messages are fetched and released one at a time — and a 500-message pass measured 21s average, 28s worst, against a 30-minute run timeout. |
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
| `Auth__Oidc__RequireHttpsMetadata` | `true` | Only turn off against a local test provider over plain HTTP. |

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
