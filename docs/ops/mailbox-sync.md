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
  - `MaxMessagesPerSync=500`
  - `MaxRetryAttempts=3`
  - `RetryBaseDelaySeconds=2`
  - `StaleRunTimeoutMinutes=90`
  - `SyncRunTimeoutMinutes=30`
- Development:
  - `ScheduleIntervalSeconds=15`
  - `StaleRunTimeoutMinutes=20`
  - `SyncRunTimeoutMinutes=10`

## Protocols

Both polled protocols run the same pass — the same drain budget, batched checkpoints,
archive-before-parse rule, run rows and retention deletion — behind `IMailboxTransport`.
What differs is only what the protocol itself makes possible.

| | IMAP | POP3 |
|---|---|---|
| Default TLS port | 993 | 995 |
| Checkpoint | `LastProcessedUid` + `LastProcessedUidValidity` | `LastProcessedUidl` |
| Resuming | server-side UID range past the checkpoint | find the UIDL in the listing, take what follows |
| Retention scan | `SEARCH DELIVEREDBEFORE`, server-side | reads every message's headers, client-side |
| Arrival time | `INTERNALDATE` | the sender's own `Date` header |
| Deletion | `\Deleted` + `EXPUNGE` | `DELE`, applied only when the session ends with `QUIT` |

Three consequences worth knowing before pointing a POP3 source at a large mailbox:

- **UIDL is required.** RFC 1939 makes it optional, and without it no durable checkpoint
  exists — every pass would re-read the whole mailbox for ever. The sync refuses instead,
  and the refusal is on the source's `mailbox-health` row.
- **A lost checkpoint costs a full pass.** If the checkpointed message is gone from the
  mailbox (deleted by hand, or by another client reading the same mailbox) there is no
  position to recover, so everything is read again. Deduplication keeps the data correct;
  the cost is the work. The worker logs it as a warning rather than leaving it to look
  like a loop.
- **Retention deletion is the expensive half.** With no server-side date search, the pass
  reads headers for every message in the mailbox. It logs how many it read.

## Mailbox Safety

- Sync is read-only. It does not delete or mutate mail on the server.
- Deletion happens only in the mailbox retention pass, only on sources with
  `deleteAfterRetention` turned on, and never for a message the archive has no copy of
  when archiving is enabled.

## Health and Diagnostics Endpoints

- `GET /api/v1/mailbox-health`
  - latest run status/error and counters per report source
  - checkpoint state (`lastProcessedUid` + `lastProcessedUidValidity` on IMAP,
    `lastProcessedUidl` on POP3 — the console's Checkpoint column shows whichever the
    source has)
- `GET /api/v1/mailbox-sync-runs`
  - sync run history with per-run counts and errors
- `POST /api/v1/report-sources/{id}/sync`
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
- `the POP3 server does not support UIDL`
  - No durable checkpoint is possible, so the source is refused rather than run.
  - Action: move the mailbox to IMAP, or use a POP3 server that implements UIDL.
- POP3 messages scanned stays equal to the mailbox size every pass
  - The checkpoint UIDL is not surviving between passes.
  - Action: check the worker log for the "checkpoint is no longer in the mailbox"
    warning, and whether something else is deleting from the same mailbox.

## UI Operations View

In `Mailbox Sources` view:

- `Mailbox Health` table for latest state.
- `Recent Sync Runs` for recent per-source history.
- Filters:
  - failed mailboxes
  - parse failures > 0
  - stale last success (>24h)
