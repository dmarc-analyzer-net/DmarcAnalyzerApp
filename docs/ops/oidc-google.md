# OIDC Login (Google) — Dev Setup

A fourth provider for exercising the OIDC login front door, alongside Zitadel
([`docs/ops/oidc-zitadel.md`](oidc-zitadel.md)), Keycloak
([`docs/ops/oidc-keycloak.md`](oidc-keycloak.md)) and Authentik
([`docs/ops/oidc-authentik.md`](oidc-authentik.md)) — see the Zitadel file's
"How it works" if you have not read it.

Google is the odd one out of the four: there is **no dev container for it**.
The other three are self-hostable and the compose file runs one on
`localhost`; Google is a real, external identity provider, and its OAuth
consent screen and client registration only exist inside a real Google Cloud
project. Testing it means a real Google Cloud project and a real
HTTPS-reachable deployment — there is nothing to `docker compose up`.

## One-time Google Cloud setup

1. **Create a project** — [console.cloud.google.com/projectcreate](https://console.cloud.google.com/projectcreate).
   Any name; a throwaway project for this alone keeps it out of anything else
   in your account.

2. **Configure the OAuth consent screen** — *APIs & Services → OAuth consent
   screen* (Google's newer console calls this "Google Auth Platform").
   - User type **External**, unless the project sits under a Google Workspace
     organisation and you deliberately want **Internal**.
   - **Publishing status: leave it on "Testing"**. This skips Google's app
     verification process entirely, at the cost of only your own listed
     **test users** being able to sign in — add your own Google account under
     *Audience → Test users*, or the consent screen refuses you too.

3. **Create the client** — *APIs & Services → Credentials → Create
   Credentials → OAuth client ID*, type **Web application**. Add the redirect
   URI:
   ```
   https://dmarc.example.com/api/v1/auth/oidc/callback
   ```
   Google's discovery document is fixed at `https://accounts.google.com` —
   there is no per-project or per-tenant issuer path the way Entra has a
   tenant ID.

## The client secret is shown exactly once

Google never lets you view or download an existing client secret again,
unlike Entra, Zitadel, Keycloak or Authentik, all of which keep some way to
inspect what is already configured. Close that one-time reveal dialog without
copying it correctly, and the console will tell you plainly: *"Viewing and
downloading client secrets is no longer available."*

There is no recovery — only rotation. Open the client, use **Add secret** to
generate a new one (up to two secrets can exist at once), verify a real login
succeeds against the new one, then **Disable** the old one. Don't delete the
old secret until the new one is confirmed working; disabling is reversible,
deleting is not.

This bit us for real while testing: a secret transcribed by reading it off a
screenshot rather than copying the actual value came out wrong in a way that
was not visually obvious, and every token exchange failed with
`invalid_client: The provided client secret is invalid.` until it was
rotated and copied for real (system clipboard, not read off the rendered
page). If you must capture the reveal dialog for documentation, copy the
value through the console's own copy button and paste it directly into
wherever it needs to live — never retype or transcribe it from what is
rendered on screen.

## Behind an ingress, this reproduces the #114-class bug immediately

Testing Google SSO requires deploying behind a real reverse proxy or ingress
— Google will not accept a plain HTTP redirect URI for anything but a
loopback address — and that surfaces the same failure class
[`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#other-providers) already
documents for `ResponseMode`, but earlier in the flow: the callback request
Google sends still succeeds, then the app's own authorize request to Google
carries the **wrong scheme**.

Confirmed against a real cluster: the app built `redirect_uri=http://…` and
sent it to Google's authorize endpoint even though the deployment was only
ever reached over `https://`. Google refuses this outright —
`redirect_uri_mismatch`, with an error page stating the registered URI does
not match — before any sign-in prompt appears. The cause is the same as
every other place this shows up: `Network__UseForwardedHeaders` is off by
default, so the app does not believe `X-Forwarded-Proto` from the ingress and
falls back to what Kestrel itself sees, which is plain HTTP behind almost any
proxy setup.

```yaml
environment:
  Network__UseForwardedHeaders: "true"
  Network__TrustedNetworks__0: "<proxy-or-pod-network-CIDR>"
```

See [running behind a reverse proxy](https://dmarc-analyzer.net/docs/reverse-proxy/)
for how to find the right CIDR for your ingress controller. Get this wrong
and the failure looks identical for every provider that checks the redirect
URI strictly — which, per the generic guide, is most of them.

## Running the app with OIDC

```yaml
environment:
  Auth__Oidc__Enabled: "true"
  Auth__Oidc__Authority: "https://accounts.google.com"
  Auth__Oidc__ClientId: "<client-id>.apps.googleusercontent.com"
  Auth__Oidc__ClientSecret: "<client-secret>"
  Auth__Oidc__DisplayName: "Google"
  Auth__Oidc__AutoProvision: "true"
  Auth__Oidc__DefaultRole: "client_viewer"
```

No PAR here either — confirmed the authorize redirect carries `response_type`,
`code_challenge` and no `response_mode` directly in the query string, the same
shape as Entra, Zitadel and Authentik. Keycloak remains the only one of the
five that uses PAR.

Verified end to end: login provisioned the existing local account rather
than duplicating it — matched by verified email, since a local admin already
existed at the same address — and `user_identity` recorded exactly one row,
issuer `https://accounts.google.com`, linked to that one user.

For everything else — production notes, `AutoProvision`, keeping a local
admin — see [`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#production-notes),
which is not Zitadel-specific despite the name.
