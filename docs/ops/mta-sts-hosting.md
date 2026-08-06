# Hosting MTA-STS policies

MTA-STS (RFC 8461) lets a domain tell sending mail servers to require verified
TLS when delivering to it. Publishing the policy needs two DNS records and an
HTTPS endpoint serving a small text file at
`https://mta-sts.<domain>/.well-known/mta-sts.txt` — and that per-domain HTTPS
endpoint, with a valid certificate, is why most fleets skip MTA-STS entirely.

This app serves those policy files itself. Onboarding a client domain becomes
one CNAME plus one TXT record; no per-domain web hosting, no per-domain
certificate management inside the app. TLS termination stays with your reverse
proxy, where Caddy's on-demand TLS makes per-domain certificates automatic.

## Enable it

1. Set `MtaSts__PolicyHost` to the hostname client CNAMEs will point at
   (e.g. `sts.your-agency.example`) so the console can show complete publish
   instructions. Point that name at your proxy.
2. On the domain's detail page, under **Transport security → Hosted policy**,
   create the policy (admin only): mode, max age, and the `mx` patterns that
   cover the domain's mail exchangers. Start in `testing`.
3. Publish the two records the console shows:
   - `CNAME mta-sts.<domain>` → your `MtaSts__PolicyHost`
   - `TXT _mta-sts.<domain>` → `v=STSv1; id=<the shown id>`
4. Use **Recheck now** — the monitoring pass validates the hosted policy
   exactly like an external one, and the card flips from setup guidance to
   green once DNS and the proxy are wired.

Editing a policy changes its id whenever the content changes; the console
calls out the new TXT value to publish. Senders only refetch the policy when
the id moves, so skipping the TXT update strands them on the old policy until
`max_age` expires. Applying one shape to many domains of a client at once is
in the editor ("Also apply to other domains in this client"); the result lists
exactly the TXT records that need updating.

## TLS termination with Caddy (recommended)

Caddy's on-demand TLS issues a certificate the first time a hostname is
requested — but only after asking the app whether the name is one it serves,
so strangers pointing DNS at your instance cannot mint certificates:

```caddyfile
{
	on_demand_tls {
		ask http://127.0.0.1:8080/mta-sts/ask
	}
}

# The console — explicit host, ordinary certificate.
dmarc.your-agency.example {
	reverse_proxy 127.0.0.1:8080
}

# Any mta-sts.<client-domain> a client CNAMEs at this instance.
https:// {
	tls {
		on_demand
	}
	reverse_proxy 127.0.0.1:8080
}
```

`reverse_proxy` preserves the Host header by default, which is what the policy
route keys on. `AllowedHosts` must stay `*` (the default) — the app has to
accept arbitrary `mta-sts.<domain>` hosts.

Nginx and Traefik have no equivalent of on-demand TLS across unrelated
registrable domains; there, either automate a certificate per client domain
(cert-manager on Kubernetes, one Ingress host per domain, all routing to the
same Service — the chart's `ingress.hosts` list already supports this) or put
a small Caddy in front for the mta-sts hosts only.

## A dedicated policy-host container (`APP_MODE=mta-sts`)

Serving from the main app works, and is the default. If you would rather not
point internet traffic at the container that also serves your console, run a
second container of the same image with `APP_MODE=mta-sts`: it serves only
`/.well-known/mta-sts.txt`, `/mta-sts/ask`, and the health probes — no
console, no API, no auth stack, no worker. Point the Caddy blocks above at it
instead.

```yaml
services:
  mta-sts:
    image: ghcr.io/dmarc-analyzer-net/dmarc-analyzer:latest
    restart: unless-stopped
    environment:
      APP_MODE: mta-sts
      ConnectionStrings__Default: "Host=postgres;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}"
    ports:
      - "8090:8080"
    depends_on:
      app:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/health/ready"]
      interval: 30s
      timeout: 5s
      retries: 5
```

Notes on this shape:

- **It never migrates.** The main app (or a `migrate` run) owns the schema;
  the `depends_on` gate above is what keeps a fresh stack ordered. Its
  readiness probe checks that the policy table exists, so it reports unready
  — rather than healthy-but-500ing — until the schema is there.
- **It needs no `Security__CredentialEncryptionKey`** — a feature of the
  isolation: the internet-facing container holds nothing that can decrypt
  mailbox credentials.
- **Console edits propagate within `MtaSts__ServeCacheSeconds`** (default 60)
  — each process caches rendered policies briefly. Negligible against
  `max_age` values measured in days.

## Turning a policy off, or leaving

- **Hosting off** (the toggle in the editor) keeps the settings and answers
  404 — useful mid-setup. The record id does not change.
- **Delete** removes the policy entirely. Remove the client's `mta-sts` CNAME
  and `_mta-sts` TXT records too, or senders that still see the TXT record
  will find a broken policy host and the monitoring card will say so.
- To retire MTA-STS *gracefully* (senders may have the policy cached), switch
  the mode to `none` first and let `max_age` pass before removing records —
  the RFC's documented exit path.

## Troubleshooting

- **The policy URL returns the console UI** — the image predates this
  feature; upgrade. The route answers 404 in plain text for unknown hosts,
  never HTML.
- **404 for a domain you host** — the domain must be active, the policy
  enabled, and the request's Host header exactly `mta-sts.<domain>`. Behind a
  proxy, confirm it forwards the original Host.
- **Certificate errors on `mta-sts.<domain>`** — the `ask` endpoint answers
  200 only for enabled policies on active domains; a 403 in Caddy's log means
  the app does not (yet) serve that name.
- **The card says "Waiting for DNS"** — the monitoring pass has never managed
  to fetch the policy from the public side. Check the CNAME, the proxy route,
  and then Recheck now.
