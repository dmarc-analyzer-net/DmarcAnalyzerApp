# OIDC Login (Microsoft Entra ID)

One of five providers the OIDC login front door has been wired against,
alongside Zitadel ([`docs/ops/oidc-zitadel.md`](oidc-zitadel.md)), Keycloak
([`docs/ops/oidc-keycloak.md`](oidc-keycloak.md)), Authentik
([`docs/ops/oidc-authentik.md`](oidc-authentik.md)) and Google
([`docs/ops/oidc-google.md`](oidc-google.md)) — see the Zitadel file's "How it
works" if you have not read it.

Entra is the one with the most provider-specific footguns, and all three of the
ones below bite on a first login rather than later, which is the worst place for
them: the deployment looks configured and the failure looks like something else.

## Register the app

*Microsoft Entra admin center → App registrations → New registration.*

1. **Redirect URI: platform "Web"**, value:
   ```
   https://dmarc.example.com/api/v1/auth/oidc/callback
   ```
2. **Create a client secret** — *Certificates & secrets → New client secret*.
   Copy the **Value**, not the Secret ID.
3. **Copy the tenant ID** from *Overview*; the authority is
   `https://login.microsoftonline.com/<tenant-id>/v2.0`.

### Entra needs a client secret

A *Web*-platform redirect URI makes the registration a confidential client, so
the token exchange requires `Auth__Oidc__ClientSecret` no matter what PKCE does
— without one it fails `AADSTS7000218`. "Allow public client flows" does not
change this; it is for device-code and native apps.

### Use your tenant's authority, not `/common`

`https://login.microsoftonline.com/common/v2.0` accepts sign-ins from *any*
Entra tenant, and email addresses in another tenant are asserted by whoever
administers that tenant, not by you. Anyone can stand up a tenant. Use the
tenant-specific issuer unless you genuinely intend to accept the whole world,
and read the `TrustUnverifiedEmail` section below before you do.

## Entra issues no `email_verified` — configure `xms_edov` instead

This is issue [#140](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues/140),
and it is the one that catches people who did everything else right.

The app links an SSO login to an **existing** local account by email, and only
when the provider vouches for the address — otherwise an identity provider that
lets a user type any address into their profile could take over an
administrator's account. Every other provider here answers that question with
the standard `email_verified` claim. **Entra does not issue it at all**, in any
token, for any account type. Microsoft's position is that the source attributes
behind the `email` claim (`mail`, `otherMails`, UPN) are mutable and not
uniformly verified, so it declines to assert what it cannot back — the
cross-tenant takeover class this guards against has a name, *nOAuth*.

What Entra offers instead is the **`xms_edov`** optional claim — "email domain
owner verified" — a boolean saying whether the address sits in a domain whose
ownership the tenant has proven. The app treats it exactly like
`email_verified`, so configuring it makes SSO work with no other change:

*App registration → Token configuration → Add optional claim → **ID** →* add
both **`email`** and **`xms_edov`**. If the picker does not list `xms_edov`,
add it through *Manage → Manifest*:

```json
"optionalClaims": {
  "idToken": [
    { "name": "email", "essential": false },
    { "name": "xms_edov", "essential": false }
  ]
}
```

Add `email` while you are there even if logins already carry one: without an
email claim the app cannot match or provision anything and refuses with
`no_account`.

### Checking whether it arrived

Log in and read the API log. A refusal now names both claims it looked for:

```
warn: DmarcAnalyzer.Api.Application.Auth.OidcSignInService[0]
      OIDC login for user@example.com refused: the provider asserted neither
      email_verified nor xms_edov, so the address cannot be trusted to identify
      the existing account.
```

If that line still appears after configuring the optional claim, the claim is
not reaching the app — re-check that it was added to the **ID token** and that
the authority is the `/v2.0` endpoint. `xms_edov` is a v2.0 claim; a v1.0
authority will not send it.

### `TrustUnverifiedEmail`, and when it is defensible

Some tenants cannot get the optional claim configured — no admin access to the
registration, a policy against manifest edits, a federated setup where it comes
back `false` for legitimate users. For those:

```yaml
environment:
  Auth__Oidc__TrustUnverifiedEmail: "true"
```

This trusts **silence only**. A provider that explicitly answers
`email_verified=false` or `xms_edov=false` is still refused, so turning it on
for Entra does not quietly weaken another provider configured later.

It is defensible on a **tenant-specific** authority, where every mailbox is
administered by the same organisation that runs the tenant — asserting a
colleague's address there requires already being an administrator of it. It is
**not** defensible on `/common` or `/organizations`: any tenant on earth can
then assert any address, and the first login from a hostile one lands
straight on your `agency_admin`. If you need multi-tenant sign-in, get
`xms_edov` configured instead.

Users who have *no* local account are unaffected either way — provisioning
never consults email assurance, because a new account lands on
`Auth__Oidc__DefaultRole` and there is nothing to take over. That asymmetry is
why Entra deployments running with `AutoProvision=true` never noticed #140:
only users who already had an account were locked out.

### Testing this without an Entra tenant

The dev Keycloak ([`docs/ops/oidc-keycloak.md`](oidc-keycloak.md)) reproduces
the Entra token shape exactly, which is how #140 was confirmed and fixed
without tenant access. Two changes to a realm, both through the admin console
or its REST API:

- **Delete the "email verified" protocol mapper** from the realm's built-in
  `email` client scope. The ID token then carries `email` and no
  `email_verified` — Entra's shape.
- **Add a hardcoded claim mapper** on the client (`xms_edov`, value `true`,
  JSON type **boolean**) to stand in for the optional claim. Flip the value to
  `false` to check that a denial is still honoured.

Keycloak emits a real JSON boolean either way, so this exercises the same claim
parsing a live tenant would.

## The callback must come back as a redirect, not a form post

Issue [#114](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues/114),
and the reason Entra logins failed outright up to and including 0.7.0. The
Microsoft handler defaults `ResponseMode` to `form_post`, which arrives as a
cross-site POST and therefore carries no `SameSite=Lax` cookie — so the
correlation cookie written during the challenge goes missing on return and every
login dies with "Correlation failed". The app now pins `ResponseMode = "query"`,
so there is nothing to configure; the failure is worth recognising because the
log names a cookie, which reads like a cookie bug rather than a response mode
one. Zitadel happens to answer with a query redirect regardless, which is why
the dev stack never showed it.

## Running the app with OIDC

```yaml
environment:
  Auth__Oidc__Enabled: "true"
  Auth__Oidc__Authority: "https://login.microsoftonline.com/<tenant-id>/v2.0"
  Auth__Oidc__ClientId: "<application-client-id>"
  Auth__Oidc__ClientSecret: "<client-secret-value>"
  Auth__Oidc__DisplayName: "Microsoft"
  Auth__Oidc__AutoProvision: "false"
  Auth__Oidc__DefaultRole: "client_viewer"
```

Behind an ingress, set `Network__UseForwardedHeaders` too — without it the app
builds `redirect_uri=http://…` from what Kestrel sees and Entra refuses the
mismatch before any sign-in prompt. See
[`docs/ops/oidc-google.md`](oidc-google.md#behind-an-ingress-this-reproduces-the-114-class-bug-immediately),
where the same failure is worked through in full.

For everything else — production notes, `AutoProvision`, keeping a local admin
— see [`docs/ops/oidc-zitadel.md`](oidc-zitadel.md#production-notes), which is
not Zitadel-specific despite the name.
