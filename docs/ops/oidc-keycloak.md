# OIDC Login (Keycloak) — Dev Setup

A second dev-only identity provider, alongside Zitadel
([`docs/ops/oidc-zitadel.md`](oidc-zitadel.md)), for testing OIDC against
Keycloak specifically. The concepts are the same either way — see that file's
"How it works" if you have not read it — this covers only what differs.

## One-time Keycloak setup

Start the dev IdP (part of the compose stack):

```bash
docker compose up -d keycloak   # console at http://localhost:8085
```

Unlike Zitadel, this needs no database prepared ahead of time. `start-dev`
with no `KC_DB` set falls back to Keycloak's own embedded dev store (an H2
file kept in the `keycloak_data` volume), so there is no `CREATE DATABASE`
step and no risk of an old volume leaving it half-migrated.

1. Open `http://localhost:8085/admin/master/console/`, sign in as `admin` /
   `admin` (bootstrapped fresh on first start — see `KC_BOOTSTRAP_ADMIN_*` in
   `docker-compose.yml`).
2. **Manage realms → Create realm**, name `dmarc-analyzer`. Realms are fully
   isolated; do not add the app's client to `master`, which is for
   administering Keycloak itself.
3. Switch into the new realm, then **Clients → Create client**:
   1. *General settings* — Client ID `dmarc-analyzer`, any display Name.
   2. *Capability config* — leave **Client authentication** off (public
      client, no secret) and **Standard flow** checked (authorization code).
      Turn **Require PKCE** on and leave **PKCE Method** at the default
      `S256`. This is the one place Keycloak's default is worth overriding:
      unlike Zitadel, which preselects PKCE as *recommended*, Keycloak ships
      this **off** even for a client with no secret — a public client with
      neither a secret nor enforced PKCE is weaker than either alone.
   3. *Login settings* — add the redirect URI, no special toggle needed
      first (Keycloak matches whatever URI you register, `http://` included,
      with nothing equivalent to Zitadel's Development Mode flag):
      - `http://localhost:8081/api/v1/auth/oidc/callback` (compose API port)
4. The client's **Credentials** tab does not exist at all once client
   authentication is off — confirmation there is really nothing to copy.
5. **Users → Create new user.** Turn **Email verified** on — the app links
   an SSO identity to any existing local account by verified email, and
   Keycloak's own flag is what that check reads. Fill in First and Last name
   here rather than leaving them blank: an incomplete profile triggers a
   Keycloak-side "Update Account Information" prompt on first login, which
   is harmless but easy to mistake for something broken in the app.
6. On the user, **Credentials → Set password**, and turn **Temporary** off
   unless you want a forced password-change screen on first sign-in too.

## Running the app with OIDC

```bash
env APP_MODE=api ASPNETCORE_URLS=http://localhost:8081 \
  ConnectionStrings__Default="Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres" \
  Database__MigrateOnStartup=true \
  Security__CredentialEncryptionKey="<your key>" \
  Auth__Oidc__Enabled=true \
  Auth__Oidc__Authority=http://localhost:8085/realms/dmarc-analyzer \
  Auth__Oidc__ClientId="dmarc-analyzer" \
  Auth__Oidc__DisplayName=Keycloak \
  Auth__Oidc__DefaultRole=client_viewer \
  Auth__Oidc__AutoProvision=true \
  Auth__Oidc__RequireHttpsMetadata=false \
  dotnet run --project src/api --no-launch-profile -c Release
```

Same host-run requirement as Zitadel and for the same reason: the issuer URL
has to resolve identically from the browser and from the app, which a
containerised API cannot do against a host-published `localhost` port.

## A wire-level difference worth knowing: PAR

Keycloak's discovery document advertises a `pushed_authorization_request_endpoint`.
The .NET OIDC handler uses PAR automatically whenever a provider advertises
it — nothing to configure, on either side — so the browser's authorize
redirect carries only `client_id` and an opaque `request_uri`, with
everything else (scope, `code_challenge`, state, and the `response_mode`
this app pins per [`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#other-providers))
pushed ahead of time over a back-channel POST instead of sitting in the
query string. Confirmed against Entra and Zitadel that this is Keycloak-only
here — neither advertises the endpoint. It changes only how the request is
carried, not what it contains, so it needed nothing from the #114 fix and
did not reproduce it: correlation still resolved cleanly end to end.

For everything else — production notes, `AutoProvision`, keeping a local
admin — see [`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#production-notes),
which is not Zitadel-specific despite the name.
