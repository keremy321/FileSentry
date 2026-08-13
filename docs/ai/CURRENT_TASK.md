# Current Task: Durable ClamAV Scanner Worker

## Objective

Implement durable asynchronous malware scanning for quarantined `PendingScan`
files. PostgreSQL remains authoritative for claims, attempts, retries, recovery,
and final file state; filesystem changes must follow persisted scan evidence and
never make an inconclusive result downloadable.

The completed infrastructure, authentication, health, and secure-ingestion
behavior must remain intact.

## In Scope

- ClamAV `INSTREAM` over TCP without sending server filesystem paths;
- bounded streaming from quarantine without full-file buffering;
- strict clean, infected, error, timeout, and malformed-response classification;
- safe scanner/version and threat-name metadata where available;
- `ScanAttempt` persistence and an EF Core migration;
- durable PostgreSQL claiming with row locking and `SKIP LOCKED`;
- a hosted background scanner in the existing application;
- `PendingScan -> Scanning -> Clean|Infected|ScanFailed` transitions;
- retryable `ScanFailed -> PendingScan` transitions with bounded exponential backoff;
- recovery of stale `Scanning` claims after termination;
- clean-file promotion from quarantine to the trusted clean root;
- prompt, retryable cleanup of infected bytes while retaining database metadata;
- fail-closed handling for missing files, storage failures, cancellation, and
  unknown scanner behavior;
- unit, PostgreSQL-backed integration, and real-ClamAV clean/EICAR verification.

## Configuration

`ClamAV` supplies the daemon host/port, health timeout, scan timeout, maximum
stream size, and bounded stream chunk size.

`ScannerWorker` supplies startup-validated values for:

- enabled state;
- maximum attempts;
- initial and maximum retry delay;
- stale-claim timeout;
- polling interval.

Retry and stale-claim values must remain bounded, and the stale timeout must be
longer than the configured scan timeout.

## Durable Workflow

- A claim transaction locks one due `PendingScan` row with `FOR UPDATE SKIP LOCKED`,
  marks it `Scanning`, and creates an in-progress `ScanAttempt` before network I/O.
- Completed scanner evidence is persisted before filesystem finalization so a
  restart can safely finish clean promotion or infected deletion.
- Exact `stream: OK` is the only clean classification.
- `FOUND` is infected and is never retried as a scan failure.
- Connection errors, timeouts, scanner errors, malformed/unknown responses, and
  scanner size failures are `ScanFailed`; retryable failures use bounded backoff.
- Host cancellation leaves the durable claim for stale recovery rather than
  falsely completing an attempt.
- Exhausted or non-retryable failures remain `ScanFailed` with quarantined bytes.
- Clean state requires a completed clean result and content in the clean root.
- Infected state is non-downloadable; deletion failures retain an infected state
  and are retried as byte cleanup, not as malware scans.

## Out of Scope

- file list, metadata, download, or delete endpoints;
- ownership authorization for file reads or mutations;
- `AuditEvent`, administration, frontend, cloud storage, or a message broker;
- any distributed worker system or deployment change.

## Verification

Run restore, Release build/tests, EF migration listing/update, formatting, diff
checks, and the NuGet vulnerability audit. Verify PostgreSQL and ClamAV container
health, real clean and assembled-EICAR scans, fail-closed ClamAV outage behavior,
retry exhaustion, stale recovery, durable attempts, clean promotion, and infected
byte deletion.

Do not create a commit or push.

## Verified Completion

Implemented and verified on 2026-08-13. Release restore/build/tests, the EF
migration list and database update, formatting, diff checks, dependency audit,
healthy PostgreSQL/ClamAV containers, real clean/EICAR scanning, outage failure,
retry exhaustion, stale recovery, file promotion, and infected-byte deletion all
completed successfully.

Recommended next milestone: `feat/file-authorization-endpoints`.
