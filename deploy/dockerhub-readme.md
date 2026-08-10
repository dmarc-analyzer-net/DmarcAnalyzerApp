# DMARC Analyzer

Open-source, self-hosted DMARC monitoring for agencies. Point it at the mailbox
your `rua=` reports arrive in and it collects, parses and charts them — unlimited
domains, many clients, no per-domain pricing, and the report data never leaves
your infrastructure.

- **DMARC aggregate reports** — parsed, charted, grouped per client domain
- **MTA-STS and TLS-RPT** — policy hosting and SMTP TLS failure reports alongside
- **Single sign-on** — OIDC, including Microsoft Entra ID, with SSO-only users
- **Multi-tenant** — per-client separation and access, built for agencies

**[Documentation](https://dmarc-analyzer.net/docs/)** ·
**[Install guide](https://dmarc-analyzer.net/docs/install/)** ·
**[Source](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp)**

## Quick start

```bash
mkdir dmarc-analyzer && cd dmarc-analyzer
curl -fsSL -o compose.yml https://raw.githubusercontent.com/dmarc-analyzer-net/DmarcAnalyzerApp/main/deploy/compose.yml
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d
```

Open <http://localhost:8080> and create the first administrator account. Two
containers: this one, and PostgreSQL.

> Keep `DMARC_ENCRYPTION_KEY` safe and backed up — it decrypts your stored mailbox
> passwords. Lose it and every mailbox source has to be re-entered.

## Runtime modes

One image, selected with `APP_MODE`:

| Value | Runs |
|---|---|
| `all` | console **and** report ingestion in one process — what the compose file uses |
| `api` | console only |
| `worker` | ingestion only |
| `migrate` | applies pending database migrations and exits |

Any other value fails startup rather than falling back, so a typo cannot leave you
with a container that serves the console and quietly ingests nothing.

**Only one ingestion worker may run against a database.** The process takes a
PostgreSQL advisory lock and a second worker exits rather than duplicating every
sync pass.

## Configuration

Environment variables, the same set here and on Kubernetes. The complete list is
in the [configuration reference](https://dmarc-analyzer.net/docs/configuration/).
The two that matter on day one:

| Variable | |
|---|---|
| `ConnectionStrings__Default` | Npgsql connection string |
| `DATABASE_URL` | `postgres://user:pass@host/db` instead, if your platform sets it |
| `Security__CredentialEncryptionKey` | base64 32 bytes; `openssl rand -base64 32` |

## Tags

`latest` tracks releases. Pin a version (`0.10.0`) for anything you depend on —
it makes upgrades explicit and rollbacks unambiguous. `edge` tracks `main` and is
unreleased.

Built for `linux/amd64` and `linux/arm64`, so a Raspberry Pi or Apple Silicon
machine works.

## Also available

- **GHCR** — `ghcr.io/dmarc-analyzer-net/dmarc-analyzer`, no anonymous pull rate limits
- **Helm** — `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer`

Apache-2.0. Issues and questions:
[github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/issues)
