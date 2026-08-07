# OIDC Login (Zitadel) — Dev Setup

The app authenticates locally (email/password) out of the box. OIDC is an
optional second front door: any OpenID Connect provider can authenticate
users, while **authorization stays in-app** (roles + client grants). This
guide wires up the dedicated dev Zitadel that ships in `docker-compose.yml`.

## How it works

1. The login page shows "Sign in with <provider>" when `Auth:Oidc:Enabled` is true (`GET /api/v1/auth/providers`).
2. `GET /api/v1/auth/oidc/login` challenges the provider (authorization code + PKCE) via the Microsoft OIDC handler, which signs the result into a short-lived `external-temp` cookie.
3. `GET /api/v1/auth/oidc/complete` consumes that cookie once, resolves the user, mints the app's own `dmarc_session`, and signs the temp scheme out. `SessionAuthMiddleware` is the only downstream authority — an SSO session is identical to a password session.
4. Identity mapping (`user_identity`, keyed by issuer + subject):
   - Known identity → log that user in.
   - Else a **verified** email matching a local user → link and log in (unverified email is refused).
   - Else, if `AutoProvision` is on → create a user with `DefaultRole` and an empty password hash (no password login for it); otherwise refuse with `no_account`.

## One-time Zitadel setup

Start the dev IdP (part of the compose stack):

```bash
docker compose up -d zitadel   # console at http://localhost:8082
```

The service runs **Zitadel v4** as a single container, with its login-v2 feature
turned off (`ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED: "false"`). v4
otherwise routes all interactive login through a *separate* login container that
has to share an origin with the API, which would mean adding a reverse proxy to
this file; without both, the console redirects to `/ui/v2/login` and answers
`{"code":5,"message":"Not Found"}`, so nobody can sign in. The flag keeps the
built-in login and the authorization-code flow behaves exactly as it did on v2.

That setting only applies when the instance is first created. If you have an
older volume — in particular one from v2.65.1, which could not start against
Postgres 18 at all — the database has to be recreated:

```bash
docker compose stop zitadel
docker compose exec -T postgres psql -U postgres -c 'DROP DATABASE zitadel'
docker compose exec -T postgres psql -U postgres -c 'CREATE DATABASE zitadel'
docker compose up -d zitadel
```

This drops every user, project and application in the dev IdP. It does not touch
`dmarc_analyzer`, which is a separate database in the same server.

1. Open `http://localhost:8082/ui/console`, sign in as `zitadel-admin@zitadel.localhost` / `Password1!` (you'll be forced to set a new password on first login).
2. **Projects → Create New Project** → name `dmarc-analyzer`.
3. In the project, under **APPLICATIONS**, click the **+** tile. That opens a
   four-step wizard:
   1. *Name and Type* — name `dmarc-analyzer`, type **Web** (preselected).
   2. *Authentication Method* — **PKCE**, which is preselected and labelled
      recommended. It sets the authentication method to `None`: no client secret
      exists to copy, or to rotate later.
   3. *Redirect URIs* — turn on **Development Mode** first, or an `http://` URI
      is rejected. Then add, pressing **Enter** in the field (the **+** next to
      it does not always register):
      - `http://localhost:8081/api/v1/auth/oidc/callback` (compose API port)
      - `http://localhost:5173/api/v1/auth/oidc/callback` (Vite dev proxy)
   4. *Overview* — check `Authentication Method: None` and both URIs, then
      **Create**.
4. The **Client Details** dialog shows the **Client ID**. Copy it; it also states
   that no secret is required, which is the difference from a provider like
   Entra ID that insists on one.

> Development Mode disables redirect-URI validation entirely — that is what
> allows plain `http://`. Fine here, never on an instance that matters.

## Running the app with OIDC

The issuer advertises `http://localhost:8082`, which must resolve to the same
Zitadel from **both** the browser and the API. A containerised API cannot
reach the host-published `localhost:8082` (remapping `localhost` inside the
container breaks loopback), so run the API on the host for OIDC — the normal
dev loop:

```bash
env APP_MODE=api ASPNETCORE_URLS=http://localhost:8081 \
  ConnectionStrings__Default="Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres" \
  Database__MigrateOnStartup=true \
  Security__CredentialEncryptionKey="<your key>" \
  Auth__Oidc__Enabled=true \
  Auth__Oidc__Authority=http://localhost:8082 \
  Auth__Oidc__ClientId="<client id>" \
  Auth__Oidc__DisplayName=Zitadel \
  Auth__Oidc__DefaultRole=client_viewer \
  Auth__Oidc__AutoProvision=true \
  Auth__Oidc__RequireHttpsMetadata=false \
  dotnet run --project src/api --no-launch-profile -c Release
```

(For the full hot-reload loop, run the Vite dev server too and use its
`http://localhost:5173` redirect URI.)

The compose API keeps `Auth__Oidc__Enabled=false` so `docker compose up` stays
self-contained; flip the env vars above (mirrored on the `api` service) when
you want OIDC inside compose.

## Production notes

- `RequireHttpsMetadata=true` (the default) in real deployments; the dev flag only exists for plain-http localhost.
- Losing/rotating the provider is safe — identities re-link by verified email.
- `AutoProvision=false` is the safer production default: users must be created by an admin first, then SSO links to them by verified email.
- **Pre-provision those accounts without a password.** `POST /api/v1/users`
  treats `password` as optional (Users → Add user leaves the field empty), and an
  omitted one stores an empty hash rather than a hash of `""` — no password can
  open the account, which is the same guarantee auto-provisioning gives. That is
  how you pick the role up front instead of accepting `DefaultRole`. The Users
  table's "Sign-in" column shows which shape each account is, and warns when a
  passwordless account exists on an instance with no provider configured, since
  it can then sign in by no route at all.
- The app logout revokes only the app session; the IdP session is left intact (single logout is out of scope).

## Other providers

Zitadel is what this dev stack ships, but nothing here is Zitadel-specific. Four
others have their own guides: [Keycloak](oidc-keycloak.md),
[Authentik](oidc-authentik.md), [Google](oidc-google.md) and
[Microsoft Entra ID](oidc-entra.md).

**Read the Entra guide before configuring Entra.** It is the one with
provider-specific behaviour that has to be worked around rather than merely
configured — it needs a client secret, its callback breaks on the handler's
default response mode (issue #114), and it issues no `email_verified` claim at
all, which refuses every Entra user who already has a local account until the
`xms_edov` optional claim is configured (issue #140).
