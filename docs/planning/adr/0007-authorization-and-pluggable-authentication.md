# ADR 0007: Authorization and Pluggable Authentication

- Status: accepted
- Date: 2026-07-23
- Supersedes parts of ADR 0003 (extends its auth model)

## Context

The app must run standalone (its own login, zero external dependencies) and
also integrate with identity platforms (Zitadel, Keycloak, Entra). It also
needs multi-tenant isolation: agency staff see everything, client viewers see
only their clients. The original auth baseline (ADR 0003) authenticated users
but enforced no authorization.

## Decision

Separate authentication from authorization:

- **Authorization is always in-app.** Roles (`agency_admin`, `agency_analyst`,
  `client_viewer`) live on `agency_user`; per-client access lives in
  `user_client_grant`. Identity providers never decide authorization. Endpoint
  access is enforced by route metadata + `RoleAuthorizationMiddleware`
  (deny-by-default for `client_viewer`); data is tenant-scoped in the query
  services via a per-request `ICurrentUserContext`.
- **Authentication is pluggable.** Local email/password and any OIDC provider
  are interchangeable front doors; both mint the same `dmarc_session` cookie,
  so all downstream code is identity-source agnostic.
- **OIDC is hybrid.** The Microsoft OIDC handler performs challenge/callback
  (state/nonce/PKCE/token validation) into a short-lived `external-temp`
  cookie; a `/complete` endpoint consumes it once and mints the app session.
- **Identity mapping** via `user_identity` (issuer + subject → user). First
  login links to an existing local user only by **verified** email, else
  optionally auto-provisions a `DefaultRole` user with no usable password.
- Registration is locked to first-run bootstrap; further users are created by
  admins.

## Consequences

### Positive

- Works identically standalone and behind any OIDC IdP; adding Keycloak/Entra
  is configuration, not code.
- One session authority and one authorization path — a single place to audit.
- New endpoints are invisible to viewers unless explicitly opted in.

### Negative

- Grants are queried per request (accepted; cheap and always fresh).
- OIDC dev requires the issuer host to resolve identically from browser and
  API — run the API on the host for local OIDC (see `docs/ops/oidc-zitadel.md`).

### Follow-up

- Optional claim→role mapping for IdP-driven authorization.
- Reconcile the ADR 0003 magic-link model with the `client_viewer` role (the
  viewer experience already approximates a client portal).
