# ADR 0002: Ingestion and Worker Execution Strategy

- Status: accepted
- Date: 2026-03-31

## Context

DMARC ingestion includes periodic mailbox polling, retries, backfill, and long-running tasks (exports, digests, alerts). We need reliability without heavy platform dependencies.

## Decision

- Use a lightweight database-backed job queue.
- Use a hosted worker mode in the same application image.
- Polling cadence is global every 60 minutes, running 24/7.
- Backfill is unlimited, processed oldest-to-newest with checkpoints.
- Kubernetes runs worker mode via CronJob-triggered executions.

## Consequences

### Positive

- Reliable retries and resumability with minimal external components.
- Same code/image for API and worker simplifies delivery.
- K8s CronJob aligns with periodic schedule and resource control.

### Negative

- DB queue requires careful locking semantics and index tuning.
- Cron-triggered workers may add slight delay versus always-on worker.

### Follow-up

- Define job schema, locking, retry, and dead-letter policies.
- Add operational views for queue depth and failed jobs.
