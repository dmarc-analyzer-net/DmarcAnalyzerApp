# ADR 0010: Machine Credentials

- Status: accepted
- Date: 2026-08-11
- Extends ADR 0007 (authorization and pluggable authentication)

## Context

Every authenticated request today is a human in a browser. Local password login
and OIDC both mint the same `dmarc_session` cookie, and `SessionAuthMiddleware`
rejects anything under `/api/v1/` that does not carry one. There is no bearer
token, no API key, and no machine identity anywhere in the codebase.
`ICurrentUserContext` is shaped accordingly: `UserId`, `Email`, `Role`,
`AllowedClientIds`.

Two pieces of wanted work need a caller that is not a person:

- **Report ingestion over HTTP** ([#156](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues/156)).
  An external service that already owns Microsoft Graph authentication wants to
  push raw DMARC payloads in, so this app never has to hold Graph credentials.
  The caller is a service account, running unattended, for years.
- **Magic links** (backlog; sketched in ADR 0003). A human with no account,
  following a link to a read-only view of one client.

Deciding these one at a time is how a codebase ends up with two token systems
that hash differently, revoke differently and are audited differently. The
question is not "how does the ingestion endpoint authenticate" but "what is a
machine credential in this application", and the answer has to be settled before
either lands.

There is also a tenancy invariant to protect. ADR 0001 makes `client` the tenant
root and domains globally unique; ADR 0007 puts authorization entirely in-app.
A machine caller pushing report data is exactly the shape of thing that quietly
breaks that: if the payload can say which client it belongs to, then a
credential leak is a cross-tenant write, not just an unwanted one.

## Decision

### A credential is a first-class row, not a column on a source

Introduce `api_credential`. One row is one issued secret:

| Column | Notes |
|---|---|
| `Id` | PK |
| `Name` | operator-chosen, so a list of credentials is readable |
| `Kind` | what this credential may do — `report_ingest` initially |
| `ReportSourceId` | nullable FK; set for `report_ingest`, null for future kinds |
| `TokenId` | the non-secret lookup half, unique and indexed |
| `TokenHash` | SHA-256 of the secret half |
| `CreatedAtUtc`, `CreatedByUserId` | who issued it |
| `LastUsedAtUtc` | nullable — evidence a credential is live, or abandoned |
| `ExpiresAtUtc` | nullable, no default expiry |
| `RevokedAtUtc` | nullable; revocation is a state, not a delete |

Storing the credential *on* `report_source` was the obvious cheaper option and
is rejected for one reason: **rotation needs two valid credentials at the same
time.** A single column forces a flag-day cutover on an unattended pipeline,
which is how operators end up never rotating at all. A table also gives more
than one ingesting system per source, a revocation history rather than an
overwrite, and somewhere for a second `Kind` to live without a second design.

`RevokedAtUtc` rather than a delete because "this credential was revoked at
09:14" is the answer to an incident question, and a deleted row cannot answer it.

### The token is split, and hashed for the job it actually has

The issued string is `dmarcanalyzer_<TokenId>_<secret>`, where the secret is 256
bits from a cryptographic RNG. Verification looks the row up by `TokenId` — one
indexed read — then compares SHA-256 of the presented secret against `TokenHash`
in constant time.

**SHA-256, deliberately, and not the PBKDF2 the app uses for passwords.**
`PasswordHasher` runs 100,000 iterations because a human password is low-entropy
and worth brute-forcing offline. A 256-bit random secret is not: there is no
guessing attack to slow down, and adding ~100 ms to every ingest request would
be paying a real cost against an imaginary threat. The prefix makes the
credential greppable in logs and leak scanners, which is worth more here than a
slow hash.

### Reveal once

The plaintext token exists in exactly one HTTP response — the one that created
it — and is never retrievable again. Lost means rotate. Nothing in the audit
trail, the logs or the config export ever contains a secret.

### Transport is `Authorization: Bearer`

Not a bespoke header. It is what every client library, proxy and log-redaction
rule already understands.

### The credential decides the client; the request body never does

A `report_ingest` credential is bound to one `ReportSourceId`, and the client is
resolved from `report_source.DefaultClientId`. A payload that names a different
client is not honoured, and not an error to be argued about — the field simply
has no authority. This keeps the ADR 0001 tenancy path intact for a caller that
is not a person, and it means a leaked credential is bounded by the source it
was issued for.

### A machine is not a user, and gets no role

Machine callers do not get a synthetic `agency_user`, and they do not get a role
from the `agency_admin` / `agency_analyst` / `client_viewer` set. Roles are for
people, and quietly minting a user for a service account is how a service
account ends up able to read the console API.

Instead a machine credential authorises **exactly the endpoints its `Kind`
names**, by allowlist. This mirrors the existing posture: `client_viewer` is
deny-by-default and must opt in per endpoint via `.AllowClientViewer()`, and
machine credentials are deny-by-default in the same way, via a parallel
`.AllowMachineCredential(kind)` marker. An endpoint that has not opted in is not
reachable with a bearer token, whatever the credential is scoped to. Cookie
sessions and bearer tokens stay separate paths that meet at the tenancy check,
never at a shared "is authenticated" boolean.

### Audit already has a place for this

`AuditEvent.ActorType` is already `user` / `anonymous` / `system`; machine
callers add `credential`, carrying the credential id rather than a user id.
Issuing, rotating and revoking are audited as admin actions. `LastUsedAtUtc` is
updated on use, throttled so a busy pipeline does not turn a read path into a
write on every request.

### What this is not

- Not OAuth client credentials, and not JWTs. There is no third party to
  federate with, and a self-hosted app should not require an authorization
  server to accept a file.
- Not a scope string framework. `Kind` is a closed enum in code, not a grammar.
- Not per-user API keys. A person's automation gets its own credential, so
  deleting the person does not silently break the pipeline, and rotating the
  pipeline does not touch the person.
- **Not magic links.** Those stay a separate mechanism: a human, one client,
  read-only, short-lived, delivered by email — a different threat model and a
  different revocation story. They must, however, reuse the same token
  generation and constant-time verification helper, so the app has exactly one
  place where a token is minted and compared.

## Consequences

### Positive

- One answer for every non-human caller, decided before the first one ships.
- Rotation is possible without a flag day, which is what makes it happen at all.
- A leaked ingest credential is bounded: one source, one client, one endpoint.
- The deny-by-default posture that protects `client_viewer` now covers machines,
  so a new endpoint is not silently machine-reachable.
- Credential lifecycle is auditable with the actor model that already exists.

### Negative

- A second authentication path through the middleware. It is more surface to
  review, and the review has to confirm the two paths cannot be confused for one
  another.
- Bearer tokens are bearer tokens. Anyone holding one is the caller; there is no
  proof of possession, so transport security and leak scanning carry real weight.
- `LastUsedAtUtc` is a write on a hot path even throttled, and it is the first
  thing to drop if ingest throughput ever becomes a problem.
- No default expiry is a deliberate trade: forced expiry on an unattended
  pipeline produces silent ingestion outages, and silent outages in *this*
  application mean a domain quietly stops being monitored.

### Follow-up

- Rate limiting per credential, before the endpoint is reachable from the
  internet.
- The ingestion endpoint contract itself — payload limits, archive bounds,
  transport idempotency ([#156](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues/156) step 4).
- Whether credential management belongs in the console or stays API-only.
- `Network__*` forwarded headers matter more here than anywhere else: without
  them every audit entry for an internet-facing endpoint records the proxy's
  address rather than the caller's.
