# App store packages

Definitions for the self-hosted app catalogues. Each is kept here, tested here,
and copied into the store's repository when submitted — none of these stores
consumes a file from our repo directly.

Both were verified by running them with the store's own substitutions emulated,
not just by reading their format docs.

## Umbrel — `umbrel/dmarc-analyzer/`

Copy the directory into a fork of [`getumbrel/umbrel-apps`](https://github.com/getumbrel/umbrel-apps)
and open a PR. Their README points contributors at a repo-local packaging skill;
this follows it.

- **Port 8189** was checked free against all 390 apps in the store. It must stay
  unique across the whole App Store.
- `gallery: []` is deliberate — the Umbrel team adds gallery images before merge,
  and official packages omit `icon` because assets live in a separate repo.
- `submission:` must be set to the PR URL once opened.
- **`exports.sh` derives the encryption key rather than using `APP_SEED` directly.**
  The app requires base64 decoding to exactly 32 bytes and throws at startup
  otherwise, so passing a raw `derive_entropy` value would make the app fail to
  install. Both secrets are derived, so they survive reinstalls — which matters
  for the encryption key, since changing it makes stored mailbox passwords
  undecryptable.

Verified with Umbrel's container-name injection emulated
(`dmarc-analyzer_server_1`, `dmarc-analyzer_db_1`): console 200, 19 migrations,
worker in-process, zero restarts, and an account surviving a restart from
`${APP_DATA_DIR}`.

## CasaOS — `casaos/`

Copy into a fork of [`IceWhaleTech/CasaOS-AppStore`](https://github.com/IceWhaleTech/CasaOS-AppStore)
under `Apps/DMARC Analyzer/` with the icon.

- **A user-defined network is required**, not `network_mode: bridge`. Single
  container apps there use `bridge`, but it gives no name resolution, so the app
  could not reach `dmarc-analyzer-db`. Every real multi-container app in that
  store defines a network; this does too.
- The store also wants `thumbnail` and `screenshot_link` assets. Neither is
  required to install, both are expected for a good listing, and neither exists
  yet.

Verified with `$AppID` substituted and the user-supplied env vars provided:
console 200, 19 migrations, zero restarts.

## Both

`depends_on` uses `condition: service_healthy` rather than plain ordering. The app
migrates as it boots, so without waiting for Postgres to accept connections it
fails once and restarts — and a first-boot restart reads as a broken app to
someone reviewing a submission. CasaOS showed exactly that until the gate was
added.

Images are pinned to a release tag, and Umbrel additionally by digest, per its
packaging guidance.
