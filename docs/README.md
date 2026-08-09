# Documentation

## Using DMARC Analyzer

**User documentation lives at [dmarc-analyzer.net/docs](https://dmarc-analyzer.net/docs/)** —
installation, the full configuration reference, connecting a mailbox, single
sign-on, upgrades and backup, and troubleshooting.

Quickest possible start (details in
[Install with Docker](https://dmarc-analyzer.net/docs/install/)):

```bash
curl -fsSL -o compose.yml https://raw.githubusercontent.com/dmarc-analyzer-net/DmarcAnalyzerApp/main/deploy/compose.yml
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d
```

## What's in this folder

| Path | Audience | Contents |
|---|---|---|
| [`ops/`](ops/) | operators & maintainers | Runbooks: [configuration reference](ops/configuration.md) (canonical — `ConfigurationContractTests` fails the build if it drifts), [cutting a release](ops/release.md), [migrating a running instance](ops/migrating-a-running-instance.md), [mailbox sync](ops/mailbox-sync.md), [OIDC with Zitadel](ops/oidc-zitadel.md), [directory listings](ops/directory-listings.md) |
| [`planning/`](planning/) | contributors | **Internal design artifacts** — architecture, data model, API contract, roadmap, backlog, ADRs |

> **`planning/` is not product documentation.** It is where we work out what to
> build, so it contains unbuilt ideas, open questions, and a backlog of `todo`
> items. Judging the software by it will mislead you — read
> [`planning/status.md`](planning/status.md) for what actually exists today, or the
> [user docs](https://dmarc-analyzer.net/docs/) if you just want to run it.

## Contributing

Start with [`planning/status.md`](planning/status.md) (what's built) and
[`planning/backlog.md`](planning/backlog.md) (what's next, prioritised — the source
of truth for picking work up). Then:

- [`planning/architecture.md`](planning/architecture.md) — components and runtime modes
- [`planning/data-model.md`](planning/data-model.md) — schema as built, plus planned tables
- [`planning/api-contract.md`](planning/api-contract.md) — §0 lists every implemented endpoint
- [`planning/adr/`](planning/adr/README.md) — architecture decision records

Repository conventions are in [`AGENTS.md`](../AGENTS.md) at the repo root.

## Keeping docs honest

The user docs on the website describe **this** code, from a separate repository. If
you change configuration keys, defaults, endpoints, or setup steps, update
[the docs site](https://github.com/dmarc-analyzer-net/dmarc-analyzer-net.github.io/tree/main/src/content/docs)
in the same change — particularly
[`configuration.md`](https://github.com/dmarc-analyzer-net/dmarc-analyzer-net.github.io/blob/main/src/content/docs/configuration.md),
which enumerates every environment variable.
