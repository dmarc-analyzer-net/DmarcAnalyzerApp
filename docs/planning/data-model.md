# Data Model

Logical data model for `DmarcAnalyzerApp` MVP.

## 1) Modeling Principles

- Single PostgreSQL database.
- Strict tenant scoping via `client_id` on all client-owned data.
- Global domain uniqueness.
- Report deduplication by business key, not file hash alone.
- Operational traceability through audit and sync run records.

## 2) Core Entities

## 2.1 Agency and Users

### `agency_user`

- `id` (pk)
- `email` (unique)
- `password_hash`
- `display_name`
- `role` (`agency_admin|agency_analyst`)
- `is_active`
- `last_login_at`
- `created_at`
- `updated_at`

Indexes:

- unique: `email`

### `session`

- `id` (pk)
- `user_id` (fk -> agency_user)
- `cookie_id` (unique)
- `created_at`
- `last_seen_at`
- `expires_at`
- `revoked_at` (nullable)
- `ip_address` (nullable)
- `user_agent` (nullable)

Indexes:

- unique: `cookie_id`
- idx: `user_id`
- idx: `expires_at`

## 2.2 Client and Domain Ownership

### `client`

- `id` (pk)
- `name`
- `slug` (unique)
- `is_active`
- `retention_months` (default 27)
- `timezone` (default `UTC`)
- `created_at`
- `updated_at`

Indexes:

- unique: `slug`

### `domain`

- `id` (pk)
- `name` (normalized lower-case FQDN)
- `client_id` (fk -> client)
- `is_active`
- `created_at`
- `updated_at`

Indexes/constraints:

- unique: `name` (globally unique across all clients)
- idx: `client_id`

## 2.3 Mailbox Sources and Routing

### `mailbox_source`

- `id` (pk)
- `name`
- `protocol` (`imap|pop3`)
- `host`
- `port`
- `use_tls` (bool)
- `username`
- `password_encrypted`
- `default_client_id` (fk -> client)
- `is_active`
- `last_success_sync_at` (nullable)
- `created_at`
- `updated_at`

Indexes:

- idx: `default_client_id`
- idx: `is_active`

### `mailbox_source_client`

Optional mapping table for explicit source-to-client associations.

- `source_id` (fk -> mailbox_source)
- `client_id` (fk -> client)
- `created_at`

Primary key:

- composite: (`source_id`, `client_id`)

### `sync_checkpoint`

Tracks resumable progress for oldest-to-newest backfill and incremental sync.

- `id` (pk)
- `source_id` (fk -> mailbox_source, unique)
- `cursor_type` (`uid|message_id|date`)
- `cursor_value`
- `last_message_date` (nullable)
- `updated_at`

Indexes:

- unique: `source_id`

### `sync_run`

- `id` (pk)
- `source_id` (fk -> mailbox_source)
- `trigger_type` (`schedule|manual|retry|startup`)
- `status` (`queued|running|completed|failed|dead_letter`)
- `started_at` (nullable)
- `finished_at` (nullable)
- `messages_scanned` (default 0)
- `attachments_processed` (default 0)
- `reports_parsed` (default 0)
- `reports_inserted` (default 0)
- `reports_deduped` (default 0)
- `error_code` (nullable)
- `error_message` (nullable)
- `created_by_user_id` (nullable fk -> agency_user)
- `created_at`

Indexes:

- idx: `source_id`, `created_at desc`
- idx: `status`, `created_at desc`

## 2.4 Ingestion Job Queue

### `job`

- `id` (pk)
- `job_type` (`poll_source|parse_message|generate_digest|evaluate_alerts|retention_purge|generate_export|generate_pdf`)
- `payload_json`
- `status` (`queued|running|completed|failed|dead_letter`)
- `priority` (default 100)
- `attempt_count` (default 0)
- `max_attempts` (default 5)
- `next_attempt_at`
- `locked_by` (nullable)
- `locked_until` (nullable)
- `last_error` (nullable)
- `created_at`
- `updated_at`

Indexes:

- idx: `status`, `next_attempt_at`
- idx: `job_type`, `status`
- idx: `locked_until`

## 2.5 Raw and Normalized DMARC Reports

### `raw_report`

Stores raw payload metadata for diagnostics and replay.

- `id` (pk)
- `source_id` (fk -> mailbox_source)
- `source_message_id` (nullable)
- `attachment_name` (nullable)
- `compression_type` (`none|zip|gzip`)
- `xml_sha256`
- `received_at`
- `stored_blob_ref` (nullable, if object storage is added)
- `created_at`

Indexes:

- idx: `source_id`, `received_at desc`
- idx: `xml_sha256`

### `dmarc_report`

- `id` (pk)
- `client_id` (fk -> client)
- `domain_id` (fk -> domain)
- `source_id` (fk -> mailbox_source)
- `raw_report_id` (nullable fk -> raw_report)
- `report_id`
- `org_name`
- `email` (nullable)
- `extra_contact_info` (nullable)
- `begin_utc`
- `end_utc`
- `policy_domain`
- `adkim` (`r|s`)
- `aspf` (`r|s`)
- `p` (`none|quarantine|reject`)
- `sp` (`none|quarantine|reject|unspecified`)
- `pct` (int)
- `discovery_method` (`domain_map|source_default_client`)
- `ingested_at`
- `created_at`

Constraints/indexes:

- unique dedup key: (`client_id`, `domain_id`, `report_id`, `begin_utc`, `end_utc`)
- idx: `client_id`, `domain_id`, `begin_utc`
- idx: `client_id`, `end_utc`
- idx: `source_id`, `ingested_at desc`

### `dmarc_record`

- `id` (pk)
- `report_id` (fk -> dmarc_report)
- `client_id` (fk -> client)
- `domain_id` (fk -> domain)
- `source_ip`
- `count`
- `disposition` (`none|quarantine|reject`)
- `dkim_result` (`pass|fail`)
- `spf_result` (`pass|fail`)
- `dkim_aligned` (bool)
- `spf_aligned` (bool)
- `envelope_from` (nullable)
- `header_from` (nullable)
- `created_at`

Indexes:

- idx: `report_id`
- idx: `client_id`, `domain_id`, `created_at`
- idx: `client_id`, `source_ip`, `created_at`
- idx: `client_id`, `disposition`, `created_at`

### `dmarc_auth_result`

Optional detailed auth result rows if persisted separately.

- `id` (pk)
- `record_id` (fk -> dmarc_record)
- `auth_type` (`dkim|spf`)
- `domain`
- `selector` (nullable)
- `result`
- `human_result` (nullable)

Indexes:

- idx: `record_id`

## 2.6 Aggregates and Dashboard Acceleration

### `daily_domain_metric`

- `id` (pk)
- `client_id` (fk -> client)
- `domain_id` (fk -> domain)
- `day_utc` (date)
- `total_count`
- `pass_count`
- `fail_count`
- `spf_aligned_count`
- `dkim_aligned_count`
- `none_count`
- `quarantine_count`
- `reject_count`
- `created_at`
- `updated_at`

Constraints/indexes:

- unique: (`client_id`, `domain_id`, `day_utc`)
- idx: `client_id`, `day_utc`

## 2.7 Alerts and Notifications

### `alert_rule`

- `id` (pk)
- `scope` (`global|client`)
- `client_id` (nullable fk -> client)
- `rule_type` (`failure_spike|policy_regression`)
- `is_enabled`
- `threshold_json`
- `created_at`
- `updated_at`

Indexes:

- idx: `scope`, `rule_type`
- idx: `client_id`, `rule_type`

### `alert_event`

- `id` (pk)
- `client_id` (fk -> client)
- `rule_id` (fk -> alert_rule)
- `severity` (`info|warning|critical`)
- `status` (`open|acknowledged|closed`)
- `title`
- `details_json`
- `detected_at`
- `created_at`
- `updated_at`

Indexes:

- idx: `client_id`, `detected_at desc`
- idx: `status`, `detected_at desc`

### `notification_recipient`

- `id` (pk)
- `scope` (`global|client`)
- `client_id` (nullable fk -> client)
- `email`
- `kind` (`alert|digest|both`)
- `is_active`
- `created_at`
- `updated_at`

Indexes:

- idx: `scope`, `kind`, `is_active`
- idx: `client_id`, `kind`, `is_active`

### `digest_schedule`

- `id` (pk)
- `client_id` (fk -> client)
- `cadence` (`monthly`)
- `day_of_month` (1-28 recommended)
- `time_utc`
- `is_active`
- `created_at`
- `updated_at`

Indexes:

- idx: `client_id`, `is_active`

### `digest_delivery`

- `id` (pk)
- `client_id` (fk -> client)
- `period_start_utc`
- `period_end_utc`
- `status` (`queued|sent|failed`)
- `smtp_message_id` (nullable)
- `error_message` (nullable)
- `sent_at` (nullable)
- `created_at`

Indexes:

- idx: `client_id`, `created_at desc`

## 2.8 Exports and PDFs

### `export_job`

- `id` (pk)
- `client_id` (fk -> client)
- `requested_by_user_id` (fk -> agency_user)
- `format` (`csv|json`)
- `request_filters_json`
- `status` (`queued|running|completed|failed|expired`)
- `row_count` (nullable)
- `file_size_bytes` (nullable)
- `artifact_ref` (nullable)
- `expires_at` (nullable)
- `error_message` (nullable)
- `created_at`
- `updated_at`

Indexes:

- idx: `client_id`, `created_at desc`
- idx: `status`, `created_at desc`

### `pdf_report_job`

- `id` (pk)
- `client_id` (fk -> client)
- `requested_by_user_id` (nullable fk -> agency_user)
- `period_start_utc`
- `period_end_utc`
- `status` (`queued|running|completed|failed|expired`)
- `artifact_ref` (nullable)
- `error_message` (nullable)
- `created_at`
- `updated_at`

Indexes:

- idx: `client_id`, `created_at desc`

## 2.9 Magic Links and Read-Only Access

### `magic_link_nonce`

- `id` (pk)
- `client_id` (fk -> client)
- `nonce` (unique random token id)
- `label` (nullable)
- `expires_at`
- `revoked_at` (nullable)
- `last_used_at` (nullable)
- `created_by_user_id` (fk -> agency_user)
- `created_at`

Indexes:

- unique: `nonce`
- idx: `client_id`, `expires_at`
- idx: `revoked_at`

## 2.10 Audit and Retention

### `audit_event`

- `id` (pk)
- `actor_type` (`agency_user|system|magic_link`)
- `actor_id` (nullable)
- `client_id` (nullable)
- `event_type`
- `entity_type`
- `entity_id` (nullable)
- `metadata_json`
- `ip_address` (nullable)
- `user_agent` (nullable)
- `created_at`

Indexes:

- idx: `client_id`, `created_at desc`
- idx: `event_type`, `created_at desc`
- idx: `actor_type`, `actor_id`, `created_at desc`

### `retention_policy`

- `id` (pk)
- `client_id` (fk -> client, unique)
- `retention_months` (default 27)
- `legal_hold` (default false)
- `updated_by_user_id` (fk -> agency_user)
- `updated_at`

Indexes:

- unique: `client_id`

## 3) Key Integrity Rules

- A `domain` belongs to exactly one `client` at a time.
- Domain name is globally unique and case-insensitive.
- Every `dmarc_report` must resolve to a valid `client` and `domain`.
- Unmatched domain reports are assigned via `mailbox_source.default_client_id` and domain is auto-created.
- Dedup uniqueness: (`client_id`, `domain_id`, `report_id`, `begin_utc`, `end_utc`).
- Retention purge selection uses report `end_utc`, not ingest time.

## 4) Suggested Type Mapping (EF Core)

- `id`: `uuid` (or `text` if string ids are preferred; uuid recommended)
- dates/timestamps: `timestamp with time zone`
- enums: PostgreSQL enum or constrained text (text for easier migration in MVP)
- JSON payloads: `jsonb`
- counters: `integer`/`bigint` based on expected volume

## 5) Query Patterns to Optimize

- Dashboard daily trend by client + date range.
- Domain-specific pass/fail/alignment trend.
- Top source IP failures in date range.
- Ingestion diagnostics by source and recent sync runs.
- Alert event timelines by client and status.

## 6) Initial Migration Order

1. Agency/auth tables (`agency_user`, `session`)
2. Client/domain tables (`client`, `domain`, `retention_policy`)
3. Mailbox and sync tables (`mailbox_source`, `mailbox_source_client`, `sync_checkpoint`, `sync_run`)
4. Job queue (`job`)
5. Report storage (`raw_report`, `dmarc_report`, `dmarc_record`, `dmarc_auth_result`)
6. Aggregates (`daily_domain_metric`)
7. Notifications/alerts (`alert_rule`, `alert_event`, `notification_recipient`, `digest_schedule`, `digest_delivery`)
8. Artifact jobs (`export_job`, `pdf_report_job`)
9. Access/audit (`magic_link_nonce`, `audit_event`)

## 7) Open Model Questions

- Should auto-created domains from fallback routing be tagged `needs_review` for agency confirmation?
- Should `domain` support aliases (for subdomain consolidation/reporting)?
- Should we materialize per-source daily aggregates in addition to per-domain?
- Should audit retention be separate from report retention policy?
