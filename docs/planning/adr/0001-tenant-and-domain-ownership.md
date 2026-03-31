# ADR 0001: Tenant and Domain Ownership Model

- Status: accepted
- Date: 2026-03-31

## Context

The product targets agencies managing DMARC analytics for many clients. We need strong client separation while keeping operations simple for self-hosted deployments.

## Decision

- Use a single PostgreSQL database.
- Enforce tenant scoping with `client_id` on all client-owned entities.
- Enforce globally unique domain ownership across clients.
- Keep one active owner client per domain.

## Consequences

### Positive

- Simpler operations than per-tenant databases.
- Lower infrastructure overhead for agency deployments.
- Clear ownership prevents ambiguous report attribution.

### Negative

- Requires strict query discipline to avoid tenant leaks.
- Domain transfers need careful workflow and audit trail.

### Follow-up

- Add guardrails in repositories/services to require client scope.
- Add tests for tenant boundary and ownership constraints.
