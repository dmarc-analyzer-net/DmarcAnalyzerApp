# ADR 0004: Deployment Targets and Runtime Modes

- Status: accepted
- Date: 2026-03-31

## Context

The product is self-hosted by agencies with mixed infrastructure maturity. We need a path that works for simple deployments and scales to Kubernetes environments.

## Decision

- Support Docker Compose and Kubernetes with equal planning depth.
- Use a single container image with dual runtime mode:
  - API mode
  - Worker mode
- Production database is PostgreSQL.
- Run EF migrations via a dedicated init job/migration container.

## Consequences

### Positive

- One build artifact simplifies release and compatibility.
- Compose supports quick adoption; K8s supports mature ops teams.
- Init-job migrations reduce race conditions from multi-replica startup.

### Negative

- Runtime mode switching increases configuration complexity.
- Must maintain two deployment docs and operational runbooks.

### Follow-up

- Define environment variable contract for mode selection.
- Provide reference Compose and K8s manifests/Helm templates.
