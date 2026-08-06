# OIDC Login (Authentik) — Dev Setup

A third dev-only identity provider, alongside Zitadel
([`docs/ops/oidc-zitadel.md`](oidc-zitadel.md)) and Keycloak
([`docs/ops/oidc-keycloak.md`](oidc-keycloak.md)). The concepts are the same
either way — see the Zitadel file's "How it works" if you have not read it —
this covers only what differs.

Authentik is the odd one out of the three in two ways: it splits the client
into **two objects** (a provider and an application), and it defaults to a
**confidential** client, so unlike Zitadel and Keycloak you do end up with a
secret to configure.

## Starting it

Behind a compose profile, because it is three containers rather than one:

```bash
docker compose --profile authentik up -d   # console at http://localhost:8086
```

Give it about 45 seconds on first start, and do not trust the open port. The
Go router binds `:9000` roughly a second after the container starts, while the
Python application behind it is still applying some 500 migrations — measured
here as a listening port at t+1s and an actual app at t+46s. A container that
is `running` with its port mapped is therefore not yet a working instance.
What to wait for is this line in the server log:

```
docker compose logs -f authentik-server | grep 'Listening at'
```

Either container may do the migrating — both try, and a database lock decides
which one wins. The server took it here; the worker started, found the work
done and moved on. So there is no single log to watch for progress, which is
the other reason to wait on the line above rather than on the worker.

No Redis, despite every upstream compose example still shipping one. As of
2026.x Authentik keeps its cache and channel layers in Postgres
(`django_postgres_cache`, `django_channels_postgres` in the migration log), so
server + worker + database is the whole stack. Don't copy a Redis service back
in from upstream docs without checking it is actually wanted.

## One-time Authentik setup

1. **Sign in** at `http://localhost:8086/if/admin/` as `akadmin` / `admin`.

   Those come from `AUTHENTIK_BOOTSTRAP_EMAIL` and
   `AUTHENTIK_BOOTSTRAP_PASSWORD` in `docker-compose.yml`, the equivalent of
   Keycloak's `KC_BOOTSTRAP_ADMIN_*`. Without them a fresh instance has an
   `akadmin` with no usable password and the sign-in page gives no hint about
   it — you would have to find `/if/flow/initial-setup/` yourself and invent
   one. They only apply on first start, so changing them later does nothing to
   an existing volume.

2. **Create the provider.** *Applications → Providers → Create*, then pick
   **OAuth2/OpenID Provider** from the type list.

   - *Authorization flow* — `default-provider-authorization-implicit-consent`
     for a dev instance (no consent screen). The `-explicit-consent` variant
     shows an "Authorize Application" prompt on first login instead; both work.
   - *Client Type* — leave **Confidential** (the default). Copy the **Client
     ID** and **Client Secret** it generates.
   - *Redirect URIs* — add one entry, mode **Strict**, type **Authorization**:
     - `http://localhost:8081/api/v1/auth/oidc/callback` (compose API port)

3. **Create the application and link it.** *Applications → Applications →
   Create*: a Name, a **Slug**, and the provider from step 2 in the *Provider*
   field.

   **The slug is load-bearing.** Authentik's `issuer_mode` defaults to
   per-provider, which makes the issuer
   `http://localhost:8086/application/o/<slug>/` — the *application's* slug,
   not the provider's name. Get it wrong and discovery 404s before any login
   is attempted.

4. **Check the link.** *Applications → Providers* marks an unlinked provider
   "Provider not assigned to any application" with a warning icon. A provider
   in that state still has a client ID and secret and still looks configured
   from the app's side, but it has no issuer and cannot be logged into — it is
   the easiest mistake to make here, since creating the provider first feels
   like the whole job.

5. **Read the issuer off the provider.** Its *Overview* tab shows **OpenID
   Configuration Issuer** — that exact string is `Auth__Oidc__Authority`.

6. **Users.** `akadmin` is enough to test with, and the email you set in step 1
   is the one the app links on. For a second user, *Directory → Users →
   Create*, set a password under its *Credentials*, and give it an email —
   the app links an SSO identity to a local account by verified email.

## Running the app with OIDC

```bash
env APP_MODE=api ASPNETCORE_URLS=http://localhost:8081 \
  ConnectionStrings__Default="Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres" \
  Database__MigrateOnStartup=true \
  Security__CredentialEncryptionKey="<your key>" \
  Auth__Oidc__Enabled=true \
  Auth__Oidc__Authority=http://localhost:8086/application/o/dmarc-analyzer/ \
  Auth__Oidc__ClientId="<client id from step 2>" \
  Auth__Oidc__ClientSecret="<client secret from step 2>" \
  Auth__Oidc__DisplayName=Authentik \
  Auth__Oidc__DefaultRole=client_viewer \
  Auth__Oidc__AutoProvision=true \
  Auth__Oidc__RequireHttpsMetadata=false \
  dotnet run --project src/api --no-launch-profile -c Release
```

Same host-run requirement as the other two and for the same reason: the issuer
URL has to resolve identically from the browser and from the app, which a
containerised API cannot do against a host-published `localhost` port.

Note the trailing slash on `Authority`. Authentik's discovery document reports
the issuer with one, and the handler compares the two strings exactly.

## Its defaults are wide open on grant types

A provider created through either creation path — provider-first, or
*Applications → Create with Provider* — comes out with **every** grant type
enabled: authorization code, implicit, hybrid, refresh token, client
credentials, password, and device code. Verified on two separately created
providers, neither of which had its Grant Types touched.

The app only ever uses authorization code (plus refresh token), so the rest is
attack surface that nothing asks for — including the resource-owner password
grant, which turns the provider into a password-checking API, and implicit,
which is deprecated for exactly the reasons the code flow exists. Untick
everything except **Authorization Code** and **Refresh token**.

This is a bigger gap than the Keycloak one noted in
[`docs/ops/oidc-keycloak.md`](oidc-keycloak.md): Keycloak's default merely
leaves PKCE unenforced, whereas this one leaves flows enabled.

## Two bits of harmless noise

**`Failed to fetch outpost configuration … 403 Forbidden`**, repeating in the
server log. That is the bundled embedded outpost, which exists for proxy and
LDAP providers. Nothing on the OIDC path touches it and it does not need
fixing to test a login.

**`POSTGRES_PASSWORD … Environment variable not found, using fallback`**, twice
at startup. Authentik's entrypoint probes that name before falling back to the
`AUTHENTIK_POSTGRESQL__PASSWORD` the compose file actually sets. It connects
fine; the warning is about the variable it looked for first.

## Wire-level: no PAR here

Unlike Keycloak, Authentik does not advertise a
`pushed_authorization_request_endpoint`, so the .NET handler builds an ordinary
front-channel authorize request with everything in the query string —
`response_type=code`, `code_challenge`, `state`, and no `response_mode` (see
[`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#other-providers) for why that
omission is deliberate). Same as Entra and Zitadel; Keycloak remains the only
one of the four that uses PAR.

Confirmed against a real login: the callback arrives as a top-level GET,
correlation resolves, and the user is provisioned. Authentik never reproduced
the `Correlation failed` of issue #114, and the fix is what keeps that true —
its own default would have been a form-post callback.

## The `sub` claim is a hashed user ID

`sub_mode` defaults to `hashed_user_id`, so the subject is an opaque hash
rather than a username or email. That is fine and preferable here: the app
matches returning users on issuer plus subject, so a stable opaque value is
exactly what it wants, and a later email or username change at the provider
does not strand the account.

For everything else — production notes, `AutoProvision`, keeping a local
admin — see [`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#production-notes),
which is not Zitadel-specific despite the name.
