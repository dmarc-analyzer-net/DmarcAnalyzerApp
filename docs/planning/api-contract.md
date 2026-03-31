# API Contract

API contract for `DmarcAnalyzerApp` MVP.

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

### POST `/mailbox-sources/{sourceId}/test-connection`

Run connectivity/auth test.

### GET `/mailbox-sources/{sourceId}/sync-runs`

List sync run history.

## 6) Ingestion and Sync

### POST `/ingestion/run-now`

Trigger immediate sync job.

Request:

```json
{
  "sourceId": "src_123"
}
```

Response `202`:

```json
{
  "jobId": "job_123",
  "status": "queued"
}
```

### GET `/ingestion/jobs`

List queued/running/failed jobs.

Filters:

- `status` (`queued|running|failed|completed|dead_letter`)
- `jobType`
- `sourceId`

### GET `/ingestion/jobs/{jobId}`

Get job detail with attempts and error history.

### POST `/ingestion/jobs/{jobId}/retry`

Retry dead-letter/failed job.

## 7) Reports and Records

### GET `/reports`

List DMARC reports.

Filters:

- `clientId` (required for agency views except global admin screens)
- `domainId`
- `from` / `to` (report period)
- `sourceId`

### GET `/reports/{reportId}`

Report header + aggregate summary.

### GET `/reports/{reportId}/records`

Get normalized DMARC records for a report.

Filters:

- `disposition`
- `spfAligned` (`true|false`)
- `dkimAligned` (`true|false`)

## 8) Dashboard and Metrics

### GET `/dashboard/summary`

Top-level metrics for selected window.

Query:

- `clientId` (required)
- `from` / `to`

Response fields:

- `totalMessages`
- `passRate`
- `failRate`
- `spfAlignmentRate`
- `dkimAlignmentRate`
- `dispositionBreakdown`

### GET `/dashboard/trends/daily`

Daily aggregate trend points.

### GET `/dashboard/sources/top`

Top source IPs by volume/failures.

## 9) Alerts

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

Query core audit log.

Filters:

- `actorType`
- `actorId`
- `eventType`
- `clientId`
- `from` / `to`

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
- `magic_link_viewer`
  - read-only subset for one client scope.

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
