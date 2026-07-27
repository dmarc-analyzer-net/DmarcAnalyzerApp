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

## The wider landscape

Surveyed 2026-07-27. Every count below was queried live, not recalled.

| Where | DMARC tools already there | Us | Effort |
|---|---|---|---|
| **Docker Hub** | — | **description was empty through 2,177 pulls** | done: CI syncs it on release |
| **GHCR** | — | description comes from the Dockerfile OCI label | already fine |
| **GitHub topics** | `dmarc` 345 repos, `email-security` 461 | 12 topics set | already fine |
| **Artifact Hub** | 2, both `dmarc2logstash`, both 0★ | registered | badge lands on next processing run |
| **awesome-selfhosted** | 0 — DMARC appears only as a mail-server feature | PR #2792 open | proposes a new category |
| **dmarc.org — Code and Libraries** | old entries; **parsedmarc is on neither dmarc.org page** | absent | contact form; unclear how actively curated |
| **Yunohost** | 1 (`dmarcguard`) | absent | needs a `_ynh` package — real work |
| **CasaOS** | **0 of 166 apps** | absent | compose-based app definition |
| **Umbrel** | **0 of 394 apps** | absent | compose-based app definition |
| **selfh.st** | — | absent | has a Submit form |

Reading of it:

- **The registry pages were the real gap**, not the directories. Docker Hub had
  real traffic landing on a blank page, which is worse than not being listed —
  someone arriving there learned nothing and left.
- **The compose-based app stores are the largest untapped audience.** CasaOS and
  Umbrel have 560 apps between them and not one DMARC tool. Both take an app
  definition wrapping a compose file, which we already ship. This is the obvious
  next move if any is wanted.
- **Yunohost has a competitor already** (`dmarcguard`), so the category is proven
  there, but its packaging format is heavier than the others.
- **dmarc.org is the most authoritative and the least certain.** Its list still
  omits parsedmarc, which suggests it is not actively maintained; a submission may
  simply sit. Worth one email, not worth chasing.

None of these is a search-traffic play — see the `seo` repo's content plan for why
deployment keywords are not worth targeting. They are distribution: getting in
front of people already browsing for something to self-host.

## Portals beyond the app stores

Surveyed 2026-07-27, every claim queried live.

### dmarc.org

Two pages, and we fit the less obvious one.

- **[Products and Services](https://dmarc.org/resources/products-and-services/)** is
  entirely commercial vendors — Agari/Fortra, dmarcian, EasyDMARC, Mimecast,
  Proofpoint — each tagged Commercial or Free Trial. **There is no open-source or
  self-hosted entry at all.** Being the only one would be striking; being
  miscategorised among paid services would not help anyone.
- **[Code and Libraries](https://dmarc.org/resources/code-and-libraries/)** is where
  open-source DMARC packages live, and **parsedmarc is on neither page**, which
  says something about how actively either is curated.

No submission form for either — only a general contact form at `/contact-us/`,
which needs a browser. Worth one polite email suggesting both; not worth chasing.

### General software directories

| Portal | State | Fit |
|---|---|---|
| **OpenAlternative** | open-source alternatives to commercial SaaS, has a Submit page | **strongest fit** — our positioning is exactly "the open-source alternative to dmarcian/EasyDMARC" |
| **SaaSHub** | has a *DMARC Monitoring* category with a dozen entries | good, but see the name collision below |
| **LibHunt** | carries parsedmarc, so OSS DMARC tooling is in scope | reasonable; no obvious submit URL, entries appear to be crawled from GitHub |
| **AlternativeTo** | blocks automated access (403); submission is browser-only | worth doing by hand |
| Slant, StackShare | little or no DMARC presence | skip |

### The name collision, which matters more than any of these

`dmarcanalyzer.com` **redirects to Mimecast's product page**, and SaaSHub's existing
"DMARC Analyzer" entry is that product, not ours — our domain appears nowhere on
it. dmarc.org's products page lists "mimecast DMARC Analyzer" too.

So the name is already taken in this exact market by a vendor with a decade of
SEO behind it. Practical consequences, in order of how soon they bite:

1. **Directory submissions will collide.** SaaSHub already has the name; a second
   entry is a duplicate or a confusion.
2. **Search is unwinnable for the product name itself**, which the `seo` repo's
   content plan already implies for different reasons.
3. **Trademark is a question for a human.** "DMARC analyzer" is descriptive, which
   makes for a weak mark, but Mimecast uses it as a product name. Nothing here is
   legal advice — it is a flag.

None of this is urgent, and none of it blocks the app-store submissions, which use
the repository name and a description. It is worth deciding deliberately rather
than discovering during a rename.
