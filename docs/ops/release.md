# Cutting a release

Releases are driven entirely by **git tags**. Merging to `main` does not publish
a release — it only refreshes the `edge` image. Pushing a `vX.Y.Z` tag is what
moves `latest` and produces version-pinned images.

## Channels

| Trigger | Image tags | Who it's for |
|---|---|---|
| tag `vX.Y.Z` | `latest`, `X.Y.Z`, `X.Y`, `sha-<commit>` | everyone; what the README quick-start and `deploy/compose.yml` pull |
| push to `main` | `edge`, `sha-<commit>` | trying an unreleased fix |
| pull request | *builds, never pushes* | proving the Dockerfile still works |

Both registries receive every tag:

```
ghcr.io/dmarc-analyzer-net/dmarc-analyzer
dmarcanalyzernet/dmarc-analyzer
```

Documentation-only pushes to `main` skip the image build entirely. Release tags
and pull requests always build it.

## The Helm chart

A release tag also publishes the chart as an OCI artifact:

```
oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer
```

**Chart `version` and `appVersion` are both set from the tag** and are not read
from the committed `Chart.yaml`, so "which application does chart 1.2.3 deploy"
has exactly one answer, and `values.image.tag` — which defaults to `appVersion` —
needs no separate bump. The values committed in `Chart.yaml` are only a default
for someone installing from a working tree.

There is no `edge` chart. A chart is cheap to install from the repository
directory, and an unversioned moving chart is the kind of thing that makes a
rollback ambiguous.

### Listing it on Artifact Hub

The chart carries `artifacthub.io/*` annotations in `Chart.yaml`, so a listing
picks up links, category and screenshots automatically once the repository is
registered. Registration is a one-off and needs a browser — it cannot be done
from CI:

1. **Add the repository** at artifacthub.io → Control Panel → Repositories → Add,
   kind *Helm charts*, URL `oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer`.
2. **Claim ownership**, which turns an unclaimed listing into a verified one. Copy
   the repository ID from that same screen into
   `deploy/helm/artifacthub-repo.yml`, set the owner email to the Artifact Hub
   account's address, then push the file to the same OCI namespace:

   ```bash
   oras push ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer:artifacthub.io \
     --config /dev/null:application/vnd.cncf.artifacthub.config.v1+yaml \
     deploy/helm/artifacthub-repo.yml:application/vnd.cncf.artifacthub.repository-metadata.layer.v1.yaml
   ```

   The ID and the email both have to match the account or the claim silently
   stays unverified.

Worth doing: a search for "dmarc" on Artifact Hub returns two packages, both
`dmarc2logstash`, both at zero stars. The category is effectively empty.

On every run, tag or not, CI **renders all six supported value combinations and
asserts every guardrail still refuses**. That is not ceremony: the migration Job
renders only without bundled Postgres and the worker Deployment only in split
mode, so a single render leaves most of the chart unexercised — which is exactly
how a Job that could never create a pod got as far as a real cluster once.

## Versioning

Semantic versioning. While on `0.x`:

- **patch** (`0.1.0` → `0.1.1`) — fixes, no schema or config changes.
- **minor** (`0.1.x` → `0.2.0`) — new features, new migrations, new
  configuration keys, or anything that changes existing behaviour. On `0.x` a
  minor may include breaking changes; say so prominently in the notes.
- **major** — reserved for `1.0.0`, once the configuration surface and data
  model are stable enough to promise compatibility.

## Before tagging

1. **CI is green on `main`.** Tests and the frontend build both pass.
2. **Migrations apply from the previous release**, not just from an empty
   database. Start the *previous* image against a fresh volume, put a row or two
   in it, then upgrade in place and confirm the count moved, the data survived and
   nothing restarted:

   ```bash
   docker compose exec -T postgres psql -U postgres -d dmarc_analyzer -tAc \
     'select count(*) from "__EFMigrationsHistory"'
   ```

   Empty-database migration proves far less — it never exercises the ordering
   between an old schema and a new one.
3. **The quick-start works.** In a scratch directory:
   ```bash
   curl -fsSL -o compose.yml https://raw.githubusercontent.com/dmarc-analyzer-net/DmarcAnalyzerApp/main/deploy/compose.yml
   echo "DMARC_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env
   docker compose up -d
   curl -s localhost:8080/api/v1/auth/setup   # expect {"requiresBootstrap":true}
   ```
   Check `docker compose ps` shows **both** containers — `app` and `postgres` —
   up with **zero restarts**. (It was three before the combined container; the
   shipped file now runs `APP_MODE=all`.)

   Then the overlays, which do not share a code path with the default and so are
   not covered by the check above:
   ```bash
   docker compose -f compose.yml -f compose.split.yml up -d   # expect app+worker+postgres
   ```
4. **Docs match the code.** If configuration keys, endpoints, or setup steps
   changed, the [docs site](https://dmarc-analyzer.net/docs) lives in the
   [website repo](https://github.com/dmarc-analyzer-net/dmarc-analyzer-net.github.io/tree/main/src/content/docs)
   and must be updated in the same release — especially `configuration.md`.
5. **`docs/planning/status.md` reflects reality**, since that's what people read
   to know what exists.
6. **Version references point at the release you are about to cut**, not the last
   one. Bump `deploy/helm/dmarc-analyzer/Chart.yaml` — both `version` and
   `appVersion` — *before* tagging.

   This is not cosmetic. `values.image.tag` defaults to the chart's `appVersion`,
   so a stale `Chart.yaml` means `helm install ./deploy/helm/dmarc-analyzer` from a
   clone silently deploys the **previous** release, schema and all. It happened
   between 0.1.0 and 0.2.0.

   `VersionReferenceTests` now fails the build for anything in *this* repo. It
   cannot see the website, so those stay manual — two files, four references:

   | File | What |
   |---|---|
   | `src/content/docs/install.md` | the pinning example and the `helm install --version` |
   | `src/content/docs/kubernetes.md` | `--version` in the install and production examples |

   Both live in the [website repo](https://github.com/dmarc-analyzer-net/dmarc-analyzer-net.github.io/tree/main/src/content/docs)
   and should ship in the same release as step 4.

## Tagging

Annotated tags only — the message is the first thing a maintainer sees in
`git tag -n`:

```bash
git checkout main && git pull
git tag -a v0.2.0 -m "v0.2.0 — retention purge, CSV export"
git push origin v0.2.0
```

CI then builds and pushes the image to both registries for `linux/amd64` and
`linux/arm64`.

## Verifying the published image

Do this as an anonymous user — logged-in pulls hide a private-package mistake:

```bash
docker logout ghcr.io
docker manifest inspect ghcr.io/dmarc-analyzer-net/dmarc-analyzer:0.2.0 \
  | grep -o '"architecture": "[a-z0-9]*"' | sort -u   # expect amd64 + arm64
docker manifest inspect dmarcanalyzernet/dmarc-analyzer:0.2.0 > /dev/null && echo "mirror ok"
```

Then re-run the quick-start from step 3 above with the **published** image rather
than a local build. That is the only check that proves what users will actually
get.

**And the chart**, which is published by the same tag and is easy to forget
because nothing else references it:

```bash
helm show chart oci://ghcr.io/dmarc-analyzer-net/charts/dmarc-analyzer --version 0.2.0
# version and appVersion should both be 0.2.0 — they are set from the tag, not
# from the committed Chart.yaml
```

> If GHCR denies an anonymous pull, the package visibility reverted to private.
> Fix it at *org → Packages → dmarc-analyzer → Package settings → Change
> visibility → Public*. Note this also requires public packages to be permitted
> org-wide (*Organization settings → Packages → Package creation*).

## Release notes

Create the GitHub Release against the tag:

```bash
gh release create v0.2.0 --verify-tag --title "v0.2.0 — …" --notes-file notes.md
```

Write them by hand rather than generating from PR titles, and keep the structure
[v0.1.0](https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/releases/tag/v0.1.0)
established:

- **Install** — the quick-start commands and both image references.
- **What's in it** — grouped by what it does for the operator, not a changelog.
- **Not in this release** — an explicit list of notable gaps. Self-hosters are
  deciding whether to trust the software; being upfront about what's missing
  earns more than a feature list, and it stops people discovering a gap the hard
  way. Keep it consistent with `docs/planning/status.md`.
- **Upgrade notes** — required config changes, or migrations that are slow or
  irreversible.

## If a release is wrong

**Never move a published tag.** Anyone who already pulled `0.2.0` would silently
get different bytes. Cut `0.2.1` instead.

To stop `latest` recommending a bad release before the fix is ready, retag the
previous good commit as a new patch version so `latest` moves back to working
code, then investigate.

Users pinned to a version are unaffected — which is the reason to pin, and worth
repeating in the notes.

## Hotfixes

For an urgent fix while `main` has unreleased work in flight: branch from the
release tag, apply the minimal fix, tag it as a patch, then merge the same fix
forward into `main` so it isn't lost.
