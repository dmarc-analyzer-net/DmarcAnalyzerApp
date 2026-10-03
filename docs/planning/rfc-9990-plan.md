# RFC 9990 completion plan

RFC 9990 (DMARC aggregate reporting, obsoletes RFC 7489's reporting half) is
mostly supported already. This plan covers what is left, in the order it should
ship.

## Where things stand

Already done on `main`:

- **Namespace.** Reports in `urn:ietf:params:xml:ns:dmarc-2.0` are detected and
  parsed (`DmarcRuaReportParser.NormalizeReportXml`).
- **`pass` action disposition.** Preserved by the parser and shown as a fourth
  bucket in analytics and the console.
- **Per-record fields.** DKIM `selector`, `human_result` (DKIM and SPF) and
  `envelope_to` are parsed and stored.
- **External destination authorization (§4).** Record inspection checks
  `<domain>._report._dmarc.<destination>`.
- **RFC 9989 DNS tags.** `np=`, `t=` and `psd=` are already parsed from the live
  record and validated, and are in `DnsDmarcRecordDto`, but the console does
  not show them and nothing compares them with reports.

What is missing:

1. **Report-level fields are discarded.** `np`, `testing` and
   `discovery_method` are read only to become `info:` lines in
   `ValidationMessages` (`DescribeDmarcBisTags`). `generator` and
   `report_metadata/error` are not read at all. DmarcRua 2.1.0 has no
   `Generator` property.
2. **`pct` is invented.** RFC 9990 removed `pct`, but `ParsePercent` turns an
   absent value into `100`, so every v2 report stores a `pct` it never sent.
   This is the same problem `sp` had.
3. **Parser messages go nowhere.** Nothing outside the parser reads
   `ValidationMessages`, `HasValidationWarnings` or `HasValidationErrors`. That
   hides every repair the parser makes (backlog: *Surface parse validation
   warnings instead of discarding them*).
4. **Every conformant v2 report raises a warning.** Stripping the namespace
   emits `warning: stripped XML namespace ...`. That is how v2 reports are
   read, not a fault, so once warnings are shown, every RFC 9990 report would
   be flagged.
5. **Existing reports cannot be corrected.** Raw XML is not kept, and a stored
   report is skipped as a duplicate if it arrives again.

## Decisions

| Question | Decision |
|---|---|
| How parser messages are kept | Store the full message list on each report at ingest |
| Where operators see them | A "report notes" panel on the domain detail page; no `/reports` routes |
| Report-mail archive default | Stays opt-in (`Backup:ArchiveReportMail`, default false) |
| RFC 9989 DNS `np`/`t` | In scope: show them, and compare them with what reporters saw |
| Backfill of existing reports | In scope, from the report-mail archive |
| `generator` | Stored and shown |
| Archiving push-endpoint payloads | Out of scope; `POST /reports` reports stay unarchived and cannot be backfilled |

## Step 0: an RFC 9990 test report (before any PR)

Add `src/api.tests/Fixtures/sample-rfc9990-aggregate.xml`. It must use every
element RFC 9990 adds or changes:

- `version`, `generator` and `error` in `report_metadata`;
- `np`, `testing` and `discovery_method` in `policy_published`, with no `pct`;
- a file-level `extension` after `policy_published`, and a record-level
  extension after `auth_results`, each in a foreign namespace;
- a `pass` disposition, `envelope_to`, DKIM `selector` and `human_result`.

Run it through the parser on `main` and record every validation message it
produces. That list decides how much PR 1 has to suppress.

Also check the element list against the XSD in RFC 9990 itself, not against a
summary. In particular, check whether `policy_published` carries `psd` and
`fo`, and whether `version` is a `feedback` child or a `report_metadata` child.

## PR 1: store the report-level fields

**Schema** (`dmarc_report`, one migration):

| Column | Type | Meaning |
|---|---|---|
| `ReportFormat` | `varchar(16)` null | `rfc7489` or `rfc9990`. **Null means the report was stored before this column existed**, so every field below is "not recorded", not "not sent". |
| `NonexistentSubdomainPolicy` | `varchar(16)` null | `np` |
| `Testing` | `boolean` null | `testing` (`y` = true) |
| `DiscoveryMethod` | `varchar(16)` null | `psl` or `treewalk` |
| `Generator` | `varchar(256)` null | `report_metadata/generator`, trimmed and capped |
| `ReportErrors` | `jsonb` null | `report_metadata/error` values as sent |
| `PublishedPct` | `int` → **`int null`** | Null when the reporter sent no `pct`. Drop the database default of `100`. |

Rows already stored keep their values. `PublishedPct` stays `100` on old rows.
Those reports were mostly RFC 7489, where `100` is the real default, and the
backfill corrects any RFC 9990 ones it can reach.

**Parser** (`DmarcRuaReportParser`):

- Set `ReportFormat` from the root namespace, using the `isDmarcBisReport`
  check that already exists.
- Read `generator` from the `XDocument` in `NormalizeReportXml`, the same way
  the v2 dispositions are captured, because the library does not model it.
- Read `np`, `testing`, `discovery_method` and `ReportMetadata.Error` from the
  deserialized objects.
- `ParsePercent` returns `int?` and gives null when the value is absent. An
  out-of-range value stays `100` with a warning, as now.
- Delete `DescribeDmarcBisTags`; the values now have columns.
- Extensions: remove `extension` elements, and any element in a foreign
  namespace, before validation. RFC 9990 says to ignore unknown extensions, so
  they must not become schema warnings. Do the same for `generator` if step 0
  shows it raises one.

**Ingest:** add the columns to the raw SQL insert in
`DmarcReportIngestor` (the `INSERT INTO "dmarc_report"` block).

**Tests:** parse the step 0 fixture and assert every new field; a v2 report
with no `pct` stores null; an RFC 7489 report stores `ReportFormat = rfc7489`,
its `pct`, and null for the v2-only fields; foreign-namespace extensions raise
no messages.

**Docs:** `data-model.md` (new columns, the meaning of a null `ReportFormat`),
`status.md`.

## PR 2: store and show parser messages

Closes out the backlog item *Surface parse validation warnings instead of
discarding them*.

**Severity first.** Three levels: `info`, `warning`, `error`.

- The namespace strip becomes `info`.
- Repairs that change meaning stay `warning`: unrecognised values replaced,
  empty `policy_evaluated` dkim/spf read as `fail`, dropped empty records
  (#190), and a recovered truncated document.
- XSD validation errors stay `error`.

Change the message strings to a structured `(Severity, Text)` record on
`DmarcReportParseResult` instead of a `"warning: ..."` prefix that callers have
to parse.

**Schema** (`dmarc_report`):

| Column | Type | Meaning |
|---|---|---|
| `ParseMessages` | `jsonb` null | `[{ "severity": "warning", "text": "..." }]`, already deduplicated by the parser |
| `ParseWarningCount` | `int` null | Count of `warning` and `error` entries, so lists can filter without reading JSON |
| `ParseMessagesOrigin` | `varchar(16)` null | `ingest`, or `backfill` when PR 4 wrote them |

Null on all three means the report predates this change. The panel says
"not recorded", not "no problems".

**API:** `GET /analytics/domains/{domainId}/report-notes?days=30`. Same scoping
and window anchoring as the other analytics routes, and 404 for cross-tenant
ids. It returns reports in the window with `ParseWarningCount > 0`, newest
first, capped at 100, each with organisation, report id, range, origin and
messages. Add `includeInfo=true` to also return info-only reports. The response
also counts reports in the window whose messages were not recorded, so the panel
can say how much it cannot see.

**Console:** a "Report notes" panel on `DomainDetailPage`. Hide it when there
is nothing to show. Each row expands to show its messages. Mark backfilled rows
"re-parsed on <date>", because their messages come from today's parser and not
necessarily the one that stored the row.

**Tests:** parser severity mapping; ingest stores messages; endpoint scoping,
filtering and the not-recorded count; a component test for the panel.

**Docs:** `api-contract.md`, `data-model.md`, `status.md`; mark the backlog item
done.

## PR 3: record inspection uses the new fields

**Observed policy** (`ObservedPolicyDto`, built in
`RecordInspectionService`): add `NonexistentSubdomainPolicy`, `Testing` and
`ReportFormat`, and make `Pct` nullable. This changes the API type of
`observed.pct` from `int` to `int | null`; update `src/web/src/lib/analytics.ts`
to match.

**Comparisons** (`Compare`):

- **`pct`:** when the observed value is null, return `not_reported` with a note
  that RFC 9990 reports do not carry `pct`, rather than comparing against an
  invented `100`. If DNS still publishes `pct=` and reporters are on RFC 9990,
  add a record issue that RFC 9989 replaced `pct` with `t`.
- **`np`:** new row. Not published in DNS means `inherited` (falls back to
  `sp`, then `p`), using the same rule as `CompareSubdomainPolicy`.
  Not sent by the reporter means `not_reported`. Otherwise match or differ.
- **`t`:** new row. Absent in DNS means `n`. Not sent by the reporter
  (including every RFC 7489 report) means `not_reported`.

**Console:** show the DNS `np`, `t` and `psd` values that the API already
returns in the records panel, plus the two new comparison rows. Show
`generator` beside the reporter on the source detail reporters list, which
needs `Generator` added to that projection.

**Tests:** comparison cases for each tag (published or not, reported or not,
match or differ), and the `pct` null case.

**Docs:** `api-contract.md` (records endpoint), `status.md`.

## PR 4: backfill existing reports from the archive

This only reaches reports whose original mail is in the report-mail archive:
mailbox and S3 sources on installs with `ArchiveReportMail` enabled, within the
archive's lifecycle. Reports pushed to `POST /reports`, and reports from installs
without the archive, stay "not recorded".

**Storage:** `IObjectStorage` has no list operation. Add
`ListAsync(prefix, continuationToken, ct)` and implement it with
`ListObjectsV2`. Archive keys are
`<prefix>/reports/yyyy/MM/dd/<reportSourceId>/<generation>-<uid>.eml.gz`, so the
backfill can walk by day.

**Job:** for each archived object:

1. Download it, gunzip it, and parse it as MIME.
2. Run each attachment through `ReportPayloadExtractor`. One mail can hold
   several reports, and TLS-RPT payloads are skipped.
3. Parse each DMARC payload with the current parser.
4. Find the stored report by its dedup key: domain, report id, range begin and
   range end.
5. If the stored report has a null `ReportFormat`, write the PR 1 columns,
   `PublishedPct` and the PR 2 messages with origin `backfill`. Otherwise leave
   it alone. This makes the job idempotent and safe to stop and re-run.

It updates **report-level columns only**. Records, and therefore historic `pass`
dispositions that were stored as `none`, are not rewritten. Their rows have no
stable key to match against, and changing them would silently change past
compliance figures. If that is wanted later, it is a separate decision with its
own preview.

**Trigger:** the same pattern as retention:

- `GET /admin/report-backfill/preview`: how many archived objects, how many
  reports with a null `ReportFormat` they would match, and an estimate of the
  reports that will stay unrecorded. Writes nothing.
- `POST /admin/report-backfill/run`: queues the job for the worker. Progress
  and the last result use a cursor row like `BackupStreamState` (last key
  processed, counts, last error) so a restarted worker resumes rather than
  starting again.
- Audit event on run. Admin only.

**Tests:** the archive walk with a fake `IObjectStorage`; a multi-report mail;
matching by dedup key; rows that already have a format are skipped; resume
from the cursor; a missing or unparseable object is counted, not fatal.

**Docs:** `api-contract.md`, `status.md`, and a note in the archive section of
`config-export-and-recovery.md` that enabling the archive is what makes later
re-parsing possible.

## Order and dependencies

```
Step 0 ─► PR 1 ─► PR 2 ─► PR 4
            └───► PR 3
```

PR 3 needs only PR 1. PR 4 needs PR 1 and PR 2, because it fills both sets of
columns. PR 1 and PR 2 each add a migration, so they merge in order.

## Not in this plan

- Archiving reports received through `POST /reports`.
- Turning the archive on by default.
- Rewriting stored records from the archive (see PR 4).
- A general `/reports` API or report list page.
- Failure reports (RFC 9991).
