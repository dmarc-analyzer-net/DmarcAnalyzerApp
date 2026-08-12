# Contributing

Thanks for considering it. This file is short on purpose: it describes what this
project actually does, not process it does not enforce.

## Licensing and paperwork

**There is no CLA and no contributor agreement to sign.** The project is
Apache-2.0, and section 5 of that licence already covers inbound contributions —
anything you deliberately submit for inclusion is licensed under the same terms,
unless you say otherwise. Opening a pull request is all the paperwork there is.

You keep the copyright in what you write.

## Before you build something large

Open an issue first and describe the shape of it. Not for permission — for the
much duller reason that this codebase has a few decisions that are load-bearing
and not obvious from the outside, and it is miserable to discover one of them in
review after a week of work. The
[architecture decision records](docs/planning/adr/README.md) are where those
decisions live, and
[`docs/planning/backlog.md`](docs/planning/backlog.md) is the prioritised list of
what is wanted, in what order, and why.

A design issue that gets a "yes, and here is the constraint you will hit" is
worth more than a large pull request that gets a "this cannot merge as shaped".

Small, self-contained fixes need none of this. Just send them.

## Orientation

[`AGENTS.md`](AGENTS.md) is the map: layout, stack, commands, the domain concepts
that are easy to misread, and the deployment topologies. It is written for both
new contributors and AI coding agents. Read it before the code.

The short version:

```bash
# Backend
dotnet build DmarcAnalyzerApp.slnx
dotnet test src/api.tests                 # fast; InMemory provider
dotnet test src/api.integration.tests     # needs Docker; starts PostgreSQL

# Frontend (from src/web)
npm install
npm run build     # tsc -b && vite build
npm test          # vitest run
npm run lint      # eslint .

# Full stack in Docker (api + worker + postgres)
echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d --build
```

## How work gets merged

- `main` is protected. Branch, implement, verify, open a pull request.
- **Merges are squash merges**, and branches are not auto-deleted afterwards.
- CI runs on every pull request, whatever branch it targets, so a stacked pull
  request is built and tested like any other. If you see *no* checks at all,
  the usual cause is a merge conflict: GitHub does not run checks on a pull
  request it cannot merge.
- Reference issues with `Refs #123` rather than `Closes #123`. Merging is not the
  same as shipping, and closing an issue is the maintainer's call.

## What review will ask of you

**Tests that could actually fail.** The fast backend suite runs on the EF Core
InMemory provider, which supports neither raw SQL nor transactions. Anything
whose correctness depends on the real database — the report/records insert, the
`ON CONFLICT` dedup, a unique index, a column width — belongs in
`src/api.integration.tests`, which starts a PostgreSQL container and can
actually execute it. If you touch one of those paths and cannot cover it there,
say in the pull request how you verified it instead. "Ran it against real
Postgres and here is what I saw" is a valid and welcome answer. Silence is not.

**Say what you actually ran.** Pull request descriptions here list the commands
and their results. This is the single most useful thing you can do for a
reviewer, and it is the local convention.

**Configuration documented in the same change.** Every setting lives in
[`docs/ops/configuration.md`](docs/ops/configuration.md), and
`ConfigurationContractTests` fails the build if code and that file disagree. Add
the setting in both places or CI will tell you.

**Comments that explain why.** The existing code comments the reasoning behind
non-obvious decisions, not the mechanics of the line below. Matching that is
worth more than matching the formatting.

## Things that look like mistakes and are not

A few names are deliberately frozen because they are *data already written*
rather than references to current code. Changing them looks like tidying and is
actually a breaking change:

- **Audit event and target types** (`mailbox_source.created`, and similar) exist
  in `audit_event` rows on every install, and the console filters on them
  literally. Renaming one splits history in two.
- **Config export entity keys** appear in every artifact an install has ever
  written, and the importer accepts any artifact at or below the current format
  version. Renaming one silently changes the meaning of documents already on
  disk.

Each is commented where it is defined. If a rename seems obviously missing, check
for that comment before "finishing" it.

## Security problems

Do not open a public issue. See [`SECURITY.md`](SECURITY.md).

## Code of conduct

There is no separate document, and one sentence covers it: be decent to people.
Behaviour that makes the project worse to participate in gets addressed by the
maintainer, up to and including blocking. That is the whole policy.
