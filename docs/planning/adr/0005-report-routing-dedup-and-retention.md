# ADR 0005: Report Routing, Deduplication, and Retention

- Status: accepted
- Date: 2026-03-31

## Context

One mailbox source can receive reports for multiple clients. We need deterministic client routing, safe deduplication, and predictable retention behavior.

## Decision

- Route reports by `policy_published.domain` owner mapping first.
- Each source has a default client fallback.
- If report domain is unmatched, auto-create domain under source default client.
- Deduplication key is `(client, domain, report-id, begin, end)`.
- Retention is configurable per client with default 27 months.
- Purge eligibility is based on report end date.

## Consequences

### Positive

- Deterministic routing for mixed-client mailboxes.
- Strong dedup behavior for repeated or replayed report deliveries.
- Retention aligns with DMARC reporting periods.

### Negative

- Auto-created domains can capture unexpected domains without governance.
- Requires tooling to review and correct fallback assignments.

### Follow-up

- Add domain review workflow for auto-created entries.
- Add alerts/audit events for unmatched-domain fallback assignments.
