# Mailbox Sync Operations

Operational guide for mailbox ingestion in `DmarcAnalyzerApp`.

## Worker Configuration

Configure via environment variables (`Worker__*`) or appsettings.

- `Worker__ScheduleIntervalSeconds`
- `Worker__MaxMessagesPerSync`
- `Worker__MaxRetryAttempts`
- `Worker__RetryBaseDelaySeconds`
- `Worker__StaleRunTimeoutMinutes`
- `Worker__SyncRunTimeoutMinutes`

Recommended baseline:

- Production:
  - `ScheduleIntervalSeconds=3600`
  - `MaxMessagesPerSync=200`
  - `MaxRetryAttempts=3`
  - `RetryBaseDelaySeconds=2`
  - `StaleRunTimeoutMinutes=90`
  - `SyncRunTimeoutMinutes=30`
- Development:
  - `ScheduleIntervalSeconds=15`
  - `MaxMessagesPerSync=50`
  - `StaleRunTimeoutMinutes=20`
  - `SyncRunTimeoutMinutes=10`

## Read-Only Mailbox Safety

- Mailbox processing is read-only.
- Sync does not delete or mutate emails on the mailbox server.

## Health and Diagnostics Endpoints

- `GET /api/v1/mailbox-health`
  - latest run status/error and counters per mailbox source
  - checkpoint state (`lastProcessedUid`, `lastProcessedUidValidity`)
- `GET /api/v1/mailbox-sync-runs`
  - sync run history with per-run counts and errors
- `POST /api/v1/mailbox-sources/{id}/sync`
  - manual operator trigger for targeted testing/recovery

## Common Failure Patterns

- `unsupported compression method`
  - Attachment ZIP compression variant not currently supported by extractor.
  - Action: track parse failure count and add extractor compatibility fallback.
- `parse failures > 0`
  - Report format variation from sender/provider.
  - Action: capture sample and add fixture coverage.
- stale success timestamp
  - Worker not catching up or mailbox has no recent traffic.
  - Action: verify worker logs, mailbox connectivity, and checkpoint movement.

## UI Operations View

In `Mailbox Sources` view:

- `Mailbox Health` table for latest state.
- `Recent Sync Runs` for recent per-source history.
- Filters:
  - failed mailboxes
  - parse failures > 0
  - stale last success (>24h)
