# API Contract

API contract for `DmarcAnalyzerApp`.

**§0 is the authoritative list of what exists today**, generated from the Carter
modules in `src/api/Modules`. The numbered sections after it describe request and
response shapes and include **planned** endpoints that are not built yet — each
such section is marked. Where the two disagree, §0 and the code win.

> The dashboard section in particular was written before implementation and
> described a `/dashboard/*` namespace; what shipped is `/analytics/*`. It has
> been corrected, but treat unmarked detail in later sections as design intent.

## 0) Implemented endpoints

Auth model: all `/api/v1/*` routes require the `dmarc_session` cookie except the
public paths noted below. Role enforcement is `RoleAuthorizationMiddleware` +
route metadata, **deny-by-default**: an endpoint with no metadata requires agency
staff (`agency_admin` or `agency_analyst`).

- **staff** — default; admin + analyst.
- **admin** — `agency_admin` only (`RequireAgencyAdmin`).
- **any** — any authenticated role incl. `client_viewer` (`AllowClientViewer`);
  the service layer scopes the data by client grants.
- **public** — no session required.

### Auth and session
| Method | Path | Access |
|---|---|---|
| POST | `/auth/login` | public |
| POST | `/auth/register` | public — **first-run bootstrap only**, locked once a user exists |
| POST | `/auth/logout` | public |
| GET | `/auth/setup` | public — `{ requiresBootstrap }`; queries the DB, so a 200 also proves migrations ran (used as the container health probe) |
| GET | `/auth/providers` | public — which login front doors are enabled |
| GET | `/auth/oidc/login` | public (external-temp scheme) |
| GET | `/auth/oidc/complete` | public (external-temp scheme) |
| GET | `/auth/me` | any |

### Clients, domains, users
| Method | Path | Access |
|---|---|---|
| GET | `/clients` | staff |
| GET | `/clients/{id}` | staff |
| POST | `/clients` | admin |
| PATCH | `/clients/{id}` | admin |
| GET | `/domains` | staff |
| GET | `/domains/{id}` | staff |
| POST | `/domains` | admin |
| PATCH | `/domains/{id}` | admin |
| GET | `/users` | admin |
| POST | `/users` | admin |
| PATCH | `/users/{id}` | admin |
| PUT | `/users/{id}/grants` | admin — sets a viewer's client grants |

### Mailbox sources and sync
| Method | Path | Access |
|---|---|---|
| GET | `/mailbox-sources` | staff |
| POST | `/mailbox-sources` | admin |
| PATCH | `/mailbox-sources/{id}` | admin |
| POST | `/mailbox-sources/{id}/sync` | staff — manual trigger |
| GET | `/mailbox-health` | staff |
| GET | `/mailbox-sync-runs` | staff |

### Analytics
All accept `days` (relative window, default 30, clamped to 365). Windows anchor
to the **newest report** rather than wall-clock time, because backfilled
mailboxes lag. All are `any` — data is client-scoped in the service, and
cross-tenant ids return **404**, never 403.

| Method | Path | Purpose |
|---|---|---|
| GET | `/analytics/summary` | Compliance totals, daily trend, top failing domains, top reporters, dispositions, mailbox rollup (staff only for the mailbox block) |
| GET | `/analytics/domains` | Per-domain compliance, pass rates, **effective** DMARC policy (inherited from the organisational domain when the domain publishes none, with `dnsPolicyInheritedFrom` naming the source), enforcement status |
| GET | `/analytics/domains/{domainId}/drilldown` | Totals + trend for one domain |
| GET | `/analytics/domains/{domainId}/sources` | Per-source-IP aggregation, worst first |
| GET | `/analytics/domains/{domainId}/source-detail` | One source: evaluated DKIM×SPF combos, raw auth results, identifiers, reporters, trend. Requires `ip` (400 if missing) |
| GET | `/analytics/domains/{domainId}/enforcement` | Guided next policy step, rationale, `readyToAdvance`, blocking sources |
| GET | `/analytics/domains/{domainId}/records` | Live DNS DMARC/SPF records parsed tag-by-tag, compared against the observed `policy_published` |
| GET | `/analytics/threats` | Sources with fully unauthenticated volume across visible domains. Accepts `limit` (default 100, max 500) |
| GET | `/analytics/hostnames` | Best-effort reverse DNS. Requires `ips` (comma-separated, max 100) |

### System and admin
| Method | Path | Access |
|---|---|---|
| GET | `/system/status` | staff |
| POST | `/admin/database/migrate` | admin — applies pending EF migrations |
| GET | `/admin/audit-events` | admin — audit trail (`days`, `eventType` prefix, `actor`, `clientId`, `limit`). Read-only by design |
| GET | `/admin/retention/preview` | admin — what the next purge would delete, per client; deletes nothing |
| POST | `/admin/retention/purge` | admin — runs the purge now. Optional `batchSize` |
| GET | `/admin/config/export` | admin — the configuration artifact, as a JSON download. Refused with 409 when no credential encryption key is set, because the mailbox passwords in it would be plaintext; `allowPlaintextCredentials=true` overrides |
| GET | `/admin/config/import/preview` | admin — what an import would change; writes nothing |
| POST | `/admin/config/import` | admin — `mode` of `restore` (empty install only) or `merge`. Additive: never deletes a row |
| GET | `/admin/backup/status` | admin — offload destination, last success, bucket versioning, whether credentials are protected |
| POST | `/admin/backup/offload` | admin — runs an offload pass now rather than waiting for the worker |
| GET | `/admin/mailbox-retention/preview` | admin — per source: cutoff, eligible messages, and which rule is suspending it; deletes nothing |
| POST | `/admin/mailbox-retention/purge` | admin — **irreversible**: expunges report mail past retention from the mailbox. Opt-in per source, suspended for any source serving a client under legal hold |
| GET | `/alerts` | any — alert history (`days`, default 30); client-scoped for viewers |
| PATCH | `/alerts/{id}` | staff — triage: `status` of `open`, `acknowledged` or `closed` |
| POST | `/admin/alerts/evaluate` | admin — evaluates alert rules now |
| POST | `/admin/notifications/test` | admin — sends a test email. Requires `to` |
| GET | `/admin/digest/preview` | admin — renders a client's digest without sending. Requires `clientId`; optional `monthsAgo` |
| POST | `/admin/digest/send` | admin — sends any due digest; already-sent periods are skipped |
| GET | `/notification-recipients` | staff |
| POST | `/notification-recipients` | admin — `clientId` null means agency-wide |
| DELETE | `/notification-recipients/{id}` | admin |
| GET | `/health/live`, `/health/ready` | public |

## 1) Conventions

- Base path: `/api/v1`
- Auth:
  - Agency UI/API: HTTP-only cookie session.
  - Client read-only: signed magic link token (JWT/HMAC + nonce), usually passed as query token or bearer-style header.
- Content type: `application/json`
- Time format: ISO-8601 UTC.
- Pagination:
  - Request: `page` (1-based), `pageSize` (default 50, max 500)
  - Response: `page`, `pageSize`, `totalItems`, `totalPages`, `items`
- Errors use a shared envelope.

Error envelope example:

```json
{
  "error": {
    "code": "validation_error",
    "message": "One or more fields are invalid.",
    "details": [
      { "field": "name", "message": "Name is required." }
    ],
    "traceId": "01HV..."
  }
}
```

## 2) Auth and Session

### POST `/auth/login`

Authenticate agency user and set session cookie.

Request:

```json
{
  "email": "admin@example.com",
  "password": "secret"
}
```

Response `200`:

```json
{
  "user": {
    "id": "usr_123",
    "email": "admin@example.com",
    "displayName": "Agency Admin",
    "role": "agency_admin"
  }
}
```

### POST `/auth/logout`

Invalidate current session cookie.

### GET `/auth/me`

Return current user profile and role.

### POST `/auth/password/reset-request`

Request password reset token.

### POST `/auth/password/reset-confirm`

Confirm password reset with token.

## 3) Clients

### GET `/clients`

List clients (agency scoped).

Filters:

- `q` (name/slug contains)
- `isActive` (`true|false`)

### POST `/clients`

Create client.

Request:

```json
{
  "name": "Acme Inc",
  "slug": "acme-inc",
  "isActive": true,
  "retentionMonths": 27,
  "timezone": "UTC"
}
```

### GET `/clients/{clientId}`
### PATCH `/clients/{clientId}`
### DELETE `/clients/{clientId}`

Soft-delete/deactivate behavior is preferred over hard delete in MVP.

## 4) Domains

### GET `/domains`

List domains globally (with client ownership).

Filters:

- `clientId`
- `q` (domain contains)

### POST `/domains`

Create domain and assign owner client (global uniqueness enforced).

Request:

```json
{
  "name": "example.com",
  "clientId": "cl_123",
  "isActive": true
}
```

### PATCH `/domains/{domainId}`

Update domain settings or ownership transfer.

## 5) Mailbox Sources

### GET `/mailbox-sources`

List mailbox sources.

### POST `/mailbox-sources`

Create source (IMAP or POP3).

Request:

```json
{
  "name": "Acme Reports Inbox",
  "protocol": "imap",
  "host": "imap.mailhost.tld",
  "port": 993,
  "useTls": true,
  "username": "dmarc@agency.tld",
  "password": "plain-on-wire-only-here",
  "defaultClientId": "cl_123",
  "isActive": true
}
```

Notes:

- Password is encrypted at rest server-side.
- One source may serve multiple clients through domain routing.

### PATCH `/mailbox-sources/{sourceId}`
### DELETE `/mailbox-sources/{sourceId}`

### POST `/mailbox-sources/{sourceId}/sync`

Manual sync trigger for operations/testing. Returns sync summary payload immediately from execution.

Notes:

- Intended for operator use; steady-state sync is worker-scheduled.
- Mailbox processing is read-only (does not delete emails).

### POST `/mailbox-sources/{sourceId}/test-connection`

Run connectivity/auth test.

### GET `/mailbox-sources/{sourceId}/sync-runs`

List sync run history.

## 6) Ingestion and Sync

### GET `/mailbox-sync-runs`

List sync run history across mailbox sources.

Filters:

- `mailboxSourceId` (optional)
- `limit` (optional, default server value)

### GET `/mailbox-health`

Operational health summary by mailbox source.

Fields include:

- latest run status and error
- latest run counters (scanned/attachments/inserted/duplicates/parse failures)
- last success timestamp
- checkpoint values (`lastProcessedUid`, `lastProcessedUidValidity`)

## 7) Reports and Records

> **Partially implemented.** Report data is currently read through the
> `/analytics/*` endpoints in §8; there are no `/reports` routes yet, and no
> upload endpoint (see the backlog's "report upload" item).

### GET `/reports`

List DMARC reports.

Filters:

- `clientId` (required for agency views except global admin screens)
- `domainId`
- `from` / `to` (report period)
- `sourceId`

Note:

- Report persistence is domain-anchored (`domain_id`) and globally unique domain name policy is enforced.

### GET `/reports/{reportId}`

Report header + aggregate summary.

### GET `/reports/{reportId}/records`

Get normalized DMARC records for a report.

Record details include full-fidelity auth result collections:

- `auth_results.dkim[]`
- `auth_results.spf[]`

Filters:

- `disposition`
- `spfAligned` (`true|false`)
- `dkimAligned` (`true|false`)

## 8) Analytics (implemented)

Replaces the `/dashboard/*` design in earlier revisions of this document. See §0
for the full list; shapes are defined by the DTOs in
`src/api/Application/Analytics/AnalyticsDtos.cs` and
`RecordInspectionDtos.cs`.

### GET `/analytics/summary?days=30`

Window is described by a `window` object (`days`, `beginUtc`, `endUtc`,
`anchoredToLatestData`). Returns:

- `totals` — `domains`, `activeDomains`, `reports`, `messages`,
  `compliantMessages`, `complianceRate`, `dkimPassRate`, `spfPassRate`,
  `failingSources`
- `trend[]` — `date`, `messages`, `compliant`, `failed`
- `topFailingDomains[]`, `topReporters[]`
- `dispositions` — `none`, `quarantine`, `reject`
- `mailboxes` — `total`, `healthy`, `failing`; **`null` for `client_viewer`**

### GET `/analytics/domains?days=30`

Per-domain rows including `publishedPolicy`, `publishedPct`, `dkimAlignment`,
`spfAlignment`, a compliance `status` (`aligned`/`issues`/`critical`/`no_data`)
and an `enforcementStatus`
(`enforced`/`ramping`/`spoofing`/`monitoring`/`no_data`).

### GET `/analytics/domains/{domainId}/enforcement?days=30`

Guided path to enforcement: `currentPolicy`, `currentPct`, message/compliance
totals, `recommendedPolicy`, `recommendedAction`, `rationale`, `readyToAdvance`,
`blockingSourceCount`, and `blockingSources[]` (top 20 by failed volume).

### GET `/analytics/domains/{domainId}/records`

Live DNS inspection. `dmarc` and `spf` each carry a `status` of `found`,
`missing`, or `lookup_failed` (a failed lookup is deliberately distinct from a
missing record), the raw record, parsed tags, and an `issues[]` list. When
report data exists, `observed` plus a field-by-field `comparison[]` shows where
DNS and the reported policy disagree.

Each `comparison[]` entry carries a `status` of `match`, `differs`, `inherited`,
or `not_reported`, plus an optional `note`. **Only `differs` is a finding.**
`inherited` means DNS publishes no such tag and RFC 7489 derives it — this is
how an absent `sp` is reported, since a subdomain policy that is not published
cannot disagree with anything. `not_reported` means the tag is published but the
reporter sent no value for it. A published `sp` weaker than `p` is a genuine gap
and surfaces in `dmarc.issues[]` rather than as a comparison difference.

### GET `/analytics/threats?days=30&limit=100`

`totalFailedMessages`, `totalSources`, and `sources[]` of `(sourceIp, domain)`
pairs whose mail failed **both** DKIM and SPF, worst first, with dispositions
and first/last seen.

## 9) Alerts

> **Not implemented.** Target state for the *alert engine* backlog item.

### GET `/alerts/rules`
### POST `/alerts/rules`
### PATCH `/alerts/rules/{ruleId}`
### DELETE `/alerts/rules/{ruleId}`

Rule types:

- `failure_spike`
- `policy_regression`

Scope:

- global default rules
- per-client overrides

### GET `/alerts/events`

List generated alert events.

Filters:

- `clientId`
- `ruleType`
- `severity`
- `status` (`open|acknowledged|closed`)

## 10) Notification Recipients and Digest

> **Not implemented.** Target state for the *email digest + SMTP relay* backlog item.

### GET `/notifications/recipients`
### POST `/notifications/recipients`
### PATCH `/notifications/recipients/{recipientId}`
### DELETE `/notifications/recipients/{recipientId}`

Supports both global recipients and per-client recipients.

### GET `/digests/schedules`
### POST `/digests/schedules`
### PATCH `/digests/schedules/{scheduleId}`

Default cadence: monthly.

### POST `/digests/run-now`

Queue immediate digest generation/sending.

## 11) Exports

> **Not implemented.** Target state for the *CSV/JSON export* backlog item.

### POST `/exports`

Create async export job.

Request:

```json
{
  "clientId": "cl_123",
  "format": "csv",
  "from": "2026-01-01T00:00:00Z",
  "to": "2026-01-31T23:59:59Z",
  "filters": {
    "domainId": "dom_123"
  }
}
```

Response `202`:

```json
{
  "exportJobId": "exp_123",
  "status": "queued"
}
```

### GET `/exports/{exportJobId}`

Get job status and artifact metadata.

### GET `/exports/{exportJobId}/download`

Download generated artifact if status is `completed`.

## 12) Magic Links (Client Read-Only)

> **Not implemented.** Target state for the *magic link access* backlog item.

### POST `/magic-links`

Create signed link.

Request:

```json
{
  "clientId": "cl_123",
  "expiresInDays": 7,
  "label": "April client review"
}
```

Response:

```json
{
  "id": "ml_123",
  "url": "https://app.example.tld/client-view?token=...",
  "expiresAt": "2026-04-30T12:00:00Z"
}
```

### GET `/magic-links`

List active/expired links.

### POST `/magic-links/{magicLinkId}/revoke`

Revoke by invalidating nonce.

## 13) PDF Reports

> **Not implemented.** Target state for the *branded PDF reports* backlog item.

### POST `/reports/pdf`

Generate branded PDF summary.

Request:

```json
{
  "clientId": "cl_123",
  "from": "2026-03-01T00:00:00Z",
  "to": "2026-03-31T23:59:59Z"
}
```

Response `202` with job id.

### GET `/reports/pdf/{jobId}`

Get render status and artifact link.

## 14) Admin and Ops

### GET `/admin/audit-events`

> **Implemented.** Filters are `days` (1–730, default 30), `eventType` (prefix
> match, so `client` finds `client.created` and `client.updated`), `actor` (email
> substring, case-insensitive), `clientId`, `limit` (1–1000, default 200) and
> `offset`. There is deliberately no write endpoint.

Returns `{ total, items[] }` — `total` is the count *before* paging, so the
console can show "100 of 4,812" rather than implying the page is the whole trail.

Each item carries `id`, `occurredAtUtc`, `actorType`, `actorUserId`, `actorEmail`,
`eventType`, `targetType`, `targetId`, `clientId`, `clientName`, `summary`,
`details`, `ipAddress` and `userAgent`.

`clientName` is resolved by left join, not by a navigation: `audit_event` has no
foreign keys on purpose, so a deleted client leaves its `clientId` in place and
`clientName` simply reads null. Surfaced in the console at `/audit` (admin only).

### GET `/admin/health`

Operational status for queues, workers, and key dependencies.

## 15) Health/Readiness

- `GET /health/live`
- `GET /health/ready`

## 16) Role Matrix (MVP)

- `agency_admin`
  - full access to all endpoints.
- `agency_analyst`
  - read/write operational endpoints, limited admin settings.
- `client_viewer`
  - any-authenticated endpoints only; reads are scoped to granted clients via
    `user_client_grant`, and cross-tenant ids return 404.
- `magic_link_viewer` *(planned)*
  - read-only subset for one client scope; magic links are not implemented.

## 17) Status Codes

- `200` success
- `201` created
- `202` accepted (async work queued)
- `204` no content
- `400` bad request / validation
- `401` unauthenticated
- `403` forbidden
- `404` not found
- `409` conflict (unique/domain ownership/dedup constraints)
- `429` rate/size limits
- `500` internal error
