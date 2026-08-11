# Security Policy

## Reporting a vulnerability

**Please do not open a public issue.**

Use GitHub's private vulnerability reporting:

**https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/security/advisories/new**

That opens a thread visible only to you and the maintainer. It needs no new
mailbox, and it keeps the report next to the code it concerns.

Useful things to include, to whatever extent you have them: the version or commit
you tested, how the instance was deployed (Compose, Helm, or from source), what
an attacker gets, and the smallest reproduction you can manage. A partial report
is worth more than a perfect one you never send.

## What to expect

This project is maintained by one person. The intent is to acknowledge a report
within a few days and to keep you updated as it is looked at, but there is no
guaranteed response time, and pretending otherwise would be dishonest.

Fixes are released as a new version — published tags are never moved, so an
upgrade is always the route to a fix. If you would like credit in the advisory,
say so and how you want to be named; if you would rather not be named, that is
fine too.

Please give a fix a reasonable chance to ship before disclosing publicly. There
is no fixed embargo period attached to that request.

## Supported versions

Pre-1.0, only the most recent release gets fixes. There are no backports to
earlier minor versions.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Anything older | No — upgrade |

`main` is not a release. Problems found there are still worth reporting, but the
fix will simply land in the next version.

## Scope

This is self-hosted software. You run it, on your infrastructure, against your
database, so the boundary is roughly: **defects in this code are in scope; how
you deploy it is not.**

In scope, as examples rather than an exhaustive list: authentication and session
handling, the client-scoping that keeps one tenant's report data away from
another, credential handling for report sources, anything reachable by feeding
the ingestion path a hostile report (the parsers and archive extraction handle
attacker-controlled input by design), and dependency vulnerabilities that are
actually reachable in this application.

Known and documented behaviour, not a vulnerability:

- **Running without `Security__CredentialEncryptionKey`.** Report-source
  passwords are then stored without encryption at rest. The application logs a
  warning saying so, and the setting is documented as required outside
  development.
- **The bundled Postgres in the Helm chart.** It is a minimal in-chart
  StatefulSet, documented as evaluation-grade. Use an external database for
  anything real.
- **Anything that requires an already-authenticated agency admin.** That role is
  fully privileged by design.
- Deployment choices such as exposing the app without TLS, or making the database
  reachable from the internet.

If you are unsure whether something is in scope, report it. Deciding is the
maintainer's job, not yours.
