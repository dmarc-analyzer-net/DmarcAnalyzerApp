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

## Addendum (2026-07-28): the shared mailbox requires a DNS opt-in per client domain

The "one mailbox source can receive reports for multiple clients" decision above has
a DNS-level consequence that wasn't called out here: every client domain publishes
`rua=`/`ruf=` pointing at a mailbox on the *agency's* domain, not its own. DMARC
requires the destination domain to authorize that explicitly, via a
`<client-domain>._report._dmarc.<agency-domain>` TXT record (RFC 9990 §4, which
compares organizational domains and sanctions the `*._report._dmarc` wildcard form —
functionally unchanged from RFC 7489 §7.1's exact-name version this app was built
against). Without it, conforming receivers silently drop the reports; nothing bounces
to explain why.

Until now this was undocumented in the product — only in the marketing site's setup
guides. `RecordInspectionService.CheckExternalDestinationsAsync` now surfaces it
directly on the domain detail page, checking authorization for any external `rua`/
`ruf` destination whenever a domain publishes its own DMARC record. It deliberately
does not attempt this for a policy inherited from an ancestor domain (`Status ==
Inherited`) — which domain name the check should use in that case is a real judgement
call the source of this logic (the website's checker, which has no tree walk) never
had to make, and porting it untested would go beyond what's actually proven correct.
