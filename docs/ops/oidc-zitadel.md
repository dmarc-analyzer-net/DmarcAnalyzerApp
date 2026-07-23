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

1. Open `http://localhost:8082/ui/console`, sign in as `zitadel-admin@zitadel.localhost` / `Password1!` (you'll be forced to set a new password on first login).
2. **Projects → Create New Project** → name `dmarc-analyzer`.
3. In the project, **Applications → New**: type **Web**, authentication method **PKCE**.
4. Redirect URIs (enable **Development Mode** to allow http):
   - `http://localhost:8081/api/v1/auth/oidc/callback` (compose API port)
   - `http://localhost:5173/api/v1/auth/oidc/callback` (Vite dev proxy)
5. Copy the generated **Client ID** (PKCE public client — no secret).

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
- The app logout revokes only the app session; the IdP session is left intact (single logout is out of scope).
