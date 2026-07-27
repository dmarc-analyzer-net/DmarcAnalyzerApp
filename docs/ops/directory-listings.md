# Directory listings

Two directories reach self-hosters better than any page we could rank for
deployment keywords — the reasoning is in the `seo` repo's content plan. This is
the state of each.

## Artifact Hub — ready, needs one browser step

`Chart.yaml` carries the `artifacthub.io/*` annotations, so category, links,
maintainer and screenshot all populate from the chart itself. Registering the
repository needed a browser once; everything after that is automated — the `chart`
CI job pushes `artifacthub-repo.yml` to the OCI namespace on each tag, which is
what earns the Verified publisher badge.

It is done in CI rather than by hand because `GITHUB_TOKEN` already carries
`packages:write` and a personal `gh` token does not — a local `oras push` fails
with `permission_denied: The token provided does not match expected scopes` until
someone runs `gh auth refresh -s write:packages`, which is exactly the kind of
step that gets forgotten.

**Why it is worth the ten minutes:** searching "dmarc" on Artifact Hub returns
**two** packages, both `dmarc2logstash`, both at **zero stars** (checked
2026-07-26). Nobody holds this category.

## awesome-selfhosted — blocked on a category decision, not on effort

The entry itself is trivial to write and the project meets the curation bar:
Apache-2.0, actively developed, working software, real documentation. There is no
minimum-age rule — the "6-12 months" line in their CONTRIBUTING is about *removing*
inactive projects, not admitting new ones.

**The problem is that no category fits**, which is worth knowing before spending a
pull request on it:

- `Communication - Email - Complete Solutions` holds mail servers (Mox, Maddy).
  We are not a mail server, and the only two DMARC mentions in the entire list are
  those servers listing it as a feature.
- `Monitoring & Status Pages` has `redirect:` set, which per their CONTRIBUTING
  means **no software may reference it** — the maintainers deliberately send
  monitoring tools to `awesome-sysadmin`.
- `awesome-sysadmin`'s monitoring section is infrastructure monitoring (Nagios,
  Cacti, checkmk). A DMARC report analyzer is not that either.
- `Analytics` is web analytics in practice — Umami, Plausible-likes, Superset.
- That leaves `Miscellaneous`, which is legitimate but is where things go to not
  be found. Most of the value of this listing is category browsing.

### The better option, if it is wanted

Their rule is that **a new tag needs at least three projects referencing it**, and
there are four self-hostable DMARC report analyzers: this one, `parsedmarc`,
`cry-inc/dmarc-report-viewer` and `tierpod/dmarc-report-converter`. None of the
other three is currently listed at all.

So proposing an `Email Security` (or `DMARC & Email Deliverability`) tag and
seeding it with all four is both allowed and genuinely useful to that list — and
it would define a category we lead rather than adding one row to a junk drawer.

It is also a larger, more presumptuous pull request into someone else's project,
and it means writing entries for three competitors. That is a judgement call for a
human, which is why this is written down rather than submitted.

### If we go the simple route instead

`software/dmarc-analyzer.yml`, tag `Miscellaneous`:

```yaml
name: DMARC Analyzer
website_url: https://dmarc-analyzer.net
source_code_url: https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp
description: Collect and analyse DMARC aggregate reports from your own mailbox, with per-client dashboards, alerting and multi-tenant separation for agencies.
licenses:
  - Apache-2.0
platforms:
  - Docker
  - .NET
tags:
  - Miscellaneous
```

Their CI fills in stars, release and commit history, so those fields are omitted.
