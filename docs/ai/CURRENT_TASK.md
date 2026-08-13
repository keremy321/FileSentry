# Current Task: Owner-Protected File Access APIs

## Objective

Implement authenticated, owner-scoped list, metadata, clean download, and delete
endpoints for existing file records. Ownership must be part of each database query,
and requests for another user's file must be indistinguishable from missing records.

The completed authentication, ingestion, health, and durable scanner behavior must
remain intact.

## Endpoints

- `GET /api/v1/files` returns only caller-owned safe metadata in deterministic
  newest-first order.
- `GET /api/v1/files/{fileId}` returns safe metadata for a caller-owned record.
- `GET /api/v1/files/{fileId}/download` streams only caller-owned `Clean` content
  from the trusted clean root, with the verified media type, a safe filename, and
  `X-Content-Type-Options: nosniff`.
- `DELETE /api/v1/files/{fileId}` removes stored bytes and transitions a
  caller-owned record to `Deleted` without allowing scanner completion to revive it.

All endpoints require JWT authentication.

## Security and Concurrency

- Every single-record lookup includes both authenticated owner ID and file ID.
- Cross-owner and nonexistent records both return `404 Not Found`.
- Internal storage names, paths, scan attempts, and Identity internals are never
  returned in user DTOs.
- `PendingScan`, `Scanning`, `Infected`, `ScanFailed`, and `Deleted` records are not
  downloadable and return a stable `FILE_NOT_CLEAN` problem where applicable.
- Downloads use only the generated storage name under the trusted clean root and
  stream through the framework without whole-file buffering.
- Database row locking and guarded scanner transitions prevent `Deleted -> Clean`
  and promotion after deletion.
- Deletion is idempotent at the storage boundary and fails safely when bytes cannot
  be removed; no alternate storage path is exposed.

## Tests

Cover authentication, owner-only listing and metadata, not-found equivalence,
clean downloads, every non-clean state, safe content disposition, `nosniff`, pending
and clean deletion, byte removal, cross-owner deletion, post-delete access, and
scanner/delete concurrency. Existing auth, ingestion, scanning, and health behavior
must remain green.

## Out of Scope

- `AuditEvent`, admin APIs, public sharing, or signed URLs;
- retention cleanup, frontend, cloud storage, or a message broker;
- distributed or multi-worker deployment work.

## Verification

Run restore, Release build/tests, formatting and diff checks, the NuGet
vulnerability audit, container health checks, and manual/integration exercises for
list, metadata, download, and delete. Do not create an EF migration unless the
existing schema cannot support the milestone.

Do not create a commit or push.

## Verified Completion

Implemented and verified on 2026-08-13. Owner-scoped list, metadata, clean download,
and delete behavior passed PostgreSQL-backed HTTP integration tests, including
not-found equivalence, hostile download filenames, missing clean content, all
non-clean states, stored-byte removal, and guarded scanner completion after delete.
Release restore/build/tests, migration application, formatting, diff checks,
dependency audit, and local PostgreSQL/ClamAV health checks completed successfully.

Recommended next milestone: `feat/audit-rate-limit-hardening`.
