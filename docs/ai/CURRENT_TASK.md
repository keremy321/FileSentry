# Current Task: Audit, Correlation, and Rate-Limit Hardening

## Objective

Add durable security auditing, request/worker correlation, focused upload rate
limiting, and small operational security improvements without changing the core
file workflow.

The completed authentication, ingestion, scanner, file authorization, and health
behavior must remain intact.

## Audit Scope

Persist safe `AuditEvent` records for:

- upload accepted;
- scan started, clean, malware detected, and failed;
- file downloaded;
- file deleted.

Audit fields are limited to event ID, actor/file identifiers where applicable,
event type, UTC timestamp, correlation ID, controlled workflow states, a bounded
failure classification, and scan duration. Audit storage must never accept file
content, credentials, tokens, secrets, arbitrary client objects, or raw malware.

Audit writes participate in the same PostgreSQL unit of work as the associated
state change where feasible. A download is not returned unless its audit write
succeeds. Failures propagate and fail safely rather than silently dropping the
audit event.

## Correlation and Logging

- Accept one syntactically safe `X-Correlation-ID` or generate a new identifier.
- Return the effective correlation ID and use it as the request trace identifier.
- Persist the ingestion correlation ID on the file record so worker scan events
  and structured logs retain durable correlation after restarts.
- Log only structured file IDs, correlation IDs, state transitions, bounded scan
  duration, and controlled failure codes; never log filenames, content, tokens,
  passwords, keys, database secrets, or malware bytes.

## Upload Rate Limit

Apply a startup-validated built-in fixed-window policy to authenticated
`POST /api/v1/files` requests. Partition authenticated uploads by user ID, keep the
existing IP-partitioned authentication limiter unchanged, and return stable
`ProblemDetails` code `UPLOAD_RATE_LIMITED` for rejected uploads.

## Operational Boundaries

- Storage remains outside the web root and is not static content.
- Trusted storage roots and all security-critical options fail validation clearly.
- ClamAV remains loopback-bound in the host development deployment.
- Do not add distributed rate limiting, Redis, OpenTelemetry, or unrelated
  refactoring.

## Tests and Verification

Cover audit fields and safe schema, all required event types, request-to-worker
correlation, normal and excessive upload traffic, authentication precedence, and
continued authentication rate limiting. Run restore, Release build/tests, EF
migration list/update, formatting, diff checks, dependency audit, container health,
and inspect logs/audit rows for sensitive data.

## Out of Scope

- CI or GitHub Actions;
- admin audit APIs or UI;
- public sharing, signed URLs, retention cleanup, dashboards, or OpenTelemetry;
- cloud storage, message brokers, or frontend work.

Do not create a commit or push.

## Verified Completion

Implemented and verified on 2026-08-14. Durable controlled-field audit events,
request and worker correlation, structured workflow logging, and authenticated
per-user upload rate limiting passed the PostgreSQL-backed integration suite. The
existing authentication limiter, ingestion, real ClamAV scanning, authorization,
downloads, deletes, and health behavior remain green.

Release restore/build/tests, migration list/application, formatting, diff checks,
NuGet audit, and local PostgreSQL/ClamAV health checks completed successfully.

Recommended next milestone: `feat/ci-pipeline`.
