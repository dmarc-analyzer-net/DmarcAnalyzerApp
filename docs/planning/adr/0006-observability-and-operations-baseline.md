# ADR 0006: Observability and Operations Baseline

- Status: accepted
- Date: 2026-03-31

## Context

Agency deployments need visibility into ingestion health, alert generation, and operational incidents without requiring a full platform team.

## Decision

- Emit structured JSON logs by default.
- Include health endpoints: liveness and readiness.
- Keep telemetry pipeline OTEL-ready for logs, metrics, and spans pushed to collector.
- Include core audit logs for login activity, config changes, sync runs, and magic-link usage.

## Consequences

### Positive

- Faster troubleshooting for ingestion and routing issues.
- Compatible with modern observability stacks without tight coupling.
- Audit trail improves accountability and operational safety.

### Negative

- Requires disciplined event schema/versioning.
- Audit and telemetry retention policies must be explicitly managed.

### Follow-up

- Define structured log schema and correlation-id standards.
- Define minimum audit events and operational dashboards.
