# ADR 0003: Authentication and Client Access

- Status: accepted
- Date: 2026-03-31

## Context

Most clients do not need full accounts in MVP. Agency operators need secure daily access, and some clients need limited read-only visibility.

## Decision

- Agency authentication uses local username/password.
- Agency sessions use HTTP-only secure cookies.
- Default session policy: 12h idle timeout, 7d absolute max.
- Client access uses signed magic links with DB nonce.
- Magic links are reusable until expiry (default 7 days), read-only, and scoped to a single client.
- OIDC/external identity providers are post-MVP.

## Consequences

### Positive

- Fast MVP path with pragmatic security controls.
- Magic links reduce account-management overhead for occasional client access.
- Nonce-backed revocation supports operational control.

### Negative

- Local auth requires secure reset/recovery implementation.
- Magic-link workflows require careful monitoring and auditing.

### Follow-up

- Implement password reset and session revocation flows.
- Log and monitor magic-link creation, access, and revocation events.
