# Planning

Planning artifacts for `DmarcAnalyzerApp`, a DMARC analyzer platform in the spirit of dmarcian and EasyDMARC.

## Product Direction

- Build a DMARC analyzer tool for aggregate report visibility, troubleshooting, and policy improvement.
- Use a C# web application backend with a React frontend.
- Use Tailwind + shadcn-style component primitives for a reusable UI system.
- Use PostgreSQL as the primary datastore.
- Ingest DMARC reports from IMAP or POP3 mailboxes using MailKit (MVP).
- Parse and serialize DMARC RUA XML with `https://github.com/danielsen/DmarcRua`.
- Package and run as Docker containers, with Docker Compose first and Kubernetes-ready configuration patterns.

## Confirmed Planning Decisions

- Primary operating model is agency-managed multi-client usage.
- Most clients will not have user accounts in MVP.
- Authentication MVP is local username/password.
- Scheduled polling is the default ingestion mode.
- Default polling interval is every 60 minutes.
- Polling runs 24/7 (no polling windows in MVP).
- Data retention must be configurable per client.
- Default retention for new clients is 27 months.
- Tenant isolation in MVP is single PostgreSQL database with tenant-keyed data model.
- Initial scale target is up to 200 clients.
- Deduplication key is org/client + domain + report-id + report date range.
- Domain ownership is globally unique across clients.
- Ingestion supports ZIP and GZIP DMARC attachments.
- Historical onboarding uses unlimited mailbox backfill (oldest-to-newest with checkpointing).
- Background processing uses a lightweight database-backed job queue.
- Client access uses signed magic links (single-client read-only scope, 7-day default expiry).
- Reporting deliverables include branded PDF summaries, dashboard links, and monthly email digest.
- Agency branding is first (logo, colors, report footer).
- Alerts in MVP include failure spikes and policy regression, delivered by SMTP email with per-client thresholds.
- Core audit logs are included in MVP.
- Office 365 and Google Workspace ingestion are planned after MVP.
- Future client login, if enabled, will be view-only (no settings/admin changes).
- Deployment guidance should support Docker Compose and Kubernetes with equal focus.

## Contents

- `backlog.md` - prioritized implementation tasks.
- `roadmap.md` - milestone delivery plan.
- `status.md` - implemented-now snapshot and planned-next summary.

## Status Legend

- `todo` - identified but not started.
- `in-progress` - currently being worked.
- `blocked` - waiting on dependency/decision.
- `done` - completed.
